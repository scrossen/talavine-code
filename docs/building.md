# Building Talavine Code

Use the SDK in `global.json`. The CLI and shared libraries target .NET 11;
this source snapshot currently uses release-candidate SDK and package versions.
An SDK/workload upgrade should be validated as a separate change.

## Terminal

Restore and build `TalavineCode.Cli.slnx`; it excludes the native desktop application.
Run `dotnet run --project SwarmCode -- --help` to verify the executable starts
without contacting a model or opening a user profile.

The CLI solution also builds seven test projects. Run the seven `dotnet test`
commands in `.github/workflows/ci.yml` after building. The RemoteControl suites
cover command/session behavior, agent procedures and portable macOS mapping;
native platform interaction requires separate acceptance.

For a local framework-dependent CLI distribution:

```sh
dotnet publish SwarmCode/SwarmCode.csproj -c Release -o artifacts/cli
```

## Windows desktop

Install Visual Studio with its MSBuild, Windows SDK, and .NET MAUI Windows
development components. Install the matching SDK/workload first. From a Visual
Studio developer shell, restore then build with the Visual Studio MSBuild:

```powershell
dotnet restore SwarmCode.App/SwarmCode.App.csproj
msbuild SwarmCode.App/SwarmCode.App.csproj /p:Configuration=Debug /p:TargetFramework=net11.0-windows10.0.19041.0
```

`AppxMSBuildToolsPath` may be supplied explicitly when the local Visual Studio
layout is not automatically detected. Desktop installation, signing, and native
interaction require separate checks beyond CLI CI.

## Apple Silicon macOS desktop

The Mac host uses AppKit and WKWebView with the shared Blazor UI.
Install the pinned SDK and matching .NET macOS workload, and Xcode 27. Point
`DEVELOPER_DIR` at the appropriate Xcode if necessary. The public SDK wrapper
uses the installed `dotnet`; it does not install tools or modify your system.

Use your own provisioning profile and signing identity, discoverable from local
Keychain/profile metadata or selected with `MACOS_CODESIGN_IDENTITY` and
`MACOS_PROVISIONING_PROFILE_PATH`. Then run:

```sh
bash scripts/build-swarmcode-macos.sh
```

Keychain access requires a profile that authorizes the application identifier.
For independent distribution, use your own app identifier and signing setup.
Never commit provisioning profiles, certificates, credentials, or signed bundles.
The bundle includes a separately built console bridge; its signing script is
included under `Installer/macOS`. Native Mac build and runtime verification are
separate from the Windows CLI CI lane.

The build script runs `scripts/verify-swarmcode-native-macos.sh` to check the
bundle, signature, runtime, and CLI bridge. It requires `rg` (ripgrep) alongside
the native Apple command-line tools. Native interaction and Keychain acceptance
still require a Mac; the Windows CI lane does not establish those results.
