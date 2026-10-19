#!/usr/bin/env bash
# Convert the existing vector artwork into the standard AppKit icon bundle.
set -euo pipefail
source_svg="${1:?SVG artwork required}"
output="${2:?Output .icns path required}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/appicon.iconset" "$(dirname "$output")"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$source_svg" --out "$work/appicon.iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -s format png -z "$double" "$double" "$source_svg" --out "$work/appicon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$work/appicon.iconset" -o "$output"
