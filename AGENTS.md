# AGENTS.md

Spire Buddy: a self-contained C# mod for Slay the Spire 2 (in-game chat, AI play,
Jev decisions, optional Combat Solver integration). Two projects, no solution file.

## Build & test

The mod references the game's assemblies via `STS2GameDataDir`, which must be a
directory containing both `sts2.dll` and `GodotSharp.dll`. It defaults to a macOS
Steam path, so Windows/Linux always pass it:

```powershell
dotnet build .\mod\SpireBuddy -c Release -p:STS2GameDataDir="C:\path\to\data_sts2_win64"
dotnet build .\mod\SpireBuddy.Tests -c Release
dotnet run --project .\mod\SpireBuddy.Tests -c Release
```

Tests need only the .NET 9 runtime (target `net9.0`); they do not need Godot or the
game assemblies. There is no linter, formatter, or CI config, and no solution file:
build each `.csproj` explicitly.

Run the full suite by omitting flags. Focused subsets are positional args after `--`:

```powershell
dotnet run --project .\mod\SpireBuddy.Tests -c Release -- --card-choices
```

Available flags: `--card-choices`, `--dialogue`, `--solver`, `--jev`,
`--jev-combat`, `--jev-strategy`, `--non-combat`, `--merchant`, `--crystal`.
Add new flags to `mod/SpireBuddy.Tests/Program.cs` (top-level `args.Contains`
routing) and wire the new `*Checks` class into the default full run there too.

Optional real decompiler check against the installed game DLL:
`$env:STS2_GAME_ASSEMBLY = "C:\path\to\sts2.dll"` then rerun the suite.

## Architecture (non-obvious parts)

- `mod/SpireBuddy/Runtime/` — model client, controller, legal-action validation,
  game state filtering, Jev, traces. **Must stay free of `Godot` and game-types
  references**: the test project compiles only `../SpireBuddy/Runtime/*.cs`, so any
  Godot/sts2 dependency added here breaks the tests.
- `mod/SpireBuddy/Game/` — Godot/game bindings, main-thread only. `Mod.cs` is the
  mod entrypoint + native UI. Neither is compiled into tests.
- `IGameAdapter` (Runtime) is the worker→game-thread boundary; the concrete
  `ScheduledGameAdapter` lives in Game.
- Tests are a hand-rolled harness, not xunit: `Check(bool, message)` throws on
  failure. Model transports are exercised with fake `HttpMessageHandler`s.

## Conventions & gotchas

- Version is duplicated and must stay in sync: `<Version>` in
  `mod/SpireBuddy/SpireBuddy.csproj` and `"version"` in `mod/SpireBuddy/SpireBuddy.json`.
  Mod ID / assembly name is `SpireBuddy`.
- `ICSharpCode.Decompiler.dll` must ship next to `SpireBuddy.dll`; `Mod.Initialize`
  resolves it with a manual `AssemblyLoadContext.Resolving` hook. Installers copy it
  and `SpireBuddy.third-party-notices.txt`.
- UI strings go through `L10n.T("English", "中文")`. The language comes from the
  persisted `SettingsSave`, not transient `LocManager` (which flips to English during
  metrics upload).
- Never distribute the game's assemblies, and treat `user://spire-buddy/settings.json`
  (contains the API key) as sensitive.
- `docs/architecture.md` is the authoritative design reference; `mod/README.md` is
  the user/settings guide. Read them before changing the decision loop.

## Verification limits

The test project does not cover native UI or live game bindings. After installing,
verify changes by restarting the game and smoke-testing in-game.
