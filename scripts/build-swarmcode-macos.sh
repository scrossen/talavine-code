#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$repo_root/SwarmCode.App/SwarmCode.App.csproj"
configuration="${CONFIGURATION:-Debug}"
[[ "$configuration" == Debug || "$configuration" == Release ]]
resolver="$repo_root/Installer/macOS/resolve-keychain-signing.py"
signing_args=(--project "$project")
[[ -z "${MACOS_CODESIGN_IDENTITY:-}" ]] || signing_args+=(--identity "$MACOS_CODESIGN_IDENTITY")
[[ -z "${MACOS_PROVISIONING_PROFILE_PATH:-}" ]] || signing_args+=(--profile "$MACOS_PROVISIONING_PROFILE_PATH")
# Resolve installed public signing metadata; no account or machine is fixed here.
signing_metadata="$(python3 "$resolver" "${signing_args[@]}")"
signing_values=()
while IFS= read -r value; do signing_values+=("$value"); done <<< "$signing_metadata"
export MACOS_CODESIGN_IDENTITY="${signing_values[1]}"
export MACOS_PROVISIONING_PROFILE_PATH="${signing_values[2]}"
if [[ "$configuration" == Release ]]; then
  [[ "$MACOS_CODESIGN_IDENTITY" == 'Developer ID Application:'* && "${signing_values[5]}" == false ]] || { echo 'Public Release builds require a Developer ID distribution profile.' >&2; exit 1; }
fi
profile_uuid="$(/usr/bin/security cms -D -i "$MACOS_PROVISIONING_PROFILE_PATH" | python3 -c 'import plistlib,sys; print(plistlib.loads(sys.stdin.buffer.read())["UUID"])')"
# MSBuild treats commas as property separators, even inside one shell argument.
msbuild_identity="${MACOS_CODESIGN_IDENTITY//,/%2C}"

# An isolated artifacts root allows validation without replacing a running app.
build_args=(build "$project" -f net11.0-macos -c "$configuration"
  "-p:CodesignKey=$msbuild_identity"
  "-p:CodesignProvision=$profile_uuid"
  -p:UseHardenedRuntime=true
  /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary)
app_bundle="$repo_root/SwarmCode.App/bin/$configuration/net11.0-macos/osx-arm64/Talavine Code.app"
if [[ -n "${VERSION:-}" ]]; then
  [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
  IFS=. read -r major minor patch <<< "$VERSION"
  code=$((10#$major * 10000 + 10#$minor * 100 + 10#$patch))
  build_args+=("-p:ApplicationDisplayVersion=$VERSION" "-p:ApplicationVersion=$code" "-p:Version=$VERSION")
fi
if [[ -n "${SWARMCODE_ARTIFACTS_PATH:-}" ]]; then
  build_args+=(--artifacts-path "$SWARMCODE_ARTIFACTS_PATH")
  output_metadata="$("$repo_root/scripts/dotnet11-mac.sh" msbuild "$project" \
    -p:TargetFramework=net11.0-macos "-p:Configuration=$configuration" "-p:ArtifactsPath=$SWARMCODE_ARTIFACTS_PATH" \
    -getProperty:OutputPath,ApplicationTitle)"
  app_bundle="$(python3 -c 'import json,pathlib,sys; p=json.loads(sys.stdin.read())["Properties"]; print(pathlib.Path(p["OutputPath"]) / (p["ApplicationTitle"] + ".app"))' <<< "$output_metadata")"
fi
"$repo_root/scripts/dotnet11-mac.sh" "${build_args[@]}"

# Seal the concrete Keychain group and public access-group metadata used by the
# app and its bundled native-agent vault bridge before VS Code starts the app.
bash "$repo_root/SwarmCode.App/Installer/macOS/sign-keychain.sh" \
  "$app_bundle"

# Do not launch an incomplete or accidentally Catalyst-based artifact.
bash "$repo_root/scripts/verify-swarmcode-native-macos.sh" \
  "$app_bundle"
