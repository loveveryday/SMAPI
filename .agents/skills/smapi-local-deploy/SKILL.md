---
name: smapi-local-deploy
description: >-
  Build, test, and deploy local SMAPI and bundled mods (such as ConsoleCommands)
  to the local Stardew Valley installation. Use when the user asks to deploy,
  build, or test SMAPI changes, or when troubleshooting locked game DLLs,
  missing NuGet dependencies in smapi-internal, or Vortex coexistence.
---

# SMAPI Local Build, Test & Deploy

## How Auto-Deployment Works

- `build/common.targets` enables `<CopyToGameFolder>true</CopyToGameFolder>` by default.
- `build/find-game-folder.targets` auto-detects the local Stardew Valley directory (e.g. `D:\SteamLibrary\steamapps\common\Stardew Valley`).
- `build/deploy-local-smapi.targets` copies build outputs to the game folder on `AfterBuild`:
  - `SMAPI` → `StardewModdingAPI.exe`, `StardewModdingAPI.dll`, and `smapi-internal\*`
  - `SMAPI.Mods.ConsoleCommands` / `SMAPI.Mods.SaveBackup` → `Mods\<AssemblyName>\`
  - `SMAPI.Toolkit` / `SMAPI.Toolkit.CoreInterfaces` → `smapi-internal\`

## Commands (Run from Repository Root)

- **Deploy SMAPI core only** (changes under `src/SMAPI`):
  ```powershell
  dotnet build src/SMAPI/SMAPI.csproj --no-restore
  ```
- **Deploy SMAPI + ConsoleCommands mod** (changes under `src/SMAPI.Mods.ConsoleCommands`):
  ```powershell
  dotnet build src/SMAPI.Mods.ConsoleCommands/SMAPI.Mods.ConsoleCommands.csproj --no-restore
  ```
- **First build or after adding/changing NuGet packages** (omit `--no-restore`):
  ```powershell
  dotnet build src/SMAPI/SMAPI.csproj
  ```
- **Compile and run unit tests without deploying** (safe while Stardew Valley is running):
  ```powershell
  dotnet test src/SMAPI.Tests/SMAPI.Tests.csproj --no-restore -p:CopyToGameFolder=false --filter "FullyQualifiedName~ConsoleCommandTests" -v minimal
  ```

## Pitfalls & Checklist

1. **Game process locks DLLs**:
   - Close Stardew Valley (`StardewModdingAPI`) before deploying; otherwise MSBuild fails with `MSB3026`/`MSB3021` copying into `smapi-internal\`.
   - Pass `-p:CopyToGameFolder=false` when you only need to compile or run tests while the game is open.
2. **New NuGet dependencies must be registered for deployment**:
   - After adding a package to `src/Directory.Packages.props` and `src/SMAPI/SMAPI.csproj`, inspect `src\SMAPI\bin\Debug\*.dll` and add all new runtime DLLs to the `CopySMAPI` target in `build/deploy-local-smapi.targets`.
3. **Missing analyzer/package cache (`CS0006`)**:
   - If `--no-restore` fails with `CS0006` on a missing NuGet DLL, rerun `dotnet build` without `--no-restore`.
4. **Vortex coexistence**:
   - If Vortex is used on this machine, clicking **Deploy Mods** in Vortex may warn about externally modified SMAPI files. Choose to keep the newer local files, or rerun the build command to redeploy.
5. **Runtime data paths**:
   - Config overrides go in `<GamePath>\smapi-internal\config.user.json`.
   - Persistent SMAPI user files (like `SMAPI-console-history.txt`) belong in `%AppData%\StardewValley\`, **never** in `ErrorLogs\` (because `SCore.PurgeNormalLogs()` deletes `ErrorLogs\SMAPI-*` on every launch).
