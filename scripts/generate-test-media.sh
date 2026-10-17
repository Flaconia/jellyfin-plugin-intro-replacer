#!/usr/bin/env bash
# Generates a throwaway test library and a sample intro with ffmpeg.
# The episode is 30s of test pattern; the first 8s stand in for the "original intro".
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MEDIA="$ROOT/docker/media"

mkdir -p "$MEDIA/Shows/Test Show/Season 01" "$MEDIA/Intros"

echo "Generating sample intro..."
ffmpeg -y -loglevel error \
    -f lavfi -i color=c=red:size=640x360:rate=25:duration=3 \
    -f lavfi -i sine=frequency=440:duration=3 \
    -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac \
    "$MEDIA/Intros/sample-intro.mp4"

echo "Generating test episode..."
ffmpeg -y -loglevel error \
    -f lavfi -i testsrc=size=640x360:rate=25:duration=30 \
    -f lavfi -i sine=frequency=330:duration=30 \
    -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac \
    "$MEDIA/Shows/Test Show/Season 01/Test Show S01E01.mkv"

echo "Done. Add /media to a Jellyfin 'Shows' library, scan, then upload"
echo "$MEDIA/Intros/sample-intro.mp4 via the Intro Replacer config page."
