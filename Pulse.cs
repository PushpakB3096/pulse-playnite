using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
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
                    args.Text = $"PlayLog: Syncing {allGames.Count} games...";
                    try
                    {
                        var coversNeedingUpload = RunLibraryMetadataSync(allGames, fullLibrarySync: true);
                        FinishLibrarySyncWithCoverUpload(coversNeedingUpload);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "PlayLog: SyncAllGames failed inside progress.");
                        throw;
                    }
                },
                new GlobalProgressOptions("PlayLog", true) { IsIndeterminate = true });

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
                        args.Text = "PlayLog: Syncing pending library changes...";
                        try
                        {
                            ExecutePendingSyncWork(toRemove, toUpdate);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "PlayLog: shutdown sync failed inside progress.");
                            throw;
                        }
                    },
                    new GlobalProgressOptions("PlayLog", true) { IsIndeterminate = true });

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

        private void ExecutePendingSyncWork(List<Guid> toRemove, List<Guid> toUpdate)
        {
            if (toRemove.Count > 0)
            {
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
                    var coversNeedingUpload = RunLibraryMetadataSync(games, fullLibrarySync: false);
                    FinishLibrarySyncWithCoverUpload(coversNeedingUpload);
                }
            }
        }

        private IReadOnlyList<string> RunLibraryMetadataSync(IEnumerable<Game> games, bool fullLibrarySync)
        {
            var syncPlayniteCovers = client.GetSyncPlayniteCoversAsync().GetAwaiter().GetResult();
            client.SetIncludePlayniteCoversInSync(syncPlayniteCovers);
            return client.SyncGamesAsync(games, fullLibrarySync).GetAwaiter().GetResult();
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

            _ = gaImporter.RunAsync();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
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
