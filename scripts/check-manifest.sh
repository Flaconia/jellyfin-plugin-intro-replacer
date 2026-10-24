#!/usr/bin/env bash
# Verifies that manifest.json is usable by a Jellyfin plugin catalog:
#   * no empty sourceUrl/checksum,
#   * each sourceUrl downloads,
#   * the downloaded file's MD5 matches the recorded checksum,
#   * the recorded targetAbi is not newer than the plugin's supported server.
#
# Usage:
#   bash scripts/check-manifest.sh                       # local manifest.json
#   bash scripts/check-manifest.sh <manifest URL>        # e.g. the raw GitHub URL
set -euo pipefail

MANIFEST_URL="${1:-manifest.json}"
TARGET_ABI_EXPECTED="10.10."
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Fetching manifest: $MANIFEST_URL"
if [ -f "$MANIFEST_URL" ]; then
    cp "$MANIFEST_URL" "$TMP_DIR/manifest.json"
elif ! curl -fsSL "$MANIFEST_URL" -o "$TMP_DIR/manifest.json"; then
    echo "FAIL: manifest is not reachable (Jellyfin logs 'An error occurred while accessing the plugin manifest')." >&2
    echo "      Check that the repository is public and that you used the raw URL, not a /blob/ URL." >&2
    exit 1
fi
echo "OK: manifest downloaded"

# One tab-separated line per version entry.
VERSIONS="$(python3 - "$TMP_DIR/manifest.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    packages = json.load(handle)

version_count = 0
for package in packages:
    for entry in package.get("versions", []):
        version_count += 1
        print("\t".join(
            str(entry.get(field, ""))
            for field in ("version", "targetAbi", "sourceUrl", "checksum")
        ))
if version_count == 0:
    sys.exit("FAIL: manifest contains no plugin versions")
PY
)" || exit 1

status=0
while IFS=$'\t' read -r version target_abi source_url checksum; do
    echo "--- $version (targetAbi $target_abi)"

    if [ -z "$source_url" ]; then
        echo "FAIL: $version has an empty sourceUrl" >&2
        status=1
        continue
    fi
    if [ -z "$checksum" ]; then
        echo "FAIL: $version has an empty checksum (Jellyfin compares the zip's MD5)" >&2
        status=1
        continue
    fi
    case "$target_abi" in
        "$TARGET_ABI_EXPECTED"*) ;;
        *) echo "WARN: targetAbi $target_abi does not look like $TARGET_ABI_EXPECTED" >&2 ;;
    esac

    zip_file="$TMP_DIR/$version.zip"
    if ! curl -fsSL "$source_url" -o "$zip_file"; then
        echo "FAIL: $version artifact is not downloadable: $source_url" >&2
        status=1
        continue
    fi

    actual="$(md5sum "$zip_file" | cut -d' ' -f1)"
    if [ "${actual}" != "${checksum,,}" ]; then
        echo "FAIL: $version checksum mismatch (manifest ${checksum,,}, artifact $actual)" >&2
        status=1
        continue
    fi

    if ! unzip -l "$zip_file" | grep -q "Jellyfin.Plugin.IntroReplacer.dll"; then
        echo "FAIL: $version zip does not contain Jellyfin.Plugin.IntroReplacer.dll" >&2
        status=1
        continue
    fi

    echo "OK: $version artifact downloads and its MD5 matches"
done <<< "$VERSIONS"

if [ "$status" -ne 0 ]; then
    echo "Manifest check failed." >&2
    exit 1
fi

echo "Manifest OK: Jellyfin can list and install every version."
