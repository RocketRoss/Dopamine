using Digimezzo.Foundation.Core.Logging;
using Digimezzo.Foundation.Core.Settings;
using Dopamine.Core.Prism;
using Dopamine.Data;
using Dopamine.Data.Entities;
using Dopamine.Services.Entities;
using Dopamine.Services.File;
using Dopamine.Services.Folders;
using Dopamine.Services.Playback;
using Dopamine.ViewModels.Common.Base;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dopamine.ViewModels.FullPlayer.Collection
{
    public class CollectionFoldersViewModel : TracksViewModelBase
    {
        private IFoldersService foldersService;
        private IFileService fileService;
        private IPlaybackService playbackService;
        private IEventAggregator eventAggregator;
        private double leftPaneWidthPercent;
        private ObservableCollection<FolderViewModel> folders;
        private ObservableCollection<SubfolderViewModel> subfolders;
        private FolderViewModel selectedFolder;
        private string activeSubfolderPath;
        private ObservableCollection<SubfolderBreadCrumbViewModel> subfolderBreadCrumbs;
        private int subfolderTrackInclusionDepthLimit;
        private CancellationTokenSource taskCancellationTokenSource;

        public DelegateCommand<string> JumpSubfolderCommand { get; set; }

        public ObservableCollection<SubfolderBreadCrumbViewModel> SubfolderBreadCrumbs
        {
            get { return this.subfolderBreadCrumbs; }
            set { SetProperty<ObservableCollection<SubfolderBreadCrumbViewModel>>(ref this.subfolderBreadCrumbs, value); }
        }

        public double LeftPaneWidthPercent
        {
            get { return this.leftPaneWidthPercent; }
            set
            {
                SetProperty<double>(ref this.leftPaneWidthPercent, value);
                SettingsClient.Set<int>("ColumnWidths", "FoldersLeftPaneWidthPercent", Convert.ToInt32(value));
            }
        }

        public ObservableCollection<FolderViewModel> Folders
        {
            get { return this.folders; }
            set { SetProperty<ObservableCollection<FolderViewModel>>(ref this.folders, value); }
        }

        public ObservableCollection<SubfolderViewModel> Subfolders
        {
            get { return this.subfolders; }
            set { SetProperty<ObservableCollection<SubfolderViewModel>>(ref this.subfolders, value); }
        }

        public FolderViewModel SelectedFolder
        {
            get { return this.selectedFolder; }
            set
            {
                SetProperty<FolderViewModel>(ref this.selectedFolder, value);
                SettingsClient.Set<string>("Selections", "SelectedFolder", value != null ? value.Path : string.Empty);
                this.GetSubfoldersAsync(null);
            }
        }

        public CollectionFoldersViewModel(IContainerProvider container, IFoldersService foldersService, IFileService fileService,
            IPlaybackService playbackService, IEventAggregator eventAggregator) : base(container)
        {
            this.foldersService = foldersService;
            this.fileService = fileService;
            this.playbackService = playbackService;
            this.eventAggregator = eventAggregator;
            this.subfolderTrackInclusionDepthLimit = 100;

            // Commands
            this.JumpSubfolderCommand = new DelegateCommand<string>((subfolderPath) => this.GetSubfoldersAsync(new SubfolderViewModel(subfolderPath, false)));

            // Load settings
            this.LeftPaneWidthPercent = SettingsClient.Get<int>("ColumnWidths", "FoldersLeftPaneWidthPercent");

            // Events
            this.foldersService.FoldersChanged += FoldersService_FoldersChanged;
            this.playbackService.PlaybackFailed += (async(_,__) => await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders));
            this.playbackService.PlaybackPaused += (async (_, __) => await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders));
            this.playbackService.PlaybackResumed += (async (_, __) => await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders));
            this.playbackService.PlaybackSuccess += (async (_, __) => await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders));
            this.playbackService.PlaybackStopped += (async (_, __) => await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders));

            this.eventAggregator.GetEvent<ActiveSubfolderChanged>().Subscribe((activeSubfolder) =>
            {
                this.GetSubfoldersAsync(activeSubfolder as SubfolderViewModel);
            });
        }

        private async void FoldersService_FoldersChanged(object sender, EventArgs e)
        {
            await this.FillListsAsync();
        }

        private void ClearFolders()
        {
            this.folders = null;
            this.Subfolders = null;
            this.SubfolderBreadCrumbs = null;
        }

        private async Task GetFoldersAsync()
        {
            this.Folders = new ObservableCollection<FolderViewModel>(await this.foldersService.GetFoldersAsync());
            FolderViewModel proposedSelectedFolder = await this.foldersService.GetSelectedFolderAsync();
            this.selectedFolder = this.Folders.Where(x => x.Equals(proposedSelectedFolder)).FirstOrDefault();
            this.RaisePropertyChanged(nameof(this.SelectedFolder));
        }

        private async Task GetSubfoldersAsync(SubfolderViewModel activeSubfolder)
        {
            this.Subfolders = null; // Required to correctly reset the selectedSubfolder
            this.SubfolderBreadCrumbs = null;
            this.activeSubfolderPath = string.Empty;

            if (this.selectedFolder != null)
            {
                this.Subfolders = new ObservableCollection<SubfolderViewModel>(await this.foldersService.GetSubfoldersAsync(this.selectedFolder, activeSubfolder));
                this.activeSubfolderPath = this.subfolders.Count > 0 && this.subfolders.Any(x => x.IsGoToParent) ? this.subfolders.Where(x => x.IsGoToParent).First().Path : this.selectedFolder.Path;
                this.SubfolderBreadCrumbs = new ObservableCollection<SubfolderBreadCrumbViewModel>(this.foldersService.GetSubfolderBreadCrumbs(this.selectedFolder, this.activeSubfolderPath));

                if (taskCancellationTokenSource != null)
                {
                    taskCancellationTokenSource.Cancel();
                }
                taskCancellationTokenSource = new CancellationTokenSource();

                await this.GetTracksAsync(taskCancellationTokenSource.Token);
                await this.foldersService.SetPlayingSubFolderAsync(this.Subfolders);
            }
        }

        private async Task GetTracksAsync(CancellationToken TaskCancellationToken)
        {
            try
            {
                IList<TrackViewModel> tracks = await GetFolderTracksAsync(this.activeSubfolderPath);
                await this.GetTracksCommonAsync(tracks, TrackOrder.None);
                TaskCancellationToken.ThrowIfCancellationRequested();

                int subfolderDepthLimit = this.subfolderTrackInclusionDepthLimit;
                int subfolderDepthIncrement = (subfolderDepthLimit == -1 ? 0 : 1);

                ObservableCollection<SubfolderViewModel> subfolders = this.Subfolders;

                for (int subfolderDepthCounter = 0; subfolderDepthCounter != subfolderDepthLimit; subfolderDepthCounter += subfolderDepthIncrement)
                {
                    TaskCancellationToken.ThrowIfCancellationRequested();
                    ObservableCollection<SubfolderViewModel> subSubfolders = new ObservableCollection<SubfolderViewModel>();

                    foreach (SubfolderViewModel subfolder in subfolders)
                    {
                        if(subfolder.IsGoToParent)
                        {
                            continue;
                        }
                        tracks = tracks.Concat(await GetFolderTracksAsync(subfolder.Path)).ToList();

                        FolderViewModel subfolderAsFolder = new FolderViewModel( new Folder { Path = subfolder.Path, SafePath = subfolder.SafePath, ShowInCollection = 0 });
                        IList<SubfolderViewModel> localSubSubfolders = await this.foldersService.GetSubfoldersAsync(subfolderAsFolder, null);

                        subSubfolders = new ObservableCollection<SubfolderViewModel> (subSubfolders.Concat(localSubSubfolders));
                        TaskCancellationToken.ThrowIfCancellationRequested();
                    }
                    await this.GetTracksCommonAsync(tracks, TrackOrder.None);
                    subfolders = subSubfolders;

                    if (subfolders.Count == 0)
                    {
                        break;
                    }
                }
                //await this.GetTracksCommonAsync(tracks, TrackOrder.None);

            }
            catch(OperationCanceledException)
            {
                LogClient.Info("Folder changed while awaiting collection of tracks for previously active folder.");
            }
        }

        private async Task<IList<TrackViewModel>> GetFolderTracksAsync(string FolderPath)
        {
            return await this.fileService.ProcessFilesInDirectoryAsync(FolderPath);
        }

        protected async override Task FillListsAsync()
        {
            await this.GetFoldersAsync();
            await this.GetSubfoldersAsync(null);
        }

        protected async override Task EmptyListsAsync()
        {
            this.ClearFolders();
            this.ClearTracks();
        }
    }
}
