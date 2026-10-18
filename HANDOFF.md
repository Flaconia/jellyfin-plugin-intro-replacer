# HANDOFF — Intro Replacer (Jellyfin plugin)

Last updated: 2026-10-18 (session 2). Read this first; then `AGENTS.md` for the hard
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
- from `IntroProvider.GetIntros` when the library does not exist yet, and
- from the upload endpoint after a new file is stored (then triggers a scan).

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

- `PluginConfiguration`: `Assets`, `Rules`, `MoviesOnly`, `SessionIdleMinutes` (180).
- `IntroAsset`: `Id`, `Name`, `FileName` (the stored file under the intros folder),
  `Enabled`.
- `IntroRule`: `Id`, `Name`, `Enabled`, `LibraryIds`, `UserIds`, `TargetItemIds`
  (active series/movie ids; empty = all), `ExcludedItemIds` (deactivated season/episode
  ids), `IncludeTags`, `ExcludeTags`, `Genres`, `Studios`, `DateRange`,
  `ReleaseDateRange`, `AssetIds`, `PlaybackMode` (`RandomOne`/`SequentialAll`),
  `Placement` (`Replace`/`Prepend`), `Frequency` (`EveryTime`/`OncePerSession`).
- There is intentionally **no** chance/probability field. (Historically a rule could
  have a 0–100 roll that decided whether a matched rule fired, falling through on
  failure; the user removed it — ordering + conditions decide now.)

REST (`Api/IntroReplacerController.cs`, `[Authorize(Policy = RequiresElevation)]`,
route `[controller]` → `/IntroReplacer/...`):

- `GET /Assets`, `POST /Assets` (multipart `file`), `DELETE /Assets/{id}` — the POST
  also ensures the hidden library and triggers a scan.
- `GET /Libraries`, `GET /Users`, `GET /Targets` (series → seasons → episodes tree).
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
- `Services/IntroSessionTracker.cs` — per-user "once per session" heuristic.
- `Api/IntroReplacerController.cs` — REST endpoints.
- `Configuration/config.html` — dashboard page (inline JS, no external controller).
- `Jellyfin.Plugin.IntroReplacer.Tests/` — `RuleEngineTests` (14),
  `IntroChapterDetectorTests` (8) → 23 passing.

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
- Session semantics are a heuristic (`SessionIdleMinutes`); intro providers get no
  real client session id.

## 10. Next steps / open items

1. **Confirm in the browser** (session 3 decision): play a Pokémon episode; B should
   play, then A should start at 0 with a "Skip Intro" button. To make it hands-free,
   each client sets `Playback → Media Segment Actions → Intro Segments → Skip`.
2. Optional: implement audio-based intro detection (or document the Intro Skipper
   dependency more prominently in the UI).
3. Optional: make `GET /Targets` lazy (it currently builds the whole
   series/season/episode tree in one response; fine for small libraries).
4. Optional: only re-apply user hiding when the library was actually (re)created.
5. Optional: run/trigger "Media Segment Scan" right after config changes so segments
   never depend on a later scheduled run.
6. `manifest.json` still has empty `sourceUrl`/`checksum` — fill before publishing.
7. `tester` user (`tester`/`tester`) exists for multi-user tests — keep or delete.
