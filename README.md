# Talavine Code

A coding agent for working in your own repositories, with a terminal interface
and a Windows / Apple Silicon macOS desktop application.

Created by **Scott Crossen**. Part of [Talavine](https://talavine.com).
For consulting and custom integration inquiries, use the contact options on
the Talavine website.

Talavine Code can work through supported native coding CLIs or model APIs,
use repository tools, retain conversations, and run configured acceptance
commands. You choose and authenticate the underlying services. Their subscriptions,
API charges, terms, and availability remain separate.

Formerly SwarmCode: the `swarmcode` executable, `.swarmcode` configuration,
`SWARMCODE_*` variables, application identity, and existing local data paths
keep their original names for compatibility.

## Start with the terminal

Install the .NET SDK selected by `global.json` (currently .NET 11 RC2), and Git.
The CLI solution does not require MAUI workloads.

```sh
dotnet restore TalavineCode.Cli.slnx
dotnet build TalavineCode.Cli.slnx --no-restore
dotnet run --project SwarmCode -- --help
dotnet run --project SwarmCode -- --list-lanes
dotnet run --project SwarmCode -- --repo /path/to/your/repository --permission-mode ask
```

Install and sign in to a supported native CLI such as Claude Code or Codex,
or configure your own supported model API key. Inside the CLI, `/help` lists
commands; `/key openai` prompts for a key without putting it in shell history.
Never commit a key, password export, session directory, or local settings file.

Default permission mode is `auto`; the example above explicitly selects `ask`.
Review permissions before granting access to repositories, commands, browsers,
credentials, or external services. Model-generated commands can change files
and perform external actions. Native CLI restrictions and host tool permissions
have different enforcement boundaries; see [security](SECURITY.md).

```sh
dotnet test SwarmCode.Tests/SwarmCode.Tests.csproj --no-restore
dotnet test SwarmCode.Hosting.Tests/SwarmCode.Hosting.Tests.csproj --no-restore
dotnet test SwarmCore/SwarmCore.Coding.Tests/SwarmCore.Coding.Tests.csproj --no-restore
```

Tests requiring live providers, personal browsers, or native platform facilities
are opt-in or platform-specific. A source build is not desktop installation proof.

## Desktop development

The full solution is `TalavineCode.slnx`. Windows needs Visual Studio with
MSBuild, the Windows SDK, and the matching .NET MAUI Windows workload. Use
Visual Studio MSBuild for Windows XAML compilation. macOS needs Apple Silicon,
the matching MAUI/Catalyst workload, Xcode 27, and an appropriate signing identity
and provisioning profile for Keychain access. Do not use someone else's signing
credentials. See [building](docs/building.md).

## Scope

This repository contains Talavine Code and the shared libraries needed to build
it. It does not include the Talavine personal/business data application, CRM,
website, private databases, private development history, or SwarmSpan's application.
The optional peer bridge requires a separately installed, paired SwarmSpan host.
Talavine Code works locally without that bridge.
See [the source layout](docs/architecture.md) for the included shared libraries.

## Contributing and license

Maintainers publish committed-source updates through an explicit file allowlist,
secret scanning, builds, and tests. Shared SwarmCore source is included here so
building Talavine Code does not require access to another repository. Public
contributions are integrated upstream before export; synchronization stops when
the public branch has changes that need reconciliation.

Start with [CONTRIBUTING.md](CONTRIBUTING.md). Talavine-owned code in this
repository is licensed under [MIT](LICENSE). Third-party files retain their
licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
The MIT license does not imply endorsement of forks or grant ownership of the
Talavine name or logo.
