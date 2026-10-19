#!/bin/bash
# Invoked only after the app verifies this script against its publisher-signed feed.
set -euo pipefail
umask 077
asset_url='' asset_sha='' asset_version='' target='' wait_pid='' launch=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --asset-url) asset_url="$2"; shift 2 ;;
    --asset-sha256) asset_sha="$2"; shift 2 ;;
    --asset-version) asset_version="$2"; shift 2 ;;
    --target-app) target="$2"; shift 2 ;;
    --wait-pid) wait_pid="$2"; shift 2 ;;
    --no-launch) launch=0; shift ;;
    *) echo "Unknown installer argument: $1" >&2; exit 2 ;;
  esac
done
[[ "$(uname -sm)" == 'Darwin arm64' ]] || { echo 'Apple Silicon macOS is required.' >&2; exit 1; }
[[ "$asset_url" == https://* && "$asset_sha" =~ ^[a-fA-F0-9]{64}$ && "$asset_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
[[ -z "$wait_pid" || "$wait_pid" =~ ^[1-9][0-9]*$ ]]
if [[ -z "$target" ]]; then
  if [[ -w /Applications ]]; then target='/Applications/Talavine Code.app'; else mkdir -p "$HOME/Applications"; target="$HOME/Applications/Talavine Code.app"; fi
fi
[[ "$target" == /* && "$target" == */Talavine\ Code.app && ! -L "$target" ]]
parent="$(cd "$(dirname "$target")" && pwd -P)"
target="$parent/Talavine Code.app"
[[ -w "$parent" ]] || { echo 'The application folder is not writable. Move Talavine Code to your Applications folder and retry.' >&2; exit 1; }
mkdir -p "$HOME/Library/Logs/SwarmCode"
exec >> "$HOME/Library/Logs/SwarmCode/update.log" 2>&1
printf '\nUpdating Talavine Code to %s at %s\n' "$asset_version" "$(date -u +%FT%TZ)"
lock="$parent/.swarmcode-update.lock"
mkdir "$lock" || { echo 'Another installation is in progress.'; exit 1; }
work="$(mktemp -d "$parent/.swarmcode-install.XXXXXX")"
mount="$work/mounted" mounted=0 replaced=0 complete=0
backup="$work/previous.app"
cleanup() {
  result=$?
  if [[ "$mounted" == 1 ]]; then /usr/bin/hdiutil detach "$mount" >/dev/null 2>&1 || true; fi
  if [[ "$replaced" == 1 && "$complete" != 1 ]]; then
    /bin/mv "$target" "$work/failed.app" || true
    [[ ! -d "$backup" ]] || /bin/mv "$backup" "$target"
  fi
  rmdir "$lock" || true
  if [[ "$complete" == 1 ]]; then /bin/rm -rf "$work"; else echo "Update failed; evidence retained in $work (exit $result)."; fi
}
trap cleanup EXIT
/usr/bin/curl --fail --location --proto '=https' --proto-redir '=https' --retry 2 --connect-timeout 30 --max-time 1800 --output "$work/package.dmg" "$asset_url"
actual="$(/usr/bin/shasum -a 256 "$work/package.dmg" | awk '{print $1}')"
[[ "$(echo "$actual" | tr A-F a-f)" == "$(echo "$asset_sha" | tr A-F a-f)" ]] || { echo 'Installer checksum mismatch.'; exit 1; }
/usr/bin/hdiutil verify "$work/package.dmg"
mkdir "$mount"
/usr/bin/hdiutil attach -readonly -nobrowse -mountpoint "$mount" "$work/package.dmg"
mounted=1
source="$mount/Talavine Code.app"
info="$source/Contents/Info.plist"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$info")" == com.swarmfamily.swarmcode ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$info")" == "$asset_version" ]]
/usr/bin/codesign --verify --deep --strict "$source"
/usr/bin/xcrun stapler validate "$source"
/usr/sbin/spctl --assess --type execute "$source"
/usr/bin/ditto "$source" "$work/Talavine Code.app"
/usr/bin/codesign --verify --deep --strict "$work/Talavine Code.app"
/usr/bin/hdiutil detach "$mount"
mounted=0
if [[ -n "$wait_pid" ]]; then
  for ((i=0;i<120;i++)); do kill -0 "$wait_pid" 2>/dev/null || break; /bin/sleep 1; done
  if kill -0 "$wait_pid" 2>/dev/null; then echo 'Application did not close; installation stopped.'; exit 1; fi
fi
# Refuse to replace a different application, a newer install or an app still in use.
if [[ -e "$target" ]]; then
  [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$target/Contents/Info.plist")" == com.swarmfamily.swarmcode ]]
  old_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$target/Contents/Info.plist")"
  awk -v old="$old_version" -v new="$asset_version" 'BEGIN { split(old,a,"."); split(new,b,"."); for(i=1;i<=3;i++){if(a[i]+0>b[i]+0)exit 1;if(a[i]+0<b[i]+0)exit 0;} }'
  if /usr/sbin/lsof -t "$target/Contents/MacOS/SwarmCode.App" >/dev/null 2>&1; then echo 'Application is still running; installation stopped.'; exit 1; fi
  /bin/mv "$target" "$backup"
fi
replaced=1
/bin/mv "$work/Talavine Code.app" "$target"
/usr/bin/codesign --verify --deep --strict "$target"
/usr/sbin/spctl --assess --type execute "$target"
if [[ "$launch" == 1 ]]; then
  launch_args=(-n)
  # LaunchServices does not inherit the helper's environment automatically.
  # Preserve an explicit profile instead of reopening a different user's data.
  [[ -z "${SWARMCODE_HOME:-}" ]] || launch_args+=(--env "SWARMCODE_HOME=$SWARMCODE_HOME")
  /usr/bin/open "${launch_args[@]}" "$target"
fi
complete=1
echo "Installed Talavine Code $asset_version."
