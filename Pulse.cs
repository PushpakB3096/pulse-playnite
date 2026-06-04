using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Pulse
{
    public class Pulse : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IDialogsFactory dialogs;
        private readonly PulseAccountClient client;
        private readonly GameActivitySessionImporter gaImporter;

        private readonly PulseSettingsViewModel settings;
        private readonly List<Guid> gameIdsToUpdate = new List<Guid>();
        private readonly List<Guid> gameIdsToRemove = new List<Guid>();
        private readonly object syncQueueLock = new object();
        private readonly System.Timers.Timer syncTimer;

        private readonly Dictionary<Guid, string> activeSessionByGameId = new Dictionary<Guid, string>();
        private readonly object sessionMapLock = new object();
        private readonly SessionSyncQueue sessionQueue;
        private readonly CoverUploadQueue coverUploadQueue;
        private readonly CoverSyncStateStore coverSyncStateStore;
        private readonly System.Timers.Timer coverDrainTimer;
        private readonly System.Timers.Timer statusIdlePollTimer;
        private readonly HashSet<Guid> recentlyPushedFromPlayLog = new HashSet<Guid>();
        private readonly object recentlyPushedLock = new object();
        private System.Timers.Timer recentlyPushedClearTimer;

        private const int RecentlyPushedClearMs = 15000;
        private const int StatusIdlePollIntervalMs = 30000;

        public override Guid Id { get; } = Guid.Parse("d1ac11bf-1668-455f-ad91-6fdb334a54c5");

        public CoverUploadQueue CoverUploadQueueInstance => coverUploadQueue;

        public Pulse(IPlayniteAPI api) : base(api)
        {
            dialogs = api.Dialogs;
            settings = new PulseSettingsViewModel(this);
            var coverSyncStatePath = Path.Combine(GetPluginUserDataPath(), "cover-sync-state.json");
            coverSyncStateStore = new CoverSyncStateStore(coverSyncStatePath);
            client = new PulseAccountClient(
                api,
                () => settings.Settings.PlayLogBearerToken?.Trim() ?? string.Empty,
                coverSyncStateStore);
            gaImporter = new GameActivitySessionImporter(
                api,
                client,
                () => settings.Settings.PlayLogBearerToken?.Trim() ?? string.Empty);
            var sessionQueuePath = Path.Combine(GetPluginUserDataPath(), "pulse-session-queue.jsonl");
            sessionQueue = new SessionSyncQueue(
                sessionQueuePath,
                client,
                () => settings.Settings.PlayLogBearerToken?.Trim() ?? string.Empty);
            var coverQueuePath = Path.Combine(GetPluginUserDataPath(), "pulse-cover-upload-queue.jsonl");
            coverUploadQueue = new CoverUploadQueue(
                coverQueuePath,
                client,
                () => settings.Settings.PlayLogBearerToken?.Trim() ?? string.Empty);
            coverDrainTimer = new System.Timers.Timer(60000);
            coverDrainTimer.AutoReset = true;
            coverDrainTimer.Elapsed += (_, __) => KickCoverUploadDrain();
            coverDrainTimer.Start();
            PlayniteApi.Database.Games.ItemUpdated += Games_ItemUpdated;
            PlayniteApi.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;

            syncTimer = new System.Timers.Timer(5000);
            syncTimer.AutoReset = false;
            syncTimer.Elapsed += (_, __) => FlushPendingSyncQueues(showShutdownProgress: false);

            statusIdlePollTimer = new System.Timers.Timer(StatusIdlePollIntervalMs);
            statusIdlePollTimer.AutoReset = true;
            statusIdlePollTimer.Elapsed += (_, __) => ApplyPendingStatusUpdatesFromServerSafe();

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        public async Task RunPairingFlowAsync()
        {
            try
            {
                var start = await client.StartPairingAsync().ConfigureAwait(false);
                if (start == null || string.IsNullOrEmpty(start.PairingId))
                {
                    dialogs.ShowErrorMessage("PlayLog: Could not start pairing.", "PlayLog");
                    return;
                }

                dialogs.ShowMessage(
                    "Enter this code in the PlayLog app on your phone (signed in):\n\n" + start.UserCode,
                    "PlayLog pairing");

                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < deadline)
                {
                    var st = await client.GetPairingStatusAsync(start.PairingId).ConfigureAwait(false);
                    if (st != null
                        && string.Equals(st.Status, "completed", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(st.PluginToken))
                    {
                        var token = st.PluginToken;
                        void ApplySuccessOnUi()
                        {
                            settings.Settings.PlayLogBearerToken = token;
                            SavePluginSettings(settings.Settings);
                            dialogs.ShowMessage("PlayLog linked successfully.", "PlayLog");
                        }

                        if (Application.Current?.Dispatcher != null
                            && !Application.Current.Dispatcher.CheckAccess())
                        {
                            Application.Current.Dispatcher.Invoke(ApplySuccessOnUi);
                        }
                        else
                        {
                            ApplySuccessOnUi();
                        }

                        return;
                    }

                    await Task.Delay(2000).ConfigureAwait(false);
                }

                dialogs.ShowErrorMessage("PlayLog: Pairing timed out.", "PlayLog");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: pairing flow failed.");
                dialogs.ShowErrorMessage("PlayLog: Pairing failed.\n" + ex.Message, "PlayLog");
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "PlayLog: Sync all games",
                Action = _ => SyncAllGames()
            };
        }

        private void SyncAllGames()
        {
            if (!settings.IsPlayLogLinked)
            {
                logger.Info("PlayLog: skip library sync — not linked");
                dialogs.ShowMessage(
                    "Link PlayLog in extension settings before syncing your library.",
                    "PlayLog");
                return;
            }

            List<Game> allGames;
            try
            {
                allGames = PlayniteApi.Database.Games.ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: failed to read library for full sync.");
                dialogs.ShowErrorMessage("PlayLog: Could not read game library.\n" + ex.Message, "PlayLog");
                return;
            }

            var progressResult = dialogs.ActivateGlobalProgress(
                args =>
                {
                    try
                    {
                        var reportProgress = new Action<LibrarySyncProgress>(
                            progress => ApplyLibrarySyncProgress(args, progress));
                        var coversNeedingUpload = RunLibraryMetadataSync(
                            allGames,
                            fullLibrarySync: true,
                            reportProgress);
                        FinishLibrarySyncWithCoverUpload(coversNeedingUpload);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "PlayLog: SyncAllGames failed inside progress.");
                        throw;
                    }
                },
                new GlobalProgressOptions("PlayLog", false) { IsIndeterminate = false });

            if (progressResult.Error != null)
            {
                logger.Error(progressResult.Error, "PlayLog: Sync all games failed.");
                dialogs.ShowErrorMessage(
                    "PlayLog: Sync all games failed.\n" + progressResult.Error.Message,
                    "PlayLog");
            }
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> args)
        {
            if (!settings.Settings.AutoSyncLibrary)
            {
                return;
            }

            if (args.UpdatedItems == null || args.UpdatedItems.Count == 0)
            {
                return;
            }

            foreach (var item in args.UpdatedItems)
            {
                var game = item.NewData;
                if (game != null)
                {
                    lock (recentlyPushedLock)
                    {
                        if (recentlyPushedFromPlayLog.Contains(game.Id))
                        {
                            continue;
                        }
                    }

                    lock (syncQueueLock)
                    {
                        gameIdsToUpdate.Add(game.Id);
                    }
                }
            }

            RestartSyncTimer();
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> args)
        {
            if (!settings.Settings.AutoSyncLibrary)
            {
                return;
            }

            if (args.AddedItems != null && args.AddedItems.Count > 0)
            {
                lock (syncQueueLock)
                {
                    foreach (var g in args.AddedItems)
                    {
                        if (g != null)
                        {
                            gameIdsToUpdate.Add(g.Id);
                        }
                    }
                }
            }

            if (args.RemovedItems != null && args.RemovedItems.Count > 0)
            {
                lock (syncQueueLock)
                {
                    foreach (var g in args.RemovedItems)
                    {
                        if (g != null)
                        {
                            gameIdsToRemove.Add(g.Id);
                        }
                    }
                }
            }

            RestartSyncTimer();
        }

        private void RestartSyncTimer()
        {
            syncTimer.Stop();
            syncTimer.Start();
        }

        /// <summary>
        /// Drains pending remove/update queues when auto-sync is enabled; otherwise leaves queues intact.
        /// </summary>
        private void FlushPendingSyncQueues(bool showShutdownProgress)
        {
            syncTimer.Stop();

            if (!settings.Settings.AutoSyncLibrary)
            {
                return;
            }

            if (!settings.IsPlayLogLinked)
            {
                logger.Info("PlayLog: skip library sync — not linked");
                return;
            }

            List<Guid> toRemove;
            List<Guid> toUpdate;

            lock (syncQueueLock)
            {
                toRemove = gameIdsToRemove.Distinct().ToList();
                gameIdsToRemove.Clear();
                toUpdate = gameIdsToUpdate.Distinct().ToList();
                gameIdsToUpdate.Clear();
            }

            var removedSet = new HashSet<Guid>(toRemove);
            toUpdate = toUpdate.Where(id => !removedSet.Contains(id)).ToList();

            if (toRemove.Count == 0 && toUpdate.Count == 0)
            {
                return;
            }

            if (showShutdownProgress)
            {
                var progressResult = dialogs.ActivateGlobalProgress(
                    args =>
                    {
                        try
                        {
                            var reportProgress = new Action<LibrarySyncProgress>(
                                progress => ApplyLibrarySyncProgress(args, progress));
                            ExecutePendingSyncWork(toRemove, toUpdate, reportProgress);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "PlayLog: shutdown sync failed inside progress.");
                            throw;
                        }
                    },
                    new GlobalProgressOptions("PlayLog", false) { IsIndeterminate = false });

                if (progressResult.Error != null)
                {
                    logger.Error(progressResult.Error, "PlayLog: pending sync on shutdown failed.");
                }
            }
            else
            {
                try
                {
                    ExecutePendingSyncWork(toRemove, toUpdate);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "PlayLog: background library sync failed.");
                }
            }
        }

        private void ExecutePendingSyncWork(
            List<Guid> toRemove,
            List<Guid> toUpdate,
            Action<LibrarySyncProgress> onProgress = null)
        {
            if (toRemove.Count > 0)
            {
                onProgress?.Invoke(new LibrarySyncProgress
                {
                    Phase = LibrarySyncPhase.Deleting,
                    DeleteCount = toRemove.Count
                });

                var playniteIds = toRemove.Select(id => id.ToString()).ToList();
                client.DeleteGamesByPlayniteIdsAsync(playniteIds).GetAwaiter().GetResult();
            }

            if (toUpdate.Count > 0)
            {
                var games = new List<Game>();
                foreach (var id in toUpdate)
                {
                    var g = PlayniteApi.Database.Games.Get(id);
                    if (g != null)
                    {
                        games.Add(g);
                    }
                }

                if (games.Count > 0)
                {
                    var coversNeedingUpload = RunLibraryMetadataSync(
                        games,
                        fullLibrarySync: false,
                        onProgress);
                    FinishLibrarySyncWithCoverUpload(coversNeedingUpload);
                }
            }

            ApplyPendingStatusUpdatesFromServer();
        }

        private void ApplyPendingStatusUpdatesFromServerSafe()
        {
            if (!settings.IsPlayLogLinked)
            {
                return;
            }

            try
            {
                ApplyPendingStatusUpdatesFromServer();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: idle status push poll failed.");
            }
        }

        private void ApplyPendingStatusUpdatesFromServer()
        {
            if (!settings.IsPlayLogLinked)
            {
                return;
            }

            IReadOnlyList<PulseAccountClient.PlayniteStatusPendingDto> pending;
            try
            {
                pending = client.GetPlayniteStatusPendingAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: fetch pending status updates failed.");
                return;
            }

            if (pending == null || pending.Count == 0)
            {
                return;
            }

            var ackPlayniteIds = new List<string>();

            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var row in pending)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.PlayniteId))
                    {
                        continue;
                    }

                    if (!Guid.TryParse(row.PlayniteId.Trim(), out var gameGuid))
                    {
                        continue;
                    }

                    var game = PlayniteApi.Database.Games.Get(gameGuid);
                    if (game == null)
                    {
                        continue;
                    }

                    var statusName = row.TargetCompletionStatusName?.Trim();
                    if (string.IsNullOrEmpty(statusName))
                    {
                        continue;
                    }

                    var statusId = ResolveCompletionStatusId(statusName);
                    if (statusId == Guid.Empty)
                    {
                        logger.Warn(
                            "PlayLog: no Playnite completion status named \"" + statusName + "\".");
                        continue;
                    }

                    if (game.CompletionStatusId == statusId)
                    {
                        ackPlayniteIds.Add(row.PlayniteId.Trim());
                        continue;
                    }

                    game.CompletionStatusId = statusId;
                    PlayniteApi.Database.Games.Update(game);
                    MarkRecentlyPushedFromPlayLog(gameGuid);
                    ackPlayniteIds.Add(row.PlayniteId.Trim());
                }
            }

            if (ackPlayniteIds.Count == 0)
            {
                return;
            }

            try
            {
                client.AckPlayniteStatusPendingAsync(ackPlayniteIds).GetAwaiter().GetResult();
                logger.Info(
                    "PlayLog: applied " + ackPlayniteIds.Count + " pending status update(s) from PlayLog.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: ack pending status updates failed.");
            }
        }

        private Guid ResolveCompletionStatusId(string statusName)
        {
            if (string.IsNullOrWhiteSpace(statusName))
            {
                return Guid.Empty;
            }

            foreach (var completionStatus in PlayniteApi.Database.CompletionStatuses)
            {
                if (completionStatus == null)
                {
                    continue;
                }

                if (string.Equals(
                        completionStatus.Name,
                        statusName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return completionStatus.Id;
                }
            }

            return Guid.Empty;
        }

        private void MarkRecentlyPushedFromPlayLog(Guid gameId)
        {
            lock (recentlyPushedLock)
            {
                recentlyPushedFromPlayLog.Add(gameId);
            }

            recentlyPushedClearTimer?.Stop();
            recentlyPushedClearTimer?.Dispose();
            recentlyPushedClearTimer = new System.Timers.Timer(RecentlyPushedClearMs)
            {
                AutoReset = false
            };
            recentlyPushedClearTimer.Elapsed += (_, __) => ClearRecentlyPushedFromPlayLog();
            recentlyPushedClearTimer.Start();
        }

        private void ClearRecentlyPushedFromPlayLog()
        {
            lock (recentlyPushedLock)
            {
                recentlyPushedFromPlayLog.Clear();
            }
        }

        private static void ApplyLibrarySyncProgress(
            GlobalProgressActionArgs args,
            LibrarySyncProgress progress)
        {
            if (args == null || progress == null)
            {
                return;
            }

            switch (progress.Phase)
            {
                case LibrarySyncPhase.CheckingSettings:
                    args.Text = "PlayLog: Checking account…";
                    break;
                case LibrarySyncPhase.PreparingBatch:
                    args.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "PlayLog: Preparing games {0}–{1} of {2}…",
                        progress.BatchRangeStart,
                        progress.BatchRangeEnd,
                        progress.GamesTotal);
                    break;
                case LibrarySyncPhase.UploadingBatch:
                    args.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "PlayLog: Uploading batch {0} of {1} ({2}/{3})…",
                        progress.BatchIndex,
                        progress.BatchCount,
                        progress.GamesDone,
                        progress.GamesTotal);
                    break;
                case LibrarySyncPhase.Finalizing:
                    args.Text = "PlayLog: Finalizing library…";
                    break;
                case LibrarySyncPhase.Deleting:
                    args.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "PlayLog: Removing {0} games…",
                        progress.DeleteCount);
                    break;
            }

            if (progress.Phase == LibrarySyncPhase.Deleting || progress.GamesTotal <= 0)
            {
                return;
            }

            args.ProgressMaxValue = progress.GamesTotal;
            args.CurrentProgressValue = Math.Min(progress.GamesDone, progress.GamesTotal);
        }

        private IReadOnlyList<string> RunLibraryMetadataSync(
            IEnumerable<Game> games,
            bool fullLibrarySync,
            Action<LibrarySyncProgress> onProgress = null)
        {
            var gameList = games != null ? games.ToList() : new List<Game>();
            var gamesTotal = gameList.Count;

            onProgress?.Invoke(new LibrarySyncProgress
            {
                Phase = LibrarySyncPhase.CheckingSettings,
                GamesDone = 0,
                GamesTotal = gamesTotal
            });

            var syncPlayniteCovers = client.GetSyncPlayniteCoversAsync().GetAwaiter().GetResult();
            client.SetIncludePlayniteCoversInSync(syncPlayniteCovers);
            return client.SyncGamesAsync(gameList, fullLibrarySync, onProgress).GetAwaiter().GetResult();
        }

        private void FinishLibrarySyncWithCoverUpload(IReadOnlyList<string> coversNeedingUpload)
        {
            if (coversNeedingUpload == null || coversNeedingUpload.Count == 0)
            {
                KickCoverUploadDrain();
                return;
            }

            foreach (var playniteId in coversNeedingUpload)
            {
                if (string.IsNullOrWhiteSpace(playniteId))
                {
                    continue;
                }

                var metadata = coverSyncStateStore.GetUploadMetadata(playniteId);
                if (metadata == null)
                {
                    Guid gameGuid;
                    if (!Guid.TryParse(playniteId, out gameGuid))
                    {
                        continue;
                    }

                    var game = PlayniteApi.Database.Games.Get(gameGuid);
                    if (game == null)
                    {
                        continue;
                    }

                    metadata = PlayniteCoverReader.TryReadForSync(PlayniteApi, game, coverSyncStateStore);
                }

                coverUploadQueue.Enqueue(metadata, playniteId);
            }

            KickCoverUploadDrain();
        }

        private void KickCoverUploadDrain()
        {
            if (!settings.IsPlayLogLinked)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    coverUploadQueue.TryDrainAll();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "PlayLog: cover upload queue drain failed.");
                }
            });
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (args?.Game == null)
            {
                return;
            }

            try
            {
                string oldSessionId;
                string newSessionId;
                lock (sessionMapLock)
                {
                    activeSessionByGameId.TryGetValue(args.Game.Id, out oldSessionId);
                    newSessionId = Guid.NewGuid().ToString("D");
                    activeSessionByGameId[args.Game.Id] = newSessionId;
                }

                var startUtc = DateTime.UtcNow;
                var playniteIdStr = args.Game.Id.ToString();
                if (!string.IsNullOrEmpty(oldSessionId))
                {
                    sessionQueue.EnqueueStop(oldSessionId, playniteIdStr, startUtc);
                }

                sessionQueue.EnqueueStart(newSessionId, playniteIdStr, startUtc);
                sessionQueue.TryDrainAll();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: OnGameStarted session tracking failed.");
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (args?.Game == null)
            {
                return;
            }

            try
            {
                string clientSessionId;
                lock (sessionMapLock)
                {
                    if (!activeSessionByGameId.TryGetValue(args.Game.Id, out clientSessionId))
                    {
                        logger.Warn("PlayLog: OnGameStopped without matching session start (restart mid-session?).");
                        return;
                    }

                    activeSessionByGameId.Remove(args.Game.Id);
                }

                var endUtc = DateTime.UtcNow;
                sessionQueue.EnqueueStop(clientSessionId, args.Game.Id.ToString(), endUtc);
                sessionQueue.TryDrainAll();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: OnGameStopped session tracking failed.");
            }
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            try
            {
                sessionQueue.TryDrainAll();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: session queue drain on startup failed.");
            }

            KickCoverUploadDrain();

            if (settings.IsPlayLogLinked)
            {
                statusIdlePollTimer.Start();
                ApplyPendingStatusUpdatesFromServerSafe();
            }

            _ = gaImporter.RunAsync();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            statusIdlePollTimer.Stop();
            recentlyPushedClearTimer?.Stop();
            FlushPendingSyncQueues(showShutdownProgress: true);
            try
            {
                sessionQueue.TryDrainAll();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "PlayLog: session queue drain on shutdown failed.");
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
        }

        public async Task<bool> GetSyncPlayniteCoversAsync(bool forceRefresh = false)
        {
            return await client.GetSyncPlayniteCoversAsync(forceRefresh).ConfigureAwait(false);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            var view = new PulseSettingsView();
            view.DataContext = settings;
            return view;
        }
    }
}
