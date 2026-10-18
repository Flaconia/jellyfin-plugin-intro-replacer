# HANDOFF — Intro Replacer (Jellyfin plugin)

Last updated: 2026-10-18 (session 7). Read this first; then `AGENTS.md` for the hard
constraints, then `README.md` for the user-facing summary.

## 1. What we are building

A Jellyfin **10.10.x** plugin that plays a **custom intro video (B)** before a movie or
episode **(A)**, then lets the client skip **A**'s own intro — without rewriting,
remuxing, or duplicating any media file.

- Plugin name: `Intro Replacer`; GUID `07e08043-224d-4228-964f-02242635344f`.
- Manifest owner: `flaconia`. TFM `net8.0`; `targetAbi 10.10.0.0`.
- `Jellyfin.Controller` / `Jellyfin.Model` pinned to `10.10.7`.

## 2. Playback mechanism (the whole trick)

Jellyfin core exposes `GET /Items/{itemId}/Intros` (controller
`Jellyfin.Api/Controllers/UserLibraryController.cs`, operationId `GetIntros`). It asks
every registered `IIntroProvider` for `IntroInfo` items and returns them as playable
`BaseItemDto`s. The client plays them, then the real item.

Our `IntroProvider : IIntroProvider` (`Jellyfin.Plugin.IntroReplacer/IntroProvider.cs`)
builds a `RuleContext`, runs `RuleEngine.Select`, and returns the chosen intro(s).

### 2a. Critical constraint: intro files MUST be library items

`LibraryManager.ResolveIntro` (in `Emby.Server.Implementations/Library/LibraryManager.cs`)
does the following for an `IntroInfo` that only has a `Path`:

```
video = ResolvePath(fileSystem.GetFileSystemInfo(info.Path)) as Video;
if (video is null) { log }
else {
    var dbItem = GetItemById(video.Id) as Video;
    if (dbItem is not null) video = dbItem;
    else return null;                 // <-- silently dropped
}
```

So a file that is not a **scanned library item** (no row in the DB for its id) is
silently discarded by the server: the provider logs "1 intro file(s)", but the API
returns `Items: []`. **This was the root cause of "the intro never plays".**

Fix: `Services/IntroLibraryManager.cs` owns a **hidden library** named
`Intro Replacer` (collection type `movies`) pointing at the plugin's intros folder
`<PluginConfigurationsPath>/IntroReplacer/intros/`. The provider then returns
`new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id }`.

The library is hidden from users by manipulating each user's preferences via
`User.SetPreference(...)` + `IUserManager.UpdateUserAsync`:
- remove from `BlockedMediaFolders` (blocking would break streaming),
- add to `EnabledFolders` **only if** the user already restricts folders,
- add to `MyMediaExcludes` + `LatestItemExcludes`,
- remove from `OrderedViews` + `GroupedFolders`.

Verified in the live server: `/UserViews` shows only the real library, and the DB
`Preferences` rows 7/8 (`LatestItemExcludes`/`MyMediaExcludes`) contain the hidden
library's item id.

The library is created lazily by `EnsureAsync()`:
- from `IntroProvider.GetIntros` when a file-based asset is resolved and the library
  does not exist yet, and
- from the upload endpoint after a new file is stored (then triggers a scan).

Assets that point at an **existing library video** (`LibraryItemId`) do not need any of
this: the item is already scanned, so the provider returns its own `Path` + `Id`.

### 2b. Media segments (skipping the item's own intro)

`Replace` wants the client to skip **A**'s original intro. That is done with a media
segment of type `Intro`:

- `Services/IntroChapterDetector.cs` — pure, unit-tested heuristic that finds the
  intro range from `ChapterInfo`s (name keywords first, then a position fallback:
  first chapter at 0:00 followed by another chapter within 4 minutes).
- `Services/IntroSegmentProvider.cs` — an `IMediaSegmentProvider` that exposes the
  chapter-derived segment. Its id is `MD5("intro replacer")` (Jellyfin's
  `GetProviderId` = MD5 of the lowercased provider name).
- `Services/IntroSegmentHelper.cs` — `ApplyPlacementAsync`: `Prepend` deletes `Intro`
  segments; `Replace` leaves existing ones alone and, if none exist, creates one from
  chapters.

**Why the provider class is needed:** `MediaSegmentManager.GetSegmentsAsync(...,
filterByProvider: true)` (the default used by `GET /MediaSegments/{itemId}`, which is
what clients call) filters rows down to the **registered** segment providers'
ids and returns `[]` if there are none. `IMediaSegmentProvider` is **not** discovered
automatically from plugin assemblies (unlike `IIntroProvider`), so
`PluginServiceRegistrator.cs` registers `IMediaSegmentProvider -> IntroSegmentProvider`
in DI.

Verified live for Pokémon S01E01: `GET /MediaSegments/{ep}` returns
`{Type: Intro, StartTicks: 0, EndTicks: 892500000}` (0 → 89.25 s, the "Sigla
d'apertura" chapter) and the `MediaSource.HasSegments` flag is `true`.

**The skip itself is client-side.** Jellyfin 10.10 has **no** server-side per-user
setting for what to do with a segment (verified: no `SegmentAction` in the server
model). The web client's `mediaSegmentManager.ts` acts on a per-type action from
browser `localStorage` (`segmentTypeAction__Intro`), defaulting to `AskToSkip`; that
shows a **Skip Intro button for 8 s** (`components/playback/skipsegment.ts`) and
never seeks on its own. For hands-free skipping the user sets
`Playback → Media Segment Actions → Intro Segments → Skip` (`MediaSegmentAction.Skip`
→ `skipSegment()` seeks to `EndTicks`). The plugin cannot set this without JS
injection, which is out of scope by the user's choice. `playbackstart` re-fires for
every item, so the main item's segment is picked up after the pre-roll — the mechanism
is sound.

Segments are populated two ways, both via the same `IMediaSegmentProvider`:
1. on demand in `ApplyPlacementAsync` when the intro is served (covers replay), and
2. by Jellyfin's built-in **"Media Segment Scan"** task
   (`MediaSegmentExtractionTask`, runs every 12 h) — confirmed working: it logged
   `Media Segment provider Intro Replacer found 1 for …` across the Pokémon episodes.
   Run it once after installing (Dashboard → Scheduled Tasks) to populate immediately.

### 2c. Client support matrix (important)

From `jellyfin-web` `release-10.10.z`
(`src/components/playback/playbackmanager.js`, `enableIntros`): intros are fetched for
any item with `MediaType === 'Video'` that is not a `TvChannel`, not `InProgress`, has
an `Id`, and only when the user's **Cinema mode** setting is on
(`userSettings.enableCinemaMode()`, default **true**). So episodes ARE supported by the
web client / Jellyfin Media Player.

Native TV/mobile apps are the caveat: per the third-party "Projectionist" plugin,
some ignore (or fail on) episode intros. **Movies work everywhere.** We do not inject
JS, by the user's explicit choice, so episode support is limited to web-based clients.

### 2d. When the client actually asks for intros (why "it only plays sometimes")

`getIntros(firstItem, apiClient, options)` in `playbackmanager.js` bails out (returns
`Items: []` **without calling the server at all**) when:

```js
options.startPositionTicks || options.startIndex || options.fullscreen === false ||
!enableIntros(firstItem) || !userSettings.enableCinemaMode()
```

That has two consequences users notice:

- **Queues / auto-advance.** `playWithIntros` fetches intros **once, for the first
  item** of the play queue. `nextTrack` → `playInternal` never re-requests intros. So a
  custom intro plays before the first item you start, not before every item in a binge.
  (A's own `Intro` segment *is* re-read on each `playbackstart`, so skipping A's intro
  still works per item.)
- **Starting a specific episode.** Pressing play on an episode calls
  `getEpisodePlaybackPromise` → `getEpisodes()` → `filterEpisodes()`, which expands the
  queue to the whole series and sets `options.startIndex` to that episode's index. Any
  truthy `startIndex` suppresses intros. So intros play when you start at index 0 (the
  first episode, or the series' first-unwatched episode) and not when you start from a
  later episode. A resume position (`startPositionTicks`) suppresses them the same way.

This is client behaviour; the server is never asked, so **the plugin cannot fix it**
without JS injection. The only universal workaround is HLS-level pre-roll (splice the
intro into the item's own stream, as "Projectionist" does), which forces a
transcode/remux of playback. Documented rather than worked around.

## 3. Bugs found and fixed in session 2

1. **Config page wiped every asset's `FileName`.**
   `Configuration/config.html` did `config.Assets = <result of GET /IntroReplacer/Assets>`,
   but that DTO has no `FileName`. Saving the config then persisted
   `<FileName />`, so `IntroAssetStore.ResolvePath` returned `null` and the provider
   logged "Intro asset ... is missing on disk". Fix: keep `config.Assets` from
   `getPluginConfiguration`, use the `/Assets` DTO only for the "file exists" flag.
2. **Path-only intros silently dropped by Jellyfin.** See §2a. Fix: hidden library +
   `ItemId`.
3. **Media segments invisible to clients.** See §2b. Fix: `PluginServiceRegistrator`.

## 3b. Session 3 notes

- The reported "intro replays from the start" was **not** a server bug: B plays and A
  carries a correct `Intro` segment. It is the client's default `AskToSkip` behaviour
  (8 s button, no auto-seek). DECISION: keep the standard behaviour and document the
  per-client auto-skip setting (`Intro Segments → Skip`); no JS injection. A config-page
  hint was added next to the `Replace`/`Prepend` radio.
- **Login gotcha:** `POST /Users/AuthenticateByName` binds the password to the JSON
  field **`Pw`**, not `Password`. Sending `Password` yields `401 Invalid username or
  password`, which is very misleading when debugging. Use
  `-d '{"Username":"root","Pw":"root"}'`.
- Credentials now `root` / `root` (confirmed working). A normal user `tester`
  (`tester` / `tester`) was created via the API for multi-user testing — remove it if
  not wanted.
- A temporary admin API key was inserted to bootstrap and then **deleted**; no keys
  remain (`SELECT count(*) FROM ApiKeys` → 0).

## 3c. Session 4 notes

Three user-reported problems.

1. **Frequency wording + a third option.** `IntroFrequency` now has three values, and
   the rule card labels/hints were rewritten to match what the client actually does
   (no "fetch", no "session" for the wait-time mode):
   - `EveryTime` — "Every time you start an episode or movie" (plays before the first
     item of a queue only; see §2d).
   - `OncePerInterval` — "Only after the wait time has passed", using the renamed global
     setting `RepeatAfterMinutes` (was `SessionIdleMinutes`; the XML name changed, so an
     old value falls back to the 180 default).
   - `OncePerSession` — "Once per session", i.e. plays again after the client
     disconnects. `IntroSessionTracker` keys on the Jellyfin session id resolved from
     the current request, and `Plugin.cs` forwards `ISessionManager.SessionEnded` to
     `NotifySessionEnded(...)`. Verified: closing the client's WebSocket removes the
     session (`WebSocketController.OnConnectionClosed` → `CloseIfNeededAsync` →
     `SessionEnded`), which is exactly the "until disconnect" signal.
   Verified live: same device → 1 then 0 intros; a second device → 1 (separate session);
   `OncePerInterval` → 1 then 0 within the wait; `EveryTime` → 1 then 1.

2. **Episode play did not show the intro.** Not a bug in the plugin — see §2d.

3. **The intro file appeared in the targets list.** `/IntroReplacer/Targets` listed every
   `Series`/`Movie`, including the hidden `Intro Replacer` library's intro clip (item
   name `85a31089f6f24389b17d990044c7e5c8`, the intro mp4). `/IntroReplacer/Libraries`
   also offered the hidden library as a rule condition. Fix: `IntroLibraryManager
   .GetLibraryItemId()` and filter it out of both endpoints (targets are filtered via
   `ILibraryManager.GetCollectionFolders(item)`).

Tests are now 32 (`IntroSessionTrackerTests` adds 9).

## 3d. Session 5 notes

1. **Existing library videos can be used as intros.** `IntroAsset` gained
   `LibraryItemId` (non-nullable; `Guid.Empty` = an uploaded file). `GET /Videos`
   lists every `Movie`/`Episode`/`Video`/`MusicVideo`/`Trailer` with a path in every
   library except the hidden intro library, and `POST /Assets/FromLibrary/{itemId}`
   registers one (idempotent; the media file is never copied or touched). The provider
   resolves such an asset with `ILibraryManager.GetItemById` and returns
   `IntroInfo { Path, ItemId }` — no hidden-library scan involved. Deleting such an
   asset only unregisters it (`IntroAssetStore.Delete` no-ops because `FileName` is
   empty).
2. **Config page UX.** Each rule is a `<details>` card (click the summary to
   collapse; the Enabled checkbox stays in the summary and stops click propagation so
   it does not toggle the card). The Targets block inside a rule is its own
   `<details>`, closed by default and built lazily on first open. Open/closed state is
   kept in a JS `openState` map keyed by rule id, so it survives re-renders (move /
   remove / refresh after save). Rules that still carry the old all-zero id get a fresh
   client-side `guid()` on load, otherwise they would all share one `openState` slot.
3. **Episode numbers in the target tree.** `TargetNodeDto` already carried `Index`;
   the checkbox label is now `"{Index}. {Name}"` for episodes.

## 3e. Session 6 notes — config page re-styled as vanilla Control Panel

1. **Look and layout.** Content lives in a `<form>` (the global
   `form { max-width: 54em }` gives the "room on the right"), the page header is
   `h1.pageTitle` (1.8em) and section headers are `h2.sectionTitle` (1.5em) — previously
   a h2/h3 pair that read as one level. Fields use the vanilla blocks
   (`inputContainer` + `label` attribute on `emby-input`, `checkboxContainer`
   `-withDescription`, `checkboxList paperList`, `selectContainer`), and every text
   button except Save is `raised` (grey/`#303030`, white text); Save is
   `raised button-submit block`. The Save button moved back to `type="submit"` with a
   `submit` handler that calls `preventDefault()`.
2. **Minutes-to-wait stepper.** Narrow `type="number"` field (6em, `max=99999`) with the
   native stacked spinner suppressed (`appearance: textfield` +
   `::-webkit-*-spin-button`) and two flat `paper-icon-button-light` buttons
   (`remove`/`add`) side by side next to it.
3. **Library-video picker is a modal.** "Add a video from a library…" opens a
   hand-built `.dialogContainer > .dialog.dialog-fixedSize.dialog-small.formDialog.paperList`
   with `formDialogHeader` / `formDialogContent.smoothScrollY` / `formDialogFooter`, a
   filter field and click-to-select `.listItem` rows grouped by library. Vanilla sizing
   comes from `dialog-small` (60%×80% at ≥80em/≥45em, fullscreen otherwise) — note that
   `.dialog` itself has **no** background in Jellyfin's CSS, hence the `paperList` class
   on the dialog to get the theme surface. `dialogHelper` is unreachable from a plugin
   page (no `window.require`), so the markup/backdrop/Escape handling is local.
   `GET /Videos` now reports `Library` (one `ParentId` query per library) to group the
   rows.
4. **Uploading or deleting an intro no longer discards unsaved rule edits**: the page
   re-reads `Assets` only (`reloadAssets()`) instead of re-running the whole `load()`.
5. **jsdom smoke test** (`/tmp/opencode/uitest/smoke.js`, throwaway) drives the page
   with stubbed `ApiClient`/`Dashboard` and asserts the whole UI build, the rule
   interactions, the save submit and the picker modal. It also documents that jsdom
   leaves `is="emby-*"` un-upgraded, i.e. the page must work with plain elements too.

## 3f. Session 7 notes — per-rule wait time, libraries in the target tree

1. **Wait time moved into the rule.** `IntroRule` gained `RepeatAfterMinutes` (default
   180) and `PluginConfiguration.RepeatAfterMinutes` was removed, so each rule decides
   its own wait. `RuleEngine.IntroSelection` now carries it, and `IntroProvider.ShouldPlay`
   reads it from the selection (also the fallback used by `OncePerSession` when no client
   session can be resolved). The config page shows the stepper only while the rule's
   Frequency is `OncePerInterval`. Old configs that still have the root
   `<RepeatAfterMinutes>` simply lose it (XmlSerializer ignores the unknown element) and
   the rules fall back to 180.
2. **Libraries are part of the target tree.** `GET /Targets` now returns one node per
   targetable library (`Type = "Library"`, one `ParentId` query per virtual folder, the
   hidden intro library skipped by id) with its series/movies, seasons and episodes as
   children. The standalone "Libraries (empty = all)" checkbox list and the now-unused
   `GET /Libraries` endpoint are gone. In the tree, an empty `LibraryIds` means "all
   libraries", so every library renders its children; picking a library narrows the rule
   and hides the others. Deselecting a library also drops its targets/exclusions.
3. **Target summary is a count.** The Targets summary now reads like
   `1 library, 2 series, 4 seasons, 20 episodes` (zero-count groups omitted; `all items`
   when nothing is selected). Counting walks the loaded tree with the same
   active/excluded logic the engine uses, so it is exact.
4. **"Active for users" moved up**, directly under the rule's Name field.

## 4. Automatic intro detection — status

- **Chapters: implemented** (`IntroChapterDetector`) and unit-tested. It is read-only
  w.r.t. the media file. Works when Jellyfin has extracted chapters (the Pokémon rips
  have 7 Italian-named chapters; Jellyfin's chapter extraction needs to have run / be
  enabled for the library).
- **Audio-based automatic detection: NOT implemented.** Real detection means
  extracting/normalising audio and fingerprinting to find repeated segments across
  episodes (what Intro Skipper does with chromaprint). This is a large feature; right
  now we log a message and leave the item alone. Options: (a) tell users to install
  Intro Skipper alongside this plugin; (b) later, implement a `IMediaSegmentProvider`
  that does audio analysis.

## 5. Config model & endpoints

`Configuration/PluginConfiguration.cs` (XML-serialised — keep it to simple settable
types):

- `PluginConfiguration`: `Assets`, `Rules`, `MoviesOnly`.
- `IntroAsset`: `Id`, `Name`, `FileName` (the stored file under the intros folder, empty
  for library assets), `LibraryItemId` (an existing library video, `Guid.Empty` for
  uploads), `Enabled`.
- `IntroRule`: `Id`, `Name`, `Enabled`, `LibraryIds` (empty = all libraries),
  `UserIds`, `TargetItemIds`
  (active series/movie ids; empty = all), `ExcludedItemIds` (deactivated season/episode
  ids), `IncludeTags`, `ExcludeTags`, `Genres`, `Studios`, `DateRange`,
  `ReleaseDateRange`, `AssetIds`, `PlaybackMode` (`RandomOne`/`SequentialAll`),
  `Placement` (`Replace`/`Prepend`), `Frequency`
  (`EveryTime`/`OncePerInterval`/`OncePerSession`), `RepeatAfterMinutes` (180, used by
  `OncePerInterval`).
- There is intentionally **no** chance/probability field. (Historically a rule could
  have a 0–100 roll that decided whether a matched rule fired, falling through on
  failure; the user removed it — ordering + conditions decide now.)

REST (`Api/IntroReplacerController.cs`, `[Authorize(Policy = RequiresElevation)]`,
route `[controller]` → `/IntroReplacer/...`):

- `GET /Assets`, `POST /Assets` (multipart `file`), `DELETE /Assets/{id}` — the POST
  also ensures the hidden library and triggers a scan.
- `POST /Assets/FromLibrary/{itemId}` — register an existing library video as an
  intro (idempotent; never touches the file).
- `GET /Videos` — every video file in every library, for the "add a video already on
  the server" picker. Runs one query per library (`InternalItemsQuery.ParentId`) so the
  library name comes for free instead of walking each item's ancestor chain; the DTO
  carries `Library` and the picker groups by it.
- `GET /Users`, `GET /Targets` (library → series → seasons → episodes tree; movies are
  direct children of their library).
- `POST /Test/{itemId}?userId=` — evaluate rules without playing.

Config is saved through Jellyfin's standard `get/updatePluginConfiguration`.

## 6. Repo layout

- `Jellyfin.Plugin.IntroReplacer/Plugin.cs` — `BasePlugin<PluginConfiguration>`,
  `IHasWebPages`, static `Instance`/`LibraryManager`; creates `AssetStore`,
  `SessionTracker`, `IntroLibrary`.
- `Jellyfin.Plugin.IntroReplacer/PluginServiceRegistrator.cs` — registers the
  `IMediaSegmentProvider`.
- `IntroProvider.cs` — `IIntroProvider`: rule match → hidden-library resolution →
  placement.
- `Services/RuleEngine.cs` — pure first-match-wins engine (unit-tested).
- `Services/RuleContext.cs` — primitive snapshot.
- `Services/IntroMatcher.cs` — builds `RuleContext` from a `BaseItem`.
- `Services/IntroAssetStore.cs` — files under
  `<PluginConfigurationsPath>/IntroReplacer/intros/`.
- `Services/IntroLibraryManager.cs` — the hidden library (create/find/hide).
- `Services/IntroChapterDetector.cs` — chapter → intro range (unit-tested).
- `Services/IntroSegmentProvider.cs` — `IMediaSegmentProvider` (chapters → Intro).
- `Services/IntroSegmentHelper.cs` — apply `Replace`/`Prepend`.
- `Services/IntroSessionTracker.cs` — pure, unit-tested; wait-time + once-per-session
  bookkeeping (`Plugin.cs` wires `ISessionManager.SessionEnded` into it).
- `Api/IntroReplacerController.cs` — REST endpoints.
- `Configuration/config.html` — dashboard page (inline JS, no external controller).
  Follows the vanilla Control Panel markup: a `<form>` (which the global
  `form { max-width: 54em }` limits), `h1.pageTitle` + `h2.sectionTitle` headers,
  `inputContainer`/`checkboxContainer`/`checkboxList` blocks, `raised` buttons and a
  `raised button-submit block` Save. Inside a rule the order is Name, Active-for-users,
  Intros, Playback, Placement, Frequency (+ a per-rule minutes stepper shown only for
  `OncePerInterval`), the collapsible Targets tree (libraries → series → seasons →
  episodes) and the tag/genre/studio fields. The "add a video from a library" picker is a
  hand-built modal (`.dialogContainer > .dialog.formDialog.paperList`) because plugin
  pages cannot `require` `dialogHelper` (`window.require`/`window.define` do not
  exist in 10.10). Custom elements (`is="emby-input|emby-checkbox|emby-select|
  emby-button|paper-icon-button-light"`) **must be built from HTML strings and
  inserted into the document** — the v0 polyfill upgrades on MutationObserver, so
  `document.createElement` + `setAttribute('is', …)` never upgrades (and would crash
  `emby-input`, whose `createdCallback` touches `parentNode`).
- `Jellyfin.Plugin.IntroReplacer.Tests/` — `RuleEngineTests` (16),
  `IntroChapterDetectorTests` (8), `IntroSessionTrackerTests` (8 methods / 9 cases)
  → 33 passing.

## 7. Commands

```bash
# Build / test  (system .NET 8 SDK + ASP.NET Core 8 runtime are installed)
dotnet build IntroReplacer.slnx
dotnet test Jellyfin.Plugin.IntroReplacer.Tests/Jellyfin.Plugin.IntroReplacer.Tests.csproj

# Publish single DLL
dotnet publish Jellyfin.Plugin.IntroReplacer/Jellyfin.Plugin.IntroReplacer.csproj -c Release -o bin/publish

# Docker test environment
bash scripts/generate-test-media.sh
bash scripts/deploy-plugin.sh                       # publish + copy DLL into docker Jellyfin
docker compose -f docker/compose.yaml up -d         # restart container after each deploy
```

Restart the container after every deploy or the plugin won't reload.

## 8. Docker test environment

- Image `jellyfin/jellyfin:10.10.7`; container `introreplacer-jellyfin`; UI on
  http://localhost:8096 (also 8920).
- Volumes: `./docker/jellyfin/config:/config`, `./docker/jellyfin/cache:/cache`,
  `./media:/media`.
- Plugin DLL: `docker/jellyfin/config/plugins/IntroReplacer/Jellyfin.Plugin.IntroReplacer.dll`.
- Plugin config XML: `docker/jellyfin/config/plugins/configurations/Jellyfin.Plugin.IntroReplacer.xml`.
- Intro files: `docker/jellyfin/config/plugins/configurations/IntroReplacer/intros/`.
- DB: `docker/jellyfin/config/data/jellyfin.db` (SQLite; owned by root, so from the
  host use a throwaway container to read/write it, e.g.
  `docker run --rm -v "$PWD/docker/jellyfin/config:/config" -v script.py:/s.py python:3-alpine python /s.py`).
- **Credentials:** `root` / `root` (works). Remember the login body uses `Pw`, not
  `Password` (see §3b). Since the DB is root-owned, use a throwaway container to
  read/write it, e.g.
  `docker run --rm -v "$PWD/docker/jellyfin/config:/config" -v script.py:/s.py python:3-alpine python /s.py`.

### Current test data (Pokémon)

- Library "Serie TV" id `c0dd427d-fcb3-7370-b38d-562fe4c21ecd` (`/media/Shows`).
- Series `Pokémon` id `2f7531d8-56d0-44c9-47a1-3b22cf285ca2`.
- Episode S01E01 id `e399a9640a1eced9c766b5f86b1811d5` — has 7 Italian-named chapters
  ("Sigla d'apertura" at 0, "Prologo", "Titolo", "Parte A", …).
- Intro asset id `74f5b38c-cd87-491f-b2d7-55672bddf6a5`, file
  `85a31089f6f24389b17d990044c7e5c8.mp4`.
- Root user id `8753a2ac-746d-4ceb-8b63-26d3e1870089`.
- There is also a leftover test show "Dette er en test show" in the library.

## 9. Hard constraints / gotchas (learned the hard way)

- `IMediaSegmentManager` lives in namespace `MediaBrowser.Controller` (folder
  misspelled `MediaSegements`). `MediaSegmentType` is in `Jellyfin.Data.Enums`.
  `IntroInfo` is in `MediaBrowser.Controller.Library`.
- `IItemRepository` (namespace `MediaBrowser.Controller.Persistence`) is the way to
  read chapters in a plugin: `GetChapters(item)` (registered as a singleton in DI).
  `IChapterManager` only has `SaveChapters`.
- `ChapterInfo` has only `StartPositionTicks` (no end) — compute the end from the next
  chapter or the item runtime.
- Do **not** bundle Jellyfin/framework DLLs: `CopyLocalLockFileAssemblies=false`
  keeps the publish output to just our DLL.
- Rules are ANDed; an empty condition list matches everything.
- `IntroSessionTracker` is deliberately free of Jellyfin types (like `RuleEngine` and
  `IntroChapterDetector`) so it stays unit-testable; the `ISessionManager.SessionEnded`
  subscription lives in `Plugin.cs`. Don't inject `ISessionManager` into the tracker
  itself — the test project does not load `MediaBrowser.Controller` at runtime.
- "Session" for the `OncePerSession` frequency means the Jellyfin session
  (`SessionEnded` on client disconnect), resolved from the request's
  `Jellyfin-DeviceId` claim. The claim constant lives in `Jellyfin.Api.Constants
  .InternalClaimTypes`, which plugins cannot reference, so the literal is used.

## 10. Next steps / open items

1. **Confirm in the browser** (session 3 decision): play a Pokémon episode; B should
   play, then A should start at 0 with a "Skip Intro" button. To make it hands-free,
   each client sets `Playback → Media Segment Actions → Intro Segments → Skip`.
   Remember §2d: the pre-roll only appears when playback starts at index 0 of a list
   (first episode / "Play" on the series), not when starting from a later episode or a
   resume position, and not on queue auto-advance.
2. **Optional, if the user wants a universal pre-roll:** HLS-level splicing
   (Projectionist-style) intercepts `/Videos/{id}/*.m3u8` and prepends the intro's
   segments. That fixes queues and mid-list episode starts on every client, at the cost
   of forcing a transcode/remux. Needs an explicit go-ahead before starting.
2. Optional: implement audio-based intro detection (or document the Intro Skipper
   dependency more prominently in the UI).
3. Optional: make `GET /Targets` lazy (it currently builds the whole
   series/season/episode tree in one response; fine for small libraries).
4. Optional: only re-apply user hiding when the library was actually (re)created.
5. Optional: run/trigger "Media Segment Scan" right after config changes so segments
   never depend on a later scheduled run.
6. `manifest.json` still has empty `sourceUrl`/`checksum` — fill before publishing.
7. `tester` user (`tester`/`tester`) exists for multi-user tests — keep or delete.
