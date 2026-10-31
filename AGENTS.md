# pulse-playnite (PlayLog Playnite extension)

Playnite plugin that syncs library, playtime, sessions, covers, achievements and filter presets to PlayLog (`pulse-server`) and applies PlayLog edits back to Playnite. Sibling repos are `pulse-app`, `playlog` and `pulse-design`. Edit only this repo.

## Verify

The project doesn't build outside Windows, so CI is the only compile check. After every push, check the GitHub Actions run and fix failures until it's green. These are the usual causes:

- **New `.cs` files must be added to `Pulse.csproj`** (`<Compile Include>`). It's an old-style project; unlisted files are silently left out of the build.
- **C# 7.3 only** (.NET Framework 4.8, no `LangVersion`): no switch expressions, records, `is not`, target-typed `new()` or nullable reference types.
- **Match Playnite SDK signatures exactly.** For example, `ReleaseDate` constructors take `int`, not `int?`; use the year, year+month or year+month+day overload.

## Releases

Every push to `master` runs the release workflow, which fails if the tag for `extension.yaml`'s `Version` already exists.

- A push that isn't a release (docs, config) must have `[skip ci]` in the commit message.
- A release bumps these together: `extension.yaml` `Version`, `AssemblyVersion` and `AssemblyFileVersion` in `Properties/AssemblyInfo.cs`, and a new `Packages` entry with a user-facing changelog in `packaging/installer.yaml`. Ask the user which version to use.

## Domain language

`CONTEXT.md` defines the product terms and the words to avoid. Read it before naming code or writing user-visible copy.

## Rules

- **Backwards compatibility:** tolerate older server responses; the server may lag extension releases during rollout.
- **No new file for a constant, small util or one-off helper.** Extend `Pulse.cs`, the matching `PulseAccountClient.*.cs` partial or an established helper. New files only for real boundaries, such as a reader or applier reused across flows.
- No single-letter variable names, except where the ecosystem expects them.
