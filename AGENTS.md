# AGENTS.md

Jellyfin plugin that plays a per-item custom intro before an episode/movie, then
lets the client skip the item's own intro. Target: **Jellyfin 10.10.x**.

> **Read `HANDOFF.md` first** — it has the current state, live test data, and the
> evidence behind these constraints.

## Hard constraints (easy to get wrong)

- **TFM/version lock.** The plugin targets `net8.0`; that matches Jellyfin 10.10.x.
  Bumping `Jellyfin.Controller`/`Jellyfin.Model` (pinned `10.10.7`) requires bumping
  the TFM in the csproj **and** `targetAbi` in `build.yaml`. A mismatch means the
  plugin silently won't load.
- **Playback mechanism is `IIntroProvider`** (`MediaBrowser.Controller.Library`),
  not media segments. Jellyfin clients call `GET /Items/{itemId}/Intros` and play
  the returned `IntroInfo` files *before* the item. Media segments can only mark time
  ranges inside the item's own file, so they can't play a different video.
- **Intro files must be real library items.** `LibraryManager.ResolveIntro` resolves a
  path to a `Video` and then looks it up with `GetItemById`; a file that is not a
  scanned library item is **silently dropped** (API returns `Items: []` with no error).
  Hence `Services/IntroLibraryManager.cs` keeps a hidden `Intro Replacer` library over
  the intros folder, and `IntroProvider` returns `IntroInfo { Path, ItemId }`. Do not
  go back to a config-folder path-only approach.
- **Segment providers are not auto-discovered.** `IMediaSegmentProvider` (unlike
  `IIntroProvider`) is not picked up from plugin assemblies; `PluginServiceRegistrator`
  must register it. Otherwise `GET /MediaSegments/{itemId}` (provider-filtered) returns
  nothing even though the rows exist. The stored provider id is
  `MD5(lowercased provider name)` (`IntroSegmentProvider.ProviderId`).
- **Episode intros are client-limited.** Web/desktop clients fetch intros for episodes
  (gated by the user's "Cinema mode" setting, default on). Some native TV/mobile apps
  ignore episode intros. Movies work on every client. No JS injection (user's choice).
- **There is no cross-client API to add an item to the media 3-dots menu.** Menu
  actions are client-side. Verified up to Jellyfin 12.0. Do not attempt server-side
  menu injection; the config lives on the plugin's dashboard page instead.
- **Placement semantics.** Per rule, `IntroPlacement.Replace` ensures an `Intro` media
  segment exists (keeps an existing one, else derives it from chapters) so the client
  skips the original intro; `IntroPlacement.Prepend` deletes those segments so the item
  plays from the start. See `IntroProvider` / `IntroSegmentHelper`.
- **Chapter reading.** Use `IItemRepository.GetChapters(item)`
  (`MediaBrowser.Controller.Persistence`, DI singleton). `IChapterManager` only saves.
  `ChapterInfo` has only `StartPositionTicks` — the end comes from the next chapter or
  the item runtime. Audio-based auto-detection is **not** implemented.
- **Once-per-session is a heuristic.** Jellyfin gives intro providers no client
  session id, so `IntroSessionTracker` treats `SessionIdleMinutes` without an intro
  as a new session (per user). Do not assume real session boundaries.
- **`IMediaSegmentManager` is in namespace `MediaBrowser.Controller`**, but its
  source folder in Jellyfin is misspelled `MediaSegements`. `MediaSegmentType` is in
  `Jellyfin.Data.Enums`. `IntroInfo` is in `MediaBrowser.Controller.Library`.
- Do **not** bundle Jellyfin/framework DLLs. `CopyLocalLockFileAssemblies=false` in
  the csproj keeps publish output to just our DLL; Jellyfin supplies the rest.

## Environment quirks

- Requires the .NET 8 SDK **and the ASP.NET Core 8 runtime** (the plugin pulls in
  `Microsoft.AspNetCore.App 8`). Both are installed system-wide, so plain `dotnet test`
  works. Without ASP.NET Core 8, the test host dies with "You must install or update .NET".
- Local Docker Jellyfin uses image `jellyfin/jellyfin:10.10.7` (see `docker/compose.yaml`).

## Commands

```bash
# Build / test
dotnet build IntroReplacer.slnx
dotnet test Jellyfin.Plugin.IntroReplacer.Tests/Jellyfin.Plugin.IntroReplacer.Tests.csproj

# Publish (produces a single DLL)
dotnet publish Jellyfin.Plugin.IntroReplacer/Jellyfin.Plugin.IntroReplacer.csproj -c Release -o bin/publish

# Docker test environment
bash scripts/generate-test-media.sh                                   # sample episode + intro
bash scripts/deploy-plugin.sh                                         # publish + copy DLL into docker Jellyfin
docker compose -f docker/compose.yaml up -d                          # then restart after each deploy
```

- Solution file is `IntroReplacer.slnx` (new `.slnx` format, not `.sln`).
- After `scripts/deploy-plugin.sh`, restart the container for the plugin to reload.

## Layout

- `Jellyfin.Plugin.IntroReplacer/Plugin.cs` — `BasePlugin<PluginConfiguration>`, `IHasWebPages`, static `Instance`/`LibraryManager`; owns `AssetStore`, `SessionTracker`, `IntroLibrary`.
- `Jellyfin.Plugin.IntroReplacer/PluginServiceRegistrator.cs` — registers `IMediaSegmentProvider`.
- `IntroProvider.cs` — `IIntroProvider` implementation; builds a `RuleContext`, runs `RuleEngine`, resolves assets via the hidden library, applies placement.
- `Services/RuleEngine.cs` — pure, first-match-wins evaluation (unit-tested; no Jellyfin types).
- `Services/RuleContext.cs` — primitive snapshot of item metadata (keeps the engine testable).
- `Services/IntroMatcher.cs` — builds `RuleContext` from a `BaseItem` (merges item + ancestor tags/genres/studios).
- `Services/IntroAssetStore.cs` — intro files live in `<PluginConfigurationsPath>/IntroReplacer/intros/`.
- `Services/IntroLibraryManager.cs` — hidden `Intro Replacer` library over the intros folder (create/find/hide).
- `Services/IntroChapterDetector.cs` — chapter → intro range heuristic (unit-tested).
- `Services/IntroSegmentProvider.cs` — `IMediaSegmentProvider` exposing the chapter-based Intro segment.
- `Services/IntroSegmentHelper.cs` — applies `Replace`/`Prepend` to `Intro` media segments.
- `Services/IntroSessionTracker.cs` — per-user "once per session" heuristic.
- `Api/IntroReplacerController.cs` — REST: `/IntroReplacer/Assets` (list/upload/delete), `/Libraries`, `/Users`, `/Targets` (series/season/episode tree), `/Test/{itemId}`.
- `Configuration/config.html` — dashboard page. Inline `<script>` (no external controller file); talks to the endpoints above and to Jellyfin's standard `get/updatePluginConfiguration`.

## Conventions

- Config is XML-serialized by Jellyfin: keep `PluginConfiguration` to simple settable
  types (no dicts/interfaces). Rule metadata uses the same models as the engine.
- Rules are ANDed; an empty condition list matches everything. `RuleEngine` is the
  single source of truth for matching — add tests there before wiring new conditions.
- Item targeting is include/exclude: `TargetItemIds` holds active series/movie ids
  (empty = all items), `ExcludedItemIds` holds deactivated season/episode ids.
  Matching runs against the item's ancestor chain, so excluding a season excludes
  its episodes. The config UI maintains these two lists from the target tree.
- There is no `Chance`/probability field by design; ordering + conditions decide.
- Owner in `build.yaml`/`manifest.json` is `flaconia`. Before publishing to a plugin
  repository, fill in `sourceUrl`/`checksum` in `manifest.json`.
