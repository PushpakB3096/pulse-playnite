# Pulse Playnite Extension — AI context

Playnite plugin (C# / .NET Framework) that syncs library metadata, sessions, covers, filter presets, and status pushes to **PlayLog** (`pulse-server`). Consumed alongside **pulse-app**; this repo is the Playnite client only.

## Stack

- **.NET Framework 4.8**, **C#**, **WPF** settings UI
- **Playnite SDK** 6.16 (`Playnite.SDK` NuGet)
- **Newtonsoft.Json** for API payloads
- **Entry:** `Pulse.cs`; HTTP client split across `PulseAccountClient*.cs`

## Build

This project **does not build on macOS/Linux** in this environment. There is no local `dotnet` / `msbuild` requirement here; compile verification runs in CI on Windows.

On Windows (or CI):

```bash
nuget restore Pulse.sln -NonInteractive
msbuild Pulse.sln /restore:false /p:Configuration=Release /p:Platform="Any CPU"
```

**After every push**, check GitHub Actions for build failures before considering the change done. Use `gh run list` / the Actions tab on the repo. Workflows:

- **Playnite extension (CI)** — branch/PR compile check (`.github/workflows/playnite-extension.yml`)
- **Playnite extension (release)** — master push packs `.pext` and drafts a release (`.github/workflows/playnite-extension-release.yml`)

If CI fails, read the MSBuild log (failed step output), fix locally, push again, and re-check Actions until green.

## Conventions

- **Do not create a new file for every constant, util, or tiny helper.** Extend an existing `PulseAccountClient*.cs` partial, `Pulse.cs`, or an established helper before adding a new file. New files only for real boundaries (e.g. a distinct applier or reader module reused across flows).
- Match Playnite SDK types exactly (`ReleaseDate` constructors take `int`, not `int?`; use the year / year+month / year+month+day overloads).
- **Naming:** Do not use single-letter variable names except where the ecosystem expects them.
- **Backwards compatibility:** Extension changes should tolerate older server responses; server may lag extension releases during rollout.

## Related workspaces

`pulse-app`, `pulse-server`, `playlog`, `pulse-design` — edit **this** repo only when working in pulse-playnite.
