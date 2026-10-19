#!/usr/bin/env bash
# Artifact gate for the AppKit host. Native UI interaction is a separate gate.
set -euo pipefail
app="${1:?Usage: verify-swarmcode-native-macos.sh <Talavine Code.app>}"
info="$app/Contents/Info.plist"
exe="$app/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$info")"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$info")" == com.swarmfamily.swarmcode ]]
[[ "$(lipo -archs "$exe")" == arm64 ]]
otool -l "$exe" | awk '/LC_BUILD_VERSION/{in_build=1;next} in_build && /platform/{if ($2 != 1) exit 1; found=1; in_build=0} END {if (!found) exit 1}'
if find "$app/Contents" -type f -name '*.dll' | rg '/(Microsoft\.Maui[^/]*|Microsoft\.MacCatalyst|Microsoft\.AspNetCore\.Components\.WebView\.Maui)\.dll$' >/dev/null; then
  echo 'MAUI or Catalyst assembly found in the native bundle.' >&2; exit 1
fi
if otool -L "$exe" | rg 'UIKit|MacCatalyst|/iOSSupport/' >/dev/null; then
  echo 'UIKit/Catalyst dependency found in the native executable.' >&2; exit 1
fi
for asset in index.html css/app.css css/talavine-code-design.css js/swarmcode.js brand/swarmcode-mark.svg _content/SwarmCore.Chat.Components/chat-components.css _content/SwarmCore.Chat.Components/chat-components.js; do
  [[ -s "$app/Contents/Resources/wwwroot/$asset" ]] || { echo "Missing bundled asset: $asset" >&2; exit 1; }
done
[[ -s "$app/Contents/Resources/appicon.icns" ]]
[[ -s "$app/Contents/MonoBundle/appsettings.json" ]]
bridge="$app/Contents/Resources/SwarmCodeBridge"
for file in swarmcode libhostfxr.dylib libcoreclr.dylib; do [[ -s "$bridge/$file" ]]; done
codesign --verify --deep --strict "$app"
# A valid signature alone can still omit CoreCLR's required JIT entitlement.
# Refuse such a bundle before launching it and generating a macOS crash alert.
codesign -d --entitlements :- "$app" 2>/dev/null | python3 -c 'import plistlib,sys; e=plistlib.loads(sys.stdin.buffer.read()); sys.exit(0 if e.get("com.apple.security.cs.allow-jit") is True else "Missing required JIT entitlement; rebuild/sign the app before launching it.")'
startup="$("$exe" --verify-runtime-startup)"
[[ "$startup" == 'PASS: native runtime started.' ]]
# Verify desktop driver discovery separately from the bundled CLI.
driver="$(env -u PLAYWRIGHT_DRIVER_SEARCH_PATH -u PLAYWRIGHT_NODEJS_PATH "$exe" --verify-playwright-driver)"
[[ "$driver" == 'PASS: bundled Playwright driver started.' ]]
# Run the self-contained helper without an SDK or an inherited .NET root.
version="$(env -u DOTNET_ROOT -u DOTNET_ROOT_ARM64 DOTNET_MULTILEVEL_LOOKUP=0 "$bridge/swarmcode" --version)"
[[ "$version" == 'Talavine Code '* ]]
echo 'PASS: Apple Silicon AppKit artifact, no MAUI/Catalyst, bundled web assets/icon, strict signature, native runtime startup, native Playwright driver, SDK-free CLI bridge.'
