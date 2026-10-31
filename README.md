# PlayLog for Playnite

A [Playnite](https://playnite.link/) extension that connects your Playnite library to [PlayLog](https://www.playlog.online/).

## About PlayLog

PlayLog is a home for your game library, on the web, Android and iOS. You can browse and search everything you own, sort games onto shelves (Playing, Backlog, Finished, and your own playlists), track play status, see playtime and session stats, and discover new games.

Your library reaches PlayLog through integrations, and Playnite is one of them. Install this extension and link it to your account, and PlayLog keeps your Playnite library, playtime and play sessions in sync. Changes you make in PlayLog flow back to Playnite.

## What it syncs

- **Library:** games, playtime, new games and removals, automatically after Playnite updates your library, or all at once with **PlayLog: Sync all games** in the main menu
- **Play sessions:** each session you play, reported as it ends
- **Cover art:** your Playnite covers, uploaded in the background
- **Achievements:** from SuccessStory or PlayniteAchievements (pick one in settings)
- **Filter presets:** your saved Playnite filters
- **Edits from PlayLog:** changes made in PlayLog, such as status, favorites and playtime, are applied back to Playnite

## Install and link

1. Install the extension in either of these ways:
   - In Playnite, open the add-on browser, search for **PlayLog** and install it.
   - Or download the latest `.pext` from [Releases](https://github.com/PushpakB3096/pulse-playnite/releases) and open it with Playnite.
2. Open the PlayLog extension settings in Playnite and choose **Link PlayLog account**.
3. Enter the short code shown there in the PlayLog app on your phone (signed in).

One PC can be linked to a PlayLog account at a time.

## Releases

Every push to `master` builds the extension and creates a **draft** GitHub Release named after the version in `extension.yaml`. The run fails if that version was already released, so pushes that aren't releases use `[skip ci]` in the commit message.

To ship a release:

1. Bump the version in `extension.yaml` and `Properties/AssemblyInfo.cs` (`AssemblyVersion`, `AssemblyFileVersion`).
2. Add a `Packages` entry for the new version, with a changelog, at the top of `packaging/installer.yaml`.
3. Push to `master` and wait for the release workflow to pass.
4. Publish the draft release on GitHub. Playnite's add-on installer reads `packaging/installer.yaml`, which points at the release's `.pext`.

## License

Private project.
