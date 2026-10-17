#!/usr/bin/env bash
# Builds the plugin and copies the DLL into the local Docker Jellyfin's plugin folder.
# After running this, restart the container: docker compose -f docker/compose.yaml restart
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$ROOT/bin/publish"
DEST="$ROOT/docker/jellyfin/config/plugins/IntroReplacer"

dotnet publish "$ROOT/Jellyfin.Plugin.IntroReplacer/Jellyfin.Plugin.IntroReplacer.csproj" \
    -c Release -o "$PUBLISH_DIR"

mkdir -p "$DEST"
cp "$PUBLISH_DIR/Jellyfin.Plugin.IntroReplacer.dll" "$DEST/"

echo "Deployed $(basename "$DEST")/Jellyfin.Plugin.IntroReplacer.dll"
echo "Restart Jellyfin: docker compose -f docker/compose.yaml restart"
