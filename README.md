# Intro Replacer (Jellyfin plugin)

‼️ **NOTE: I've coded this using DeepSeek mainly for personal use (as you can probably tell from this README). Feel free to use this plugin if you want or even propose improvements, but don't expect maximum efficiency or code cleanliness.**

‼️ **NOTE: in order to get the "replacing intro" trick working, you need to set your Jellyfin user's Intro Segments global setting to Skip.
Otherwise, the custom intro will only be prepended before the episode.**


Plays a **custom intro** before an episode or movie, then lets the client skip the
item's own intro — effectively replacing it. Works on any client that supports
Jellyfin intros (the server-side `IIntroProvider` mechanism, same as the official
Local Intros plugin).

> Target: **Jellyfin 10.10.x** (.NET 8).

## How it works

- A rule engine (first-match-wins) picks an intro file. Per rule you choose **one**
  targeting method with a radio button: the **Targets** tree (library → series → season
  → episode/movie) or **Tags** (include/exclude tags, genres, studios). The other method
  is greyed out and kept, so you can switch back without losing it. Users and the
  current/release date windows apply to both.
- The rule header always shows the result of the selected method (e.g.
  `Targets — 1 series, 2 seasons, 20 episodes` or `Tags — 12 episodes, 3 movies`),
  even when the rule is collapsed. In the target tree an empty selection means
  "all items".
- Per rule you choose **Replace** (skip the item's own intro) or **Prepend** (play
  the item from the beginning), and **how often the intro plays**: every time you start
  a title, only after a per-rule wait time you set, or once per session (again after you
  close the app or sign out).
- Uploaded intro files are stored in the plugin data folder (configurable, see below)
  and registered through a **hidden internal library** (`Intro Replacer`), hidden from
  every user's home. This is required: Jellyfin silently drops intro files that are not
  scanned library items. No media files are rewritten or duplicated. Files copied into
  the intro folder are registered automatically.
- You can also use **any video already in your libraries** (movies, episodes, other
  videos) as an intro — nothing is copied or modified. Deleting such an intro only
  removes it from the plugin's list, never the media file.
- For **Replace**, if the item has no `Intro` media segment yet, one is derived from
  its chapters (e.g. a "Opening"/"Intro" chapter) so clients offer to
  skip the original intro. Existing segments (e.g. from Intro Skipper) are kept.
  Automatic audio-based detection is **not** implemented — install Intro Skipper for
  items without chapters.
- **Skipping is decided by the client, not the server.** By default Jellyfin shows a
  **Skip Intro button** (for ~8 s) while the segment plays; it never seeks by itself.
  For hands-free skipping, each client sets
  `Playback → Media Segment Actions → Intro Segments → Skip` (a per-client/browser
  preference — Jellyfin 10.10 has no server-side equivalent).

There is **no** per-item "3-dots" menu: Jellyfin has no cross-client plugin API for
that. Assignment happens on the plugin's dashboard page (`Dashboard → Plugins → Intro Replacer`),
which is styled like the rest of the Control Panel: collapsible rule cards, Jellyfin
checkboxes and dropdowns, and a modal to browse your libraries and pick a video.

> Client note: movies get intros on every client. Episode intros are handled by the
> web client (browser and Jellyfin Media Player). Some native TV/mobile apps ignore
> episode intros; movies still work there.
>
> Client note: the intro is played **before the first title you start**. Jellyfin's web
> client only requests it when playback begins at the start of a list, so it appears
> when you press Play on a series/movie or on the first episode, not when you jump to a
> later episode or resume one, and not on automatic "next episode" transitions. This is
> a client limitation; the server is never asked in those cases.

## Intro storage folder (Docker)

Uploaded intro files live in the plugin data folder by default. To keep them on a
larger drive, create a dedicated folder on the host and mount it into the container:

```yaml
volumes:
  - /mnt/media/Intros:/media/Intros
```

Then set the plugin's **Intro folder** to the **container path** `/media/Intros`
(not the host path). The plugin moves existing intro files there, registers the folder
as its hidden library and scans it. Use a dedicated folder, not a Series/Movies library
root, or the clips will also appear in that library.

Permissions: the official `jellyfin/jellyfin` image runs as **root**, so folders it
creates under `/config` are root-owned. Create the host folder as your own user
(`mkdir -p /mnt/media/Intros`); the container can still write into it, and you can drop
files there. For the default folder, fix ownership once with:

```bash
sudo chown -R 1000:1000 <jellyfin config>/plugins/configurations/IntroReplacer
```

## Install (plugin catalog)

1. Dashboard → Plugins → Repositories → **+** and add this URL:

   ```
   https://raw.githubusercontent.com/Flaconia/jellyfin-plugin-intro-replacer/main/manifest.json
   ```

   It must be the **raw** URL. A `github.com/.../blob/...` URL returns HTML, which
   Jellyfin cannot parse, and the catalog then stays empty.
2. Plugins → Catalog → **Intro Replacer** → Install, then restart Jellyfin.

The catalog entry comes from [`manifest.json`](manifest.json). Releases are built by
[`.github/workflows/release.yaml`](.github/workflows/release.yaml): pushing a tag such as
`v1.0.0.0` publishes `Jellyfin.Plugin.IntroReplacer_<version>.zip` and rewrites the
manifest's `sourceUrl` and `checksum`. Jellyfin verifies the downloaded zip's **MD5**
against that checksum, so a stale checksum makes the install fail.

Verify the manifest (locally, or against the published URL) with:

```bash
bash scripts/check-manifest.sh
bash scripts/check-manifest.sh https://raw.githubusercontent.com/Flaconia/jellyfin-plugin-intro-replacer/main/manifest.json
```

### If the plugin does not appear in the catalog

- The repository must be **public**: Jellyfin fetches the manifest without
  authentication, so a private repo always results in an empty catalog.
- `curl -sI <raw manifest URL>` must return `200`, and `curl -s <raw manifest URL>` must
  start with `[`. Otherwise Jellyfin logs
  `An error occurred while accessing the plugin manifest` and lists nothing.
- The manifest needs at least one version whose `targetAbi` is not newer than the
  server (this plugin: `10.10.0.0`, for Jellyfin 10.10.x).

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

## Translations

The dashboard page and the plugin's API messages are localized. Strings live in
[`Jellyfin.Plugin.IntroReplacer/Localization/`](Jellyfin.Plugin.IntroReplacer/Localization)
as flat JSON files — `en.json` is the source language and any other file is named after
the Jellyfin locale (`it.json`, `de.json`, `pt-BR.json`, …). The files are embedded in
the DLL, so **adding a language needs no code change**: copy `en.json`, translate the
values (keep the keys and `{placeholders}` unchanged) and rebuild. The plugin resolves
the admin's locale (`pt-BR` → `pt` → `en`) and falls back to English for missing keys.
Italian (`it.json`) is included.

The folder is shaped for [Weblate](https://weblate.org) as a monolingual *JSON file*
component (base file `en.json`, mask `*/Localization/*.json`). See
[`Localization/README.md`](Jellyfin.Plugin.IntroReplacer/Localization/README.md) for the
component settings and the translator rules (plural `.one`/`.other` keys, the one HTML
string, and which technical values must not be translated).

## License

GPL-3.0 (see Jellyfin plugin conventions). Owner: `flaconia`.
