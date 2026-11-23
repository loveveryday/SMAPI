# AGENTS.md

SMAPI is the mod loader for Stardew Valley (C#, .NET 6). Solution: `src/SMAPI.slnx`.

## Layout
- `src/SMAPI` — core mod loader (`StardewModdingAPI.exe`).
- `src/SMAPI.Mods.ConsoleCommands`, `src/SMAPI.Mods.SaveBackup` — bundled mods.
- `src/SMAPI.Toolkit*` — shared toolkit (manifests, versions, mod data, API clients).
- `src/SMAPI.ModBuildConfig*` — NuGet package and Roslyn analyzers for mod authors.
- `src/SMAPI.Web` — smapi.io site.
- `src/SMAPI.Installer`, `src/SMAPI.Internal` — installer and internal helpers.
- `src/SMAPI.Tests*` — NUnit tests (FluentAssertions, Moq).
- `build/` — shared MSBuild targets; `docs/` — user and technical docs.

## Build, test, deploy
Builds copy output into the local game folder by default. See
`.agents/skills/smapi-local-deploy/SKILL.md` for the full workflow and pitfalls.

- Compile and test without deploying (safe while the game is running):
  `dotnet test src/SMAPI.Tests/SMAPI.Tests.csproj -p:CopyToGameFolder=false`
- Deploy: `dotnet build src/SMAPI/SMAPI.csproj` (close Stardew Valley first, or DLL copies fail).
- New runtime NuGet DLLs for SMAPI must also be added to `CopySMAPI` in `build/deploy-local-smapi.targets`.

## Code style
`.editorconfig` is enforced as errors. Key rules:
- Always qualify members with `this.`.
- Use language keywords (`int`, `string`), not `Int32`/`String`.
- No `var` for built-in types.
- 4-space indent; 2 spaces in `.csproj`/`.targets`; nullable is enabled (except `netstandard2.0`).

## Conventions
- Commit messages: short, lowercase, imperative (e.g. `add ...`, `fix ...`).
- Put package versions in `src/Directory.Packages.props` (central package management).
- User-visible changes go under "Upcoming release" in `docs/release-notes.md`.
- Persistent user files belong in `%AppData%\StardewValley\`, never in `ErrorLogs\` (purged on launch).
