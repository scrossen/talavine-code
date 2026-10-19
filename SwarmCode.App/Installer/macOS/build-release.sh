#!/bin/bash
# Build one immutable, Developer ID signed, notarized Apple Silicon installer.
set -euo pipefail
[[ "$(uname -sm)" == 'Darwin arm64' ]]
repo="$(cd "$(dirname "$0")/../../.." && pwd -P)"
version="${VERSION:?Set VERSION to an exact x.y.z release version}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
export MACOS_NOTARY_KEYCHAIN_PROFILE="${MACOS_NOTARY_KEYCHAIN_PROFILE:?A verified notarytool Keychain profile is required}"
export CONFIGURATION=Release
# Refuse development signing before starting the build.
metadata="$(python3 "$repo/Installer/macOS/resolve-keychain-signing.py" --project "$repo/SwarmCode.App/SwarmCode.App.csproj")"
values=(); while IFS= read -r value; do values+=("$value"); done <<< "$metadata"
[[ "${values[1]}" == 'Developer ID Application:'* && "${values[5]}" == false ]]
export MACOS_CODESIGN_IDENTITY="${values[1]}" MACOS_PROVISIONING_PROFILE_PATH="${values[2]}"
xcrun notarytool history --keychain-profile "$MACOS_NOTARY_KEYCHAIN_PROFILE" >/dev/null
output="${SWARMCODE_RELEASE_DIR:-$repo/.artifacts/swarmcode-release/$version}"
mkdir -p "$output/evidence"
output="$(cd "$output" && pwd -P)"
dmg="$output/SwarmCode-macos-arm64-$version.dmg"
[[ ! -e "$dmg" ]] || { echo 'An installer already exists; verify/resume it without rebuilding.' >&2; exit 1; }
"$repo/scripts/dotnet11-mac.sh" test "$repo/SwarmCode.App.Tests/SwarmCode.App.Tests.csproj" -c Release --logger "trx;LogFileName=$output/evidence/app-tests.trx"
"$repo/scripts/dotnet11-mac.sh" test "$repo/SwarmCode.Hosting.Tests/SwarmCode.Hosting.Tests.csproj" -c Release --filter 'FullyQualifiedName~MacDataPathCompatibilityTests|FullyQualifiedName~MacVaultBridgeTests|FullyQualifiedName~SecretsTests|FullyQualifiedName~HostToolBridgeHostingTests|FullyQualifiedName~McpBridgeToolsTests' --logger "trx;LogFileName=$output/evidence/hosting-tests.trx"
bash "$repo/scripts/build-swarmcode-macos.sh"
bash "$repo/SwarmCode.App/Installer/macOS/package-release.sh"
