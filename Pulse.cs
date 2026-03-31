using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace Pulse
{
    public class Pulse : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IDialogsFactory dialogs;
        private readonly PulseAccountClient client;

        private readonly PulseSettingsViewModel settings;
        private readonly List<Guid> gameIdsToUpdate = new List<Guid>();
        private readonly List<Guid> gameIdsToRemove = new List<Guid>();
        private readonly object syncQueueLock = new object();
        private readonly System.Timers.Timer syncTimer;

        private readonly Dictionary<Guid, string> activeSessionByGameId = new Dictionary<Guid, string>();
        private readonly object sessionMapLock = new object();
        private readonly SessionSyncQueue sessionQueue;

        public override Guid Id { get; } = Guid.Parse("d1ac11bf-1668-455f-ad91-6fdb334a54c5");

        public Pulse(IPlayniteAPI api) : base(api)
        {
            dialogs = api.Dialogs;
            client = new PulseAccountClient(api);
            var sessionQueuePath = Path.Combine(GetPluginUserDataPath(), "pulse-session-queue.jsonl");
            sessionQueue = new SessionSyncQueue(sessionQueuePath, client);
            settings = new PulseSettingsViewModel(this);

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

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Pulse: Sync all games",
                Action = _ => SyncAllGames()
            };
        }

        private void SyncAllGames()
        {
            List<Game> allGames;
            try
            {
                allGames = PlayniteApi.Database.Games.ToList();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Pulse: failed to read library for full sync.");
                dialogs.ShowErrorMessage("Pulse: Could not read game library.\n" + ex.Message, "Pulse");
                return;
            }

            var progressResult = dialogs.ActivateGlobalProgress(
                args =>
                {
                    args.Text = $"Pulse: Syncing {allGames.Count} games...";
                    try
                    {
                        client.SyncGamesAsync(allGames, fullLibrarySync: true).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Pulse: SyncAllGames failed inside progress.");
                        throw;
                    }
                },
                new GlobalProgressOptions("Pulse", true) { IsIndeterminate = true });

            if (progressResult.Error != null)
            {
                logger.Error(progressResult.Error, "Pulse: Sync all games failed.");
                dialogs.ShowErrorMessage(
                    "Pulse: Sync all games failed.\n" + progressResult.Error.Message,
                    "Pulse");
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
                        args.Text = "Pulse: Syncing pending library changes...";
                        try
                        {
                            ExecutePendingSyncWork(toRemove, toUpdate);
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Pulse: shutdown sync failed inside progress.");
                            throw;
                        }
                    },
                    new GlobalProgressOptions("Pulse", true) { IsIndeterminate = true });

                if (progressResult.Error != null)
                {
                    logger.Error(progressResult.Error, "Pulse: pending sync on shutdown failed.");
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
                    logger.Error(ex, "Pulse: background library sync failed.");
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
                    client.SyncGamesAsync(games, fullLibrarySync: false).GetAwaiter().GetResult();
                }
            }
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
                logger.Error(ex, "Pulse: OnGameStarted session tracking failed.");
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
                        logger.Warn("Pulse: OnGameStopped without matching session start (restart mid-session?).");
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
                logger.Error(ex, "Pulse: OnGameStopped session tracking failed.");
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
                logger.Error(ex, "Pulse: session queue drain on startup failed.");
            }
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
                logger.Error(ex, "Pulse: session queue drain on shutdown failed.");
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
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
