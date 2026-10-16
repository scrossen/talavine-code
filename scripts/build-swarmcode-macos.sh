#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$repo_root/SwarmCode.App/SwarmCode.App.csproj"
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
profile_uuid="$(/usr/bin/security cms -D -i "$MACOS_PROVISIONING_PROFILE_PATH" | python3 -c 'import plistlib,sys; print(plistlib.loads(sys.stdin.buffer.read())["UUID"])')"

"$repo_root/scripts/dotnet11-mac.sh" build "$project" \
  -f net11.0-maccatalyst -c Debug \
  "-p:CodesignKey=$MACOS_CODESIGN_IDENTITY" \
  "-p:CodesignProvision=$profile_uuid" \
  -p:UseHardenedRuntime=true \
  /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

# Seal the concrete Keychain group and public access-group metadata used by the
# app and its bundled native-agent vault bridge before VS Code starts the app.
bash "$repo_root/SwarmCode.App/Installer/macOS/sign-keychain.sh" \
  "$repo_root/SwarmCode.App/bin/Debug/net11.0-maccatalyst/maccatalyst-arm64/Talavine Code.app"
