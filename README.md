# Talavine Code

**A coding harness designed to be unleashed.**

Give it the job and let it work. Talavine Code brings your coding agents, model
APIs, repositories, and personal apps into one desktop and terminal workspace.
It's built for people who want to get from an idea to working software without
approving the same job one tool call at a time.

**[Download the Windows installer](https://talavine.com/talavine-code#download)** ·
[See what it does](https://talavine.com/talavine-code) ·
[Build from source](#start-with-the-terminal) · [Report an issue](https://github.com/scrossen/talavine-code/issues)

## Why try another coding harness?

- **Your prompt is the go-ahead.** In the default `auto` mode, a user-requested
  coding turn can edit files and run commands without a stream of Allow dialogs.
  Choose `ask-risky` or `ask` when you want more approval steps. Explicit deny
  rules still apply; see [security](SECURITY.md) for native CLI boundaries.
- **One model's refusal doesn't have to end the job.** Configure a mix of
  providers, including models with more permissive policies. If one declines or
  can't finish, the harness can hand the remaining work to another model in the
  same conversation. You choose the services and fallback order.
- **Get several answers, then put them to the test.** `/compare` sends the same
  prompt to multiple configured API models. With `--judge`, the models review
  one another's proposals and can concede to a stronger answer; a separate judge
  settles a remaining disagreement. See the alternatives and reasoning before
  committing to an approach. A model's ranking isn't a substitute for running tests.
- **Keep the apps you build somewhere useful.** The desktop's **My apps** library
  lets you create an app, add an existing project, return to its coding chat,
  and run or preview it using its configured launch command. Your projects stay
  in folders you choose.
- **Bring the models you already use.** Work through supported native coding
  CLIs or API providers, retain conversations, and run acceptance commands in your
  own repositories. The shared SwarmCore harness is included for developers who
  want to adapt it or embed it in their own tools.

For example, after configuring the named API lanes:

```text
/compare --models openai,anthropic,gemini --judge openai Propose three ways to add offline sync to this app. Compare failure recovery and implementation effort.
```

Comparison produces proposals and a review; it doesn't apply several competing
implementations to your repository. Provider calls are billed by your services.

## Try it before you fork it

The [Windows x64 desktop preview](https://talavine.com/talavine-code#download)
includes its .NET runtime, so you don't need the SDK to try the app. Bring a
supported coding CLI sign-in or your own model API key, choose a project, and
give it a concrete job. Project build tools still depend on what you're building.
Apple Silicon macOS source is included; see [building](docs/building.md).

Created by **[Scott Crossen](https://github.com/scrossen)**. Part of
[Talavine](https://talavine.com). I build agent harnesses and integrations, and
help turn prototypes into software people can use. For consulting or custom
development, [get in touch](https://talavine.com/support).

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
dotnet run --project SwarmCode -- --repo /path/to/your/repository
```

Install and sign in to a supported native CLI such as Claude Code or Codex,
or configure your own supported model API key. Inside the CLI, `/help` lists
commands; `/key openai` prompts for a key without putting it in shell history.
Never commit a key, password export, session directory, or local settings file.

Default permission mode is `auto`. Add `--permission-mode ask` to approve side effects.
Review permissions before granting access to repositories, commands, browsers,
credentials, or external services. Model-generated commands can change files
and perform external actions. Native CLI restrictions and host tool permissions
have different enforcement boundaries; see [security](SECURITY.md).

```sh
dotnet test SwarmCode.Tests/SwarmCode.Tests.csproj --no-restore
dotnet test SwarmCode.Hosting.Tests/SwarmCode.Hosting.Tests.csproj --no-restore
dotnet test SwarmCode.App.Tests/SwarmCode.App.Tests.csproj --no-restore
dotnet test SwarmCore/SwarmCore.Coding.Tests/SwarmCore.Coding.Tests.csproj --no-restore
```

Tests requiring live providers, personal browsers, or native platform facilities
are opt-in or platform-specific. A source build is not desktop installation proof.

## Desktop development

The full solution is `TalavineCode.slnx`. Windows needs Visual Studio with
MSBuild, the Windows SDK, and the matching .NET MAUI Windows workload. Use
Visual Studio MSBuild for Windows XAML compilation. macOS needs Apple Silicon,
the matching .NET macOS workload, Xcode 27, and an appropriate signing identity
and provisioning profile for Keychain access. The Mac host uses native AppKit
and WKWebView with the same Blazor pages; Windows uses MAUI. Do not use someone else's signing
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
