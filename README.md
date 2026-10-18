# Intro Replacer (Jellyfin plugin)

Plays a **custom intro** before an episode or movie, then lets the client skip the
item's own intro — effectively replacing it. Works on any client that supports
Jellyfin intros (the server-side `IIntroProvider` mechanism, same as the official
Local Intros plugin).

> Target: **Jellyfin 10.10.x** (.NET 8).

## How it works

- A rule engine (first-match-wins) picks an intro file based on library, users,
  tags, genres, studios, current/release date windows, or a series/season/episode
  target tree.
- Per rule you choose **Replace** (skip the item's own intro) or **Prepend** (play
  the item from the beginning), and whether the intro plays **every time** or
  **once per session**.
- Uploaded intro files are stored in the plugin data folder and registered through a
  **hidden internal library** (`Intro Replacer`), hidden from every user's home. This
  is required: Jellyfin silently drops intro files that are not scanned library
  items. No media files are rewritten or duplicated.
- For **Replace**, if the item has no `Intro` media segment yet, one is derived from
  its chapters (e.g. a "Opening"/"Sigla d'apertura" chapter) so clients offer to
  skip the original intro. Existing segments (e.g. from Intro Skipper) are kept.
  Automatic audio-based detection is **not** implemented — install Intro Skipper for
  items without chapters.
- **Skipping is decided by the client, not the server.** By default Jellyfin shows a
  **Skip Intro button** (for ~8 s) while the segment plays; it never seeks by itself.
  For hands-free skipping, each client sets
  `Playback → Media Segment Actions → Intro Segments → Skip` (a per-client/browser
  preference — Jellyfin 10.10 has no server-side equivalent).

There is **no** per-item "3-dots" menu: Jellyfin has no cross-client plugin API for
that. Assignment happens on the plugin's dashboard page (`Dashboard → Plugins → Intro Replacer`).

> Client note: movies get intros on every client. Episode intros are handled by the
> web client (browser and Jellyfin Media Player). Some native TV/mobile apps ignore
> episode intros; movies still work there.

## Install (release)

1. Dashboard → Plugins → Repositories → add this repo's `manifest.json` URL.
2. Catalog → install **Intro Replacer**, then restart Jellyfin.

## Build from source

Requires the .NET 8 SDK (to build `net8.0`) and, for tests, the ASP.NET Core 8 runtime.

```bash
dotnet build IntroReplacer.slnx

# Tests
dotnet test Jellyfin.Plugin.IntroReplacer.Tests/Jellyfin.Plugin.IntroReplacer.Tests.csproj

dotnet publish Jellyfin.Plugin.IntroReplacer/Jellyfin.Plugin.IntroReplacer.csproj -c Release -o bin/publish
```

Copy `bin/publish/Jellyfin.Plugin.IntroReplacer.dll` into
`<jellyfin config>/plugins/IntroReplacer/` and restart.

## Local test environment (Docker)

```bash
bash scripts/generate-test-media.sh   # creates docker/media with a sample episode + intro
bash scripts/deploy-plugin.sh         # publishes and copies the DLL into the docker Jellyfin
docker compose -f docker/compose.yaml up -d
```

Then open http://localhost:8096, complete setup, add `/media` as a **Shows**
library and scan. Open the plugin's dashboard page, upload the sample intro,
and add a rule (e.g. target the test series). Play the episode: the intro plays
first, then the episode starts.

## License

GPL-3.0 (see Jellyfin plugin conventions). Owner: `flaconia`.
