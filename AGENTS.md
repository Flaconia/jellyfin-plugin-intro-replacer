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
- **jellyfin-web only asks for intros when playback starts at index 0 of a list.**
  `getIntros` returns `[]` without calling the server when `options.startPositionTicks`
  or `options.startIndex` is truthy (`fullscreen === false` also suppresses). Pressing
  play on an episode expands the queue to its series and sets `startIndex`, so the
  pre-roll shows only for the first/unwatched-first episode and not for later episodes
  or resumes. Intros are also fetched **once, for the first item of a queue**; queue
  auto-advance never re-requests them. Not fixable server-side; the only universal fix
  is HLS-level splicing. See `HANDOFF.md` §2d.
- **There is no cross-client API to add an item to the media 3-dots menu.** Menu
  actions are client-side. Verified up to Jellyfin 12.0. Do not attempt server-side
  menu injection; the config lives on the plugin's dashboard page instead.
- **Placement semantics.** Per rule, `IntroPlacement.Replace` ensures an `Intro` media
  segment exists (keeps an existing one, else derives it from chapters) so the client
  skips the original intro; `IntroPlacement.Prepend` deletes those segments so the item
  plays from the start. See `IntroProvider` / `IntroSegmentHelper`.
- **Skipping is client-side, not server-side.** Jellyfin 10.10 has no server-side
  per-user "what to do with a segment" setting. The web client reads a per-type action
  from browser `localStorage` (default `AskToSkip` → an 8 s "Skip Intro" button; it
  never auto-seeks). Auto-skip requires the user to set `Intro Segments → Skip` on each
  client. Do not try to set that from the server (only JS injection could, which is out
  of scope). Segments are populated on demand and by the built-in "Media Segment Scan"
  task (our `IMediaSegmentProvider` feeds it).
- **Chapter reading.** Use `IItemRepository.GetChapters(item)`
  (`MediaBrowser.Controller.Persistence`, DI singleton). `IChapterManager` only saves.
  `ChapterInfo` has only `StartPositionTicks` — the end comes from the next chapter or
  the item runtime. Audio-based auto-detection is **not** implemented.
- **Frequency has three modes.** `EveryTime`, `OncePerInterval` (uses the rule's
  `RepeatAfterMinutes`, present on `IntroRule`, not on `PluginConfiguration`) and
  `OncePerSession`. "Session" here is the Jellyfin session:
  the provider resolves it from the request's `Jellyfin-DeviceId` claim and
  `ISessionManager.Sessions`, and `Plugin.cs` forwards `ISessionManager.SessionEnded`
  (client disconnect) to `IntroSessionTracker.NotifySessionEnded`. Keep the tracker free
  of Jellyfin types so it stays unit-testable. If no session can be resolved (e.g. an
  API-key request), it falls back to the wait-time behaviour.
- **`IMediaSegmentManager` is in namespace `MediaBrowser.Controller`**, but its
  source folder in Jellyfin is misspelled `MediaSegements`. `MediaSegmentType` is in
  `Jellyfin.Data.Enums`. `IntroInfo` is in `MediaBrowser.Controller.Library`.
- Do **not** bundle Jellyfin/framework DLLs. `CopyLocalLockFileAssemblies=false` in
  the csproj keeps publish output to just our DLL; Jellyfin supplies the rest.
- **The config page's custom elements must be built from HTML strings.** Jellyfin's
  v0 custom-element polyfill upgrades a subtree when the document's MutationObserver
  sees it inserted, so `document.createElement('input')` +
  `setAttribute('is', 'emby-input')` never upgrades (and `emby-input`'s
  `createdCallback` touches `parentNode`, which would throw). Do
  `wrapper.innerHTML = '<input is="emby-input" label="…">'` and insert the result into
  the page, as Jellyfin's own dashboards do. Same reason not to reach for
  `emby-collapse`/`emby-radio`: those modules are only pulled in by lazy legacy
  controller chunks (library browser, filter dialog), so they may not be registered on
  the dashboard route — use `<details>` + local CSS instead. There is likewise no
  reachable tooltip component, so the field help uses a local hover bubble
  (`.ir-tip`/`.ir-tipBubble` in `config.html`) with a `help_outline` icon button.

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
- `Services/IntroAssetStore.cs` — intro files live in the configured `IntroFolder` (absolute path) or, by default, `<PluginConfigurationsPath>/IntroReplacer/intros/`. Uploads keep the original (sanitised) file name; dropped files are auto-registered.
- `Services/IntroLibraryManager.cs` — hidden `Intro Replacer` library over the intros folder (create/find/hide/re-point when the folder changes).
- `Services/IntroChapterDetector.cs` — chapter → intro range heuristic (unit-tested).
- `Services/IntroSegmentProvider.cs` — `IMediaSegmentProvider` exposing the chapter-based Intro segment.
- `Services/IntroSegmentHelper.cs` — applies `Replace`/`Prepend` to `Intro` media segments.
- `Services/IntroSessionTracker.cs` — intro-repeat bookkeeping (wait-time + once-per-session); pure, unit-tested, no Jellyfin types.
- `Api/IntroReplacerController.cs` — REST: `/IntroReplacer/Assets` (list/upload/delete), `/IntroReplacer/Assets/FromLibrary/{itemId}` (use an existing library video as an intro), `/IntroReplacer/IntroFolder` (get/set the intro storage folder), `/Videos` (every video in every library, for that picker), `/Users`, `/Targets` (library → series → seasons → episodes tree, movies as direct library children), `/Test/{itemId}`.
- `Configuration/config.html` — dashboard page. Inline `<script>` (no external controller file); talks to the endpoints above and to Jellyfin's standard `get/updatePluginConfiguration`. Follows the vanilla Control Panel markup (`<form>` limited to 54em, `h1.pageTitle`, `h2.sectionTitle`, `inputContainer`/`checkboxContainer`/`checkboxList`, `raised` buttons, `raised button-submit block` Save); the library-video picker is a hand-built `.dialogContainer > .dialog.formDialog` modal — plugin pages **cannot** `require` `dialogHelper` (no `window.require`/`window.define` in 10.10).

## Conventions

- Config is XML-serialized by Jellyfin: keep `PluginConfiguration` to simple settable
  types (no dicts/interfaces). Rule metadata uses the same models as the engine.
- Rules are ANDed; an empty condition list matches everything. `RuleEngine` is the
  single source of truth for matching — add tests there before wiring new conditions.
- Item targeting is include/exclude: `TargetItemIds` holds active series/movie ids
  (empty = all items), `ExcludedItemIds` holds deactivated season/episode ids.
  Matching runs against the item's ancestor chain, so excluding a season excludes
  its episodes. The config UI maintains these two lists from the target tree.
- Intro assets are either **uploaded files** (`FileName`, stored in the configured
  `IntroFolder` — default `<PluginConfigurationsPath>/IntroReplacer/intros/` — scanned
  through the hidden library) or **existing library videos** (`LibraryItemId`, resolved
  with `ILibraryManager.GetItemById`). Always branch on `LibraryItemId != Guid.Empty`
  before touching the asset store: a library asset has an empty `FileName`, and deleting
  it must only unregister it — never delete the user's media. There is **no** global
  enable/disable flag on an asset: the admin list is read-only (name + Remove), and each
  rule picks the intros it uses via `AssetIds`.
- Uploads keep their original (sanitised) base name so the hidden library item — i.e.
  the title shown while the intro plays — is readable; legacy `<hex>.mp4` names are
  renamed by `ScanForNewFiles`. `GET /Assets` also auto-registers any supported video
  dropped into the folder. Changing `IntroFolder` moves the files and rebuilds the
  hidden library (`RecreateAsync`); 10.10 has no `UpdateVirtualFolderOptions`.
- The hidden `Intro Replacer` library and its intro clips must never show up in the
  config UI. `GET /Targets` and `GET /Videos` filter it out using
  `IntroLibraryManager.GetLibraryItemId()` (one query per virtual folder, skipping the
  hidden one). Add the same filter to any new endpoint that enumerates libraries or
  items.
- **Targets and Tags are alternative targeting methods, not additive conditions.** Each
  rule has `TargetMode` (`Targets` or `Tags`; `null` = infer: Tags when any
  tag/genre/studio condition is set, else Targets, so old rules keep working).
  `RuleEngine.Matches` evaluates **only** the selected method — `LibraryIds`,
  `ExcludedLibraryIds`, `TargetItemIds` and `ExcludedItemIds` are ignored in `Tags`
  mode, and `IncludeTags`/`ExcludeTags`/`Genres`/`Studios` are ignored in `Targets`
  mode. User/date/release-date conditions apply to both. The UI enforces this with a
  radio on the left of each section: the unselected section is greyed out, collapsed
  and cannot be opened, but its content stays in the rule and returns when selected
  again.
- The targets tree is the only place library scoping is chosen: `GET /Targets` returns
  the libraries themselves as the top-level nodes, so there is no separate library
  checkbox list and no `GET /Libraries` endpoint. Libraries are active by default and
  deactivated through `ExcludedLibraryIds` (unchecking hides the library's whole
  subtree, like a series/season; the config page converts any legacy `LibraryIds`
  include-list into exclusions on load). An empty `LibraryIds` still means "all".
  `TargetNodeDto` carries the item's merged `Tags`/`Genres`/`Studios` (via
  `IntroMatcher.GetMergedMetadata`) so the config page can preview a tag rule locally.
- The rule header always shows a live scope line (inside the rule `<summary>`, so it
  survives collapsing): `Targets — …` counts the selected tree items, `Tags — …`
  counts matching episodes/movies using the same include/exclude/genre/studio logic as
  the engine. Keep it updated from every control that changes the rule's scope.
- A rule's intro picker is a compact token list: only the chosen assets are shown, each
  with a Remove button, followed by a trailing `(add …)` `emby-select` listing the
  remaining intros in alphabetical order. The Tags conditions live in their own
  collapsible `<details>` (`Include/Exclude tags`, `Genres`, `Studios`).
- There is no `Chance`/probability field by design; ordering + conditions decide.
- Owner in `build.yaml`/`manifest.json` is `flaconia`. Before publishing to a plugin
  repository, fill in `sourceUrl`/`checksum` in `manifest.json`.
