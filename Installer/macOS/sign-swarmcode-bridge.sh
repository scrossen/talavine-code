#!/bin/bash
set -euo pipefail
helper=${1:?bundle helper directory required}
identity=${2:?parent signing identity required}
entitlements=${3:?bridge entitlements required}
[[ -f "$helper/swarmcode" && -f "$helper/libhostfxr.dylib" && -f "$helper/libcoreclr.dylib" ]] || { echo 'Incomplete self-contained vault bridge' >&2; exit 1; }
/usr/bin/xattr -cr "$helper"
chmod u+x "$helper/swarmcode"
sign_options=(--force --sign "$identity")
if [[ "$identity" == '-' ]]; then
  sign_options+=(--timestamp=none)
else
  sign_options+=(--timestamp --options runtime)
fi
while IFS= read -r -d '' binary; do
  [[ "$binary" == "$helper/swarmcode" ]] && continue
  if /usr/bin/file -b "$binary" | /usr/bin/grep -q 'Mach-O'; then
    /usr/bin/codesign "${sign_options[@]}" "$binary"
    /usr/bin/codesign --verify --strict "$binary"
  fi
done < <(/usr/bin/find "$helper" -type f -print0)
if [[ "$identity" == '-' ]]; then
  /usr/bin/codesign "${sign_options[@]}" "$helper/swarmcode"
else
  /usr/bin/codesign "${sign_options[@]}" --entitlements "$entitlements" "$helper/swarmcode"
fi
/usr/bin/codesign --verify --strict "$helper/swarmcode"
# Runtime closure must start without relying on an installed dotnet SDK.
env -u DOTNET_ROOT -u DOTNET_ROOT_ARM64 "$helper/swarmcode" --version
