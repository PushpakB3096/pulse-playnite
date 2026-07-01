# PlayLog Playnite Extension

Playnite plugin that syncs library metadata and sessions to PlayLog.

## Language

**Achievement data**:
Normalized per-game trophy progress stored by PlayLog after server processing.
_Avoid_: SuccessStory JSON, plugin file

**Achievement import**:
Raw achievement payload read from a third-party Playnite plugin and sent to PlayLog before normalization.
_Avoid_: sync payload, plugin blob

**Achievement source**:
Which Playnite achievement plugin produced an import (`successStory` or `playniteAchievements`).
_Avoid_: plugin name, addon id

**Achievement source preference**:
The user-configured setting that determines which achievement plugin the extension reads from during sync. Stored in `PulseSettings.AchievementSourcePreference`. Defaults to `playniteAchievements`. If the chosen source is not installed, no achievement import is sent and the user sees a warning in extension settings.
_Avoid_: active source, detected source

**Achievement**:
A single unlockable milestone with name, optional description, unlock state, and optional metadata.
_Avoid_: trophy, cheevo
