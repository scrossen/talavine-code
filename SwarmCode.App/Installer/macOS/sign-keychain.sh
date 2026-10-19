#!/usr/bin/env bash
# Re-signs a built Talavine Code.app so its Keychain KEK is reachable (no -34018
# MissingEntitlement). The AppKit host preserves the data-protection keychain, which
# requires an application identifier + keychain-access-groups entitlement that a
# provisioning profile authorizes; a plain signature carries neither.
#
# Usage: sign-keychain.sh <path/to/Talavine Code.app>   (modified in place)
# Env:   MACOS_PROVISIONING_PROFILE_PATH / MACOS_CODESIGN_IDENTITY override the
#        automatic profile+identity resolution shared with Talavine.
set -euo pipefail
APP="${1:?Usage: sign-keychain.sh <Talavine Code.app>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
RESOLVER="$REPO_ROOT/Installer/macOS/resolve-keychain-signing.py"
PROJECT="$REPO_ROOT/SwarmCode.App/SwarmCode.App.csproj"
INFO="$APP/Contents/Info.plist"
[[ -f "$INFO" ]] || { echo "Not an app bundle: $APP" >&2; exit 1; }

BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$INFO")"
ARGS=(--bundle-id "$BUNDLE_ID")
[[ -z "${MACOS_CODESIGN_IDENTITY:-}" ]] || ARGS+=(--identity "$MACOS_CODESIGN_IDENTITY")
[[ -z "${MACOS_PROVISIONING_PROFILE_PATH:-}" ]] || ARGS+=(--profile "$MACOS_PROVISIONING_PROFILE_PATH")
VALUES=()
while IFS= read -r line; do VALUES+=("$line"); done < <(python3 "$RESOLVER" "${ARGS[@]}")
IDENTITY="${VALUES[1]}"; PROFILE="${VALUES[2]}"; TEAM="${VALUES[3]}"

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
# Start from the entitlements the build already sealed so nothing is dropped.
codesign -d --entitlements :- "$APP" 2>/dev/null > "$WORK/entitlements.plist" || true
[[ -s "$WORK/entitlements.plist" ]] || printf '<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict/></plist>' > "$WORK/entitlements.plist"
# CoreCLR requires JIT authorization under hardened runtime, including after a
# failed/incomplete MSBuild signing pass. Do not preserve a broken empty seal.
python3 - "$WORK/entitlements.plist" <<'PY'
import plistlib, sys
from pathlib import Path
path = Path(sys.argv[1])
entitlements = plistlib.loads(path.read_bytes())
entitlements['com.apple.security.cs.allow-jit'] = True
path.write_bytes(plistlib.dumps(entitlements))
PY
python3 "$RESOLVER" --bundle-id "$BUNDLE_ID" --profile "$PROFILE" \
  --apply-entitlements "$WORK/entitlements.plist" --app-info "$INFO"
/usr/libexec/PlistBuddy -c "Add :com.apple.developer.team-identifier string $TEAM" "$WORK/entitlements.plist" 2>/dev/null || true

cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"
timestamp=(--timestamp=none)
[[ "$IDENTITY" != 'Developer ID Application:'* ]] || timestamp=(--timestamp)
# Nested code was sealed by the build and bridge target. Replacing its signature
# with the app's Keychain entitlements would broaden helper privileges.
codesign --force --options runtime --sign "$IDENTITY" --entitlements "$WORK/entitlements.plist" "${timestamp[@]}" "$APP"
codesign --verify --deep --strict "$APP"
echo "Signed $APP as $BUNDLE_ID with $(basename "$PROFILE")"
