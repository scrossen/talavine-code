#!/bin/bash
# Resume packaging a verified Release build without rebuilding or rerunning tests.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../../.." && pwd -P)"
version="${VERSION:?An exact release version is required}"
output="${SWARMCODE_RELEASE_DIR:-$repo/.artifacts/swarmcode-release/$version}"
dmg="$output/SwarmCode-macos-arm64-$version.dmg"
: "${MACOS_NOTARY_KEYCHAIN_PROFILE:?Notary Keychain profile is required}"
metadata="$(python3 "$repo/Installer/macOS/resolve-keychain-signing.py" --project "$repo/SwarmCode.App/SwarmCode.App.csproj")"
values=(); while IFS= read -r value; do values+=("$value"); done <<< "$metadata"
[[ "${values[1]}" == 'Developer ID Application:'* && "${values[5]}" == false ]]
export MACOS_CODESIGN_IDENTITY="${values[1]}"
source_app="$repo/SwarmCode.App/bin/Release/net11.0-macos/osx-arm64/Talavine Code.app"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$source_app/Contents/Info.plist")" == "$version" ]]
bash "$repo/scripts/verify-swarmcode-native-macos.sh" "$source_app"
python3 - "$output/evidence" <<'CHECK'
import pathlib,sys,xml.etree.ElementTree as ET
for name in ('app-tests.trx','hosting-tests.trx'):
 r=ET.parse(pathlib.Path(sys.argv[1])/name).getroot();c=r.find('.//{*}Counters')
 assert c is not None and int(c.attrib['passed'])>0 and int(c.attrib['failed'])==0 and int(c.attrib['notExecuted'])==0, name
CHECK
signature="$(codesign -dvv "$source_app" 2>&1)"
grep -q '^Authority=Developer ID Application:' <<< "$signature"
app="$output/Talavine Code.app"
[[ ! -e "$app" ]] || { echo "Candidate app already exists; resume notarization from its evidence." >&2; exit 1; }
ditto "$repo/SwarmCode.App/bin/Release/net11.0-macos/osx-arm64/Talavine Code.app" "$app"
# Notarize and staple the app before making the outer disk image.
ditto -c -k --keepParent "$app" "$output/notary-app.zip"
xcrun notarytool submit "$output/notary-app.zip" --keychain-profile "$MACOS_NOTARY_KEYCHAIN_PROFILE" --wait --output-format json > "$output/evidence/app-notary.json"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("App notarization:",d["status"],d["id"]); assert d["status"]=="Accepted"' "$output/evidence/app-notary.json"
xcrun stapler staple "$app"
xcrun stapler validate "$app"
spctl --assess --type execute --verbose=2 "$app"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/Talavine Code.app"
ln -s /Applications "$stage/Applications"
printf 'Drag Talavine Code.app into Applications, then open it.\nUpdates are checked at startup and daily; use Check for Updates in the app menu at any time.\nRequires Apple Silicon and macOS 14 or newer.\n' > "$stage/Install Talavine Code.txt"
hdiutil create -volname 'Talavine Code' -srcfolder "$stage" -format UDZO "$dmg"
codesign --force --timestamp --sign "$MACOS_CODESIGN_IDENTITY" "$dmg"
xcrun notarytool submit "$dmg" --keychain-profile "$MACOS_NOTARY_KEYCHAIN_PROFILE" --wait --output-format json > "$output/evidence/dmg-notary.json"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("DMG notarization:",d["status"],d["id"]); assert d["status"]=="Accepted"' "$output/evidence/dmg-notary.json"
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
hdiutil verify "$dmg"
shasum -a 256 "$dmg" > "$output/evidence/installer.sha256"
cp "$repo/SwarmCode.App/Installer/macOS/install.sh" "$output/SwarmCode-install-macos-$version.sh"
git -C "$repo" rev-parse HEAD > "$output/evidence/source-revision.txt"
git -C "$repo" diff --binary > "$output/evidence/source.patch"
printf 'Notarized candidate ready: %s\nRun the mounted-app, native UI and exact-updater smoke gates before publication.\n' "$dmg"
