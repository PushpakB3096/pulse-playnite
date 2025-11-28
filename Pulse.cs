using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Pulse
{
    public class Pulse : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IDialogsFactory dialogs;
        private readonly PulseAccountClient client;

        private PulseSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("d1ac11bf-1668-455f-ad91-6fdb334a54c5");

        public Pulse(IPlayniteAPI api) : base(api)
        {
            dialogs = api.Dialogs;
            client = new PulseAccountClient(api);

            Properties = new GenericPluginProperties
            {
                HasSettings = false // we'll flip this when we add real settings
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

                // Show quick info before network call
                dialogs.ShowMessage($"Pulse: Syncing {allGames.Count} games to backend...", "Pulse");

                // Block on async call in this context
                client.SyncGamesAsync(allGames).GetAwaiter().GetResult();

                dialogs.ShowMessage("Pulse: Sync complete.", "Pulse");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Pulse: SyncAllGames failed.");
                dialogs.ShowErrorMessage("Pulse: Sync all games failed.\n" + ex.Message, "Pulse");
            }
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            // Add code to be executed when game is finished installing.
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            // Add code to be executed when game is started running.
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            // Add code to be executed when game is preparing to be started.
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            // Add code to be executed when game is uninstalled.
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Add code to be executed when Playnite is initialized.
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Add code to be executed when Playnite is shutting down.
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // Add code to be executed when library is updated.
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new PulseSettingsView();
        }
    }
}