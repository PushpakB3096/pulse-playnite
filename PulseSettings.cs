using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Pulse
{
    public class PulseSettings : ObservableObject
    {
        private bool autoSyncLibrary = true;

        /// <summary>
        /// When true, library changes (playtime, adds/removes) sync to PlayLog after a short delay.
        /// </summary>
        public bool AutoSyncLibrary { get => autoSyncLibrary; set => SetValue(ref autoSyncLibrary, value); }

        private string achievementSourcePreference = AchievementExtensionPaths.ImportSourcePlayniteAchievements;

        /// <summary>
        /// Which Playnite achievement plugin to read from: "playniteAchievements" or "successStory".
        /// </summary>
        public string AchievementSourcePreference
        {
            get => achievementSourcePreference;
            set => SetValue(ref achievementSourcePreference, value);
        }

        private string playLogBearerToken = string.Empty;

        /// <summary>
        /// Bearer token from PlayLog pairing (`pl_…`). Required for sync and session APIs.
        /// </summary>
        public string PlayLogBearerToken { get => playLogBearerToken; set => SetValue(ref playLogBearerToken, value); }

        private bool optionThatWontBeSaved = false;
        // Playnite serializes settings object to a JSON object and saves it as text file.
        // If you want to exclude some property from being saved then use `JsonDontSerialize` ignore attribute.
        [DontSerialize]
        public bool OptionThatWontBeSaved { get => optionThatWontBeSaved; set => SetValue(ref optionThatWontBeSaved, value); }
    }

    public class PulseSettingsViewModel : ObservableObject, ISettings
    {
        private readonly Pulse plugin;
        private PulseSettings editingClone { get; set; }
        private DispatcherTimer coverStatusTimer;
        private bool syncPlayniteCoversEnabled;

        private PulseSettings settings;
        public PulseSettings Settings
        {
            get => settings;
            set
            {
                if (settings != null)
                {
                    settings.PropertyChanged -= Settings_PropertyChanged;
                }

                settings = value;

                if (settings != null)
                {
                    settings.PropertyChanged += Settings_PropertyChanged;
                }

                OnPropertyChanged();
                NotifyPlayLogLinkChanged();
            }
        }

        /// <summary>True when a PlayLog device token is stored (pairing completed).</summary>
        public bool IsPlayLogLinked =>
            !string.IsNullOrEmpty((Settings?.PlayLogBearerToken ?? string.Empty).Trim());

        /// <summary>For binding the "not linked" panel without an inverse converter.</summary>
        public bool IsPlayLogNotLinked => !IsPlayLogLinked;

        public bool ShowCoverArtSection => IsPlayLogLinked;

        /// <summary>True when the PA radio button should be checked.</summary>
        public bool IsPlayniteAchievementsSelected
        {
            get => Settings?.AchievementSourcePreference == AchievementExtensionPaths.ImportSourcePlayniteAchievements;
            set
            {
                if (value && Settings != null)
                    Settings.AchievementSourcePreference = AchievementExtensionPaths.ImportSourcePlayniteAchievements;
            }
        }

        /// <summary>True when the SuccessStory radio button should be checked.</summary>
        public bool IsSuccessStorySelected
        {
            get => Settings?.AchievementSourcePreference == AchievementExtensionPaths.ImportSourceSuccessStory;
            set
            {
                if (value && Settings != null)
                    Settings.AchievementSourcePreference = AchievementExtensionPaths.ImportSourceSuccessStory;
            }
        }

        /// <summary>True when the source preference differs from what it was when settings were opened.</summary>
        public bool ShowAchievementSourceChangedWarning =>
            editingClone != null &&
            !string.Equals(
                Settings?.AchievementSourcePreference,
                editingClone.AchievementSourcePreference,
                StringComparison.Ordinal);

        public bool SyncPlayniteCoversEnabled => syncPlayniteCoversEnabled;

        public int CoverUploadPendingCount =>
            plugin?.CoverUploadQueueInstance?.PendingCount ?? 0;

        public bool CoverUploadIsDraining =>
            plugin?.CoverUploadQueueInstance?.IsDraining ?? false;

        public string CoverUploadStatusText
        {
            get
            {
                if (!IsPlayLogLinked)
                {
                    return string.Empty;
                }

                if (!syncPlayniteCoversEnabled)
                {
                    return "PlayLog Plus syncs your Playnite cover art to the mobile app.";
                }

                var pendingCount = CoverUploadPendingCount;
                if (CoverUploadIsDraining)
                {
                    return pendingCount > 0
                        ? "Cover upload: Uploading… (" + pendingCount + " remaining)"
                        : "Cover upload: Uploading…";
                }

                if (pendingCount > 0)
                {
                    return "Cover upload: " + pendingCount + " remaining";
                }

                return "Cover upload: Up to date";
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PulseSettings.PlayLogBearerToken))
            {
                NotifyPlayLogLinkChanged();
            }

            if (e.PropertyName == nameof(PulseSettings.AchievementSourcePreference))
            {
                OnPropertyChanged(nameof(IsPlayniteAchievementsSelected));
                OnPropertyChanged(nameof(IsSuccessStorySelected));
                OnPropertyChanged(nameof(ShowAchievementSourceChangedWarning));
            }
        }

        private void NotifyPlayLogLinkChanged()
        {
            OnPropertyChanged(nameof(IsPlayLogLinked));
            OnPropertyChanged(nameof(IsPlayLogNotLinked));
            OnPropertyChanged(nameof(ShowCoverArtSection));
            NotifyCoverUploadStatusChanged();
        }

        private void NotifyCoverUploadStatusChanged()
        {
            OnPropertyChanged(nameof(CoverUploadPendingCount));
            OnPropertyChanged(nameof(CoverUploadIsDraining));
            OnPropertyChanged(nameof(SyncPlayniteCoversEnabled));
            OnPropertyChanged(nameof(CoverUploadStatusText));
        }

        private async Task RefreshCoverSyncStatusAsync()
        {
            if (!IsPlayLogLinked)
            {
                syncPlayniteCoversEnabled = false;
                NotifyCoverUploadStatusChanged();
                return;
            }

            try
            {
                syncPlayniteCoversEnabled = await plugin.GetSyncPlayniteCoversAsync().ConfigureAwait(false);
            }
            catch
            {
                syncPlayniteCoversEnabled = false;
            }

            NotifyCoverUploadStatusChanged();
        }

        private void StartCoverStatusTimer()
        {
            if (coverStatusTimer != null)
            {
                return;
            }

            coverStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            coverStatusTimer.Tick += (_, __) =>
            {
                NotifyCoverUploadStatusChanged();
                if (CoverUploadPendingCount == 0 && !CoverUploadIsDraining)
                {
                    return;
                }

                _ = RefreshCoverSyncStatusAsync();
            };
            coverStatusTimer.Start();
        }

        private void StopCoverStatusTimer()
        {
            if (coverStatusTimer == null)
            {
                return;
            }

            coverStatusTimer.Stop();
            coverStatusTimer = null;
        }

        public PulseSettingsViewModel(Pulse plugin)
        {
            // Injecting your plugin instance is required for Save/Load method because Playnite saves data to a location based on what plugin requested the operation.
            this.plugin = plugin;

            // Load saved settings.
            var savedSettings = plugin.LoadPluginSettings<PulseSettings>();

            // LoadPluginSettings returns null if no saved data is available.
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new PulseSettings();
            }

        }

        public void BeginEdit()
        {
            // Code executed when settings view is opened and user starts editing values.
            editingClone = Serialization.GetClone(Settings);
            OnPropertyChanged(nameof(ShowAchievementSourceChangedWarning));
            _ = RefreshCoverSyncStatusAsync();
            StartCoverStatusTimer();
        }

        public void CancelEdit()
        {
            // Code executed when user decides to cancel any changes made since BeginEdit was called.
            // This method should revert any changes made since BeginEdit.
            StopCoverStatusTimer();
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // Code executed when user decides to confirm changes made since BeginEdit was called.
            // This method should save settings made since BeginEdit.
            StopCoverStatusTimer();
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            // Code execute when user decides to confirm changes made since BeginEdit was called.
            // Executed before EndEdit is called and EndEdit is not called if false is returned.
            // List of errors is presented to user if verification fails.
            errors = new List<string>();
            return true;
        }

        public async System.Threading.Tasks.Task RunPairingAsync()
        {
            await plugin.RunPairingFlowAsync().ConfigureAwait(false);
            await RefreshCoverSyncStatusAsync().ConfigureAwait(false);
        }
    }
}