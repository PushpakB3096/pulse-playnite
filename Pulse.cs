using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
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

        public override Guid Id { get; } = Guid.Parse("d1ac11bf-1668-455f-ad91-6fdb334a54c5");

        public Pulse(IPlayniteAPI api) : base(api)
        {
            dialogs = api.Dialogs;
            client = new PulseAccountClient(api);
            settings = new PulseSettingsViewModel(this);

            PlayniteApi.Database.Games.ItemUpdated += Games_ItemUpdated;
            PlayniteApi.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;

            syncTimer = new System.Timers.Timer(5000);
            syncTimer.AutoReset = false;
            syncTimer.Elapsed += (_, __) => FlushPendingSyncQueues();

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
            try
            {
                List<Game> allGames = PlayniteApi.Database.Games.ToList();

                dialogs.ShowMessage($"Pulse: Syncing {allGames.Count} games to backend...", "Pulse");

                client.SyncGamesAsync(allGames).GetAwaiter().GetResult();

                dialogs.ShowMessage("Pulse: Sync complete.", "Pulse");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Pulse: SyncAllGames failed.");
                dialogs.ShowErrorMessage("Pulse: Sync all games failed.\n" + ex.Message, "Pulse");
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

        private void FlushPendingSyncQueues()
        {
            syncTimer.Stop();

            List<Guid> toRemove;
            List<Guid> toUpdate;

            lock (syncQueueLock)
            {
                toRemove = gameIdsToRemove.Distinct().ToList();
                gameIdsToRemove.Clear();
                toUpdate = gameIdsToUpdate.Distinct().ToList();
                gameIdsToUpdate.Clear();
            }

            if (!settings.Settings.AutoSyncLibrary)
            {
                return;
            }

            var removedSet = new HashSet<Guid>(toRemove);
            toUpdate = toUpdate.Where(id => !removedSet.Contains(id)).ToList();

            try
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
                        client.SyncGamesAsync(games).GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Pulse: background library sync failed.");
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            FlushPendingSyncQueues();
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
