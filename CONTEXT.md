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

**Achievement**:
A single unlockable milestone with name, optional description, unlock state, and optional metadata.
_Avoid_: trophy, cheevo
