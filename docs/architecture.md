# Source layout

Talavine Code has three hosts/composition projects and fourteen shared libraries.
The source includes complete required shared projects, including reusable APIs
that other hosts may also use. No private product assembly is needed to build it.

| Projects | Responsibility |
| --- | --- |
| `SwarmCode` | Terminal entry point and native-agent MCP bridge entry point |
| `SwarmCode.App` | Windows and Apple Silicon Mac Catalyst desktop UI |
| `SwarmCode.Hosting` | Host configuration, local stores, credentials, scheduling, and composition |
| `SwarmCore.Coding` | Coding orchestration, repository tools, native lanes, and coding workflows |
| `SwarmCore.Agent` | Reusable agent runtime, tool policy, procedures, and execution contracts |
| `SwarmCore.Abstractions` | Shared contracts, provider metadata, process and security infrastructure |
| `SwarmCore.LlmRouting` | Provider adapters, model catalogs, routing, and reviewed provider packs |
| `SwarmCore.Secrets` | Credential storage/protection primitives and platform keystore adapters |
| `SwarmCore.Missions` | Resource-scoped execution, shell/HTTP/browser primitives and containment |
| `SwarmCore.Mcp` | MCP integrations and transport adapters |
| `SwarmCore.Services`, `SwarmCore.Scripting`, `SwarmCore.Skills` | Authored services, C# scripting, and reusable skills |
| `SwarmCore.PersonalMemory`, `SwarmCore.Knowledge` | Shared memory and knowledge contracts/logic |
| `SwarmCore.Chat`, `SwarmCore.Chat.Components` | Chat contracts and reusable Razor UI components |

Shared libraries must not depend on application assemblies. Hosts supply stores,
credentials, authorization, scheduling, and UI. Publishing a tool descriptor is
separate from authorizing a session to execute it. A configured model or fallback
route is separate from a successful inference or a verified task result.

Provider packs under `SwarmCore.LlmRouting/ProviderPacks` are reviewed metadata,
not executable authority. Changes to their endpoints, argument templates, or
capability claims need review and tests. Do not store credentials in a pack.

Mission process containment is distinct from ordinary coding shell permissions.
Platform support and actual runtime restrictions must be tested on the target OS.
An ordinary workspace directory does not constitute an operating-system sandbox.

The peer adapter uses the optional, separately installed SwarmSpan host through
its local paired-device interface. The SwarmSpan application and private product
implementations are not part of this repository.
