# CLI dependency inventory

This baseline predates the portable `SwarmCode.App.Tests` suite. That suite adds
bUnit and its HTML parser dependencies, including an explicit AngleSharp 1.5.0
pin. Generate the current exact inventory with `dotnet list TalavineCode.Cli.slnx
package --include-transitive`; audit with `--vulnerable --include-transitive`.
The publisher audits the current restored graph before each source publication.

Generated from restored NuGet metadata for `TalavineCode.Cli.slnx` during public-source preparation. Includes its tests. This is package metadata, not a legal clearance or a desktop/native dependency inventory. Third-party terms apply independently of this repository's MIT license.

| Package | Version | License metadata |
| --- | --- | --- |
| Azure.Core | 1.47.1 | MIT |
| Azure.Identity | 1.14.2 | MIT |
| Markdig | 1.1.2 | BSD-2-Clause |
| Microsoft.AspNetCore.Authorization | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.AspNetCore.Components | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.AspNetCore.Components.Analyzers | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.AspNetCore.Components.Forms | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.AspNetCore.Components.Web | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.AspNetCore.Metadata | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT |
| Microsoft.Bcl.Cryptography | 9.0.4 | MIT |
| Microsoft.Bcl.HashCode | 1.1.1 | MIT |
| Microsoft.CodeAnalysis.Analyzers | 5.9.0-1.26328.17 | MIT |
| Microsoft.CodeAnalysis.CSharp | 5.9.0 | MIT |
| Microsoft.CodeAnalysis.CSharp.Scripting | 5.9.0 | MIT |
| Microsoft.CodeAnalysis.Common | 5.9.0 | MIT |
| Microsoft.CodeAnalysis.Scripting.Common | 5.9.0 | MIT |
| Microsoft.CodeCoverage | 17.13.0 | MIT |
| Microsoft.Data.SqlClient | 6.1.1 | MIT |
| Microsoft.Data.SqlClient.SNI.runtime | 6.0.2 | LICENSE.txt |
| Microsoft.Data.Sqlite | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Data.Sqlite.Core | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.AI | 10.2.0 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| Microsoft.Extensions.Caching.Memory | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Caching.Memory | 9.0.4 | MIT |
| Microsoft.Extensions.Configuration | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.Binder | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.CommandLine | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.FileExtensions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.Json | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Configuration.UserSecrets | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.DependencyInjection | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Diagnostics | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.FileProviders.Physical | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.FileSystemGlobbing | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Hosting | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Http | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Localization.Abstractions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.Configuration | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.Console | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.Debug | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.EventLog | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Logging.EventSource | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Options | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Options.ConfigurationExtensions | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.Validation | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.Extensions.VectorData.Abstractions | 10.0.0 | MIT |
| Microsoft.Identity.Client | 4.73.1 | MIT |
| Microsoft.Identity.Client.Extensions.Msal | 4.73.1 | MIT |
| Microsoft.IdentityModel.Abstractions | 7.7.1 | MIT |
| Microsoft.IdentityModel.JsonWebTokens | 7.7.1 | MIT |
| Microsoft.IdentityModel.Logging | 7.7.1 | MIT |
| Microsoft.IdentityModel.Protocols | 7.7.1 | MIT |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 7.7.1 | MIT |
| Microsoft.IdentityModel.Tokens | 7.7.1 | MIT |
| Microsoft.JSInterop | 11.0.0-rc.2.26504.105 | MIT |
| Microsoft.NET.Test.Sdk | 17.13.0 | MIT |
| Microsoft.Playwright | 1.57.0 | MIT |
| Microsoft.SemanticKernel.Abstractions | 1.72.0 | MIT |
| Microsoft.SqlServer.Server | 1.0.0 | MIT |
| Microsoft.TestPlatform.ObjectModel | 17.13.0 | MIT |
| Microsoft.TestPlatform.TestHost | 17.13.0 | MIT |
| Microsoft.Win32.SystemEvents | 11.0.0-rc.2.26504.105 | MIT |
| ModelContextProtocol | 2.0.0 | Apache-2.0 |
| ModelContextProtocol.Core | 2.0.0 | Apache-2.0 |
| NCrontab | 3.3.3 | COPYING.txt |
| Newtonsoft.Json | 13.0.1 | MIT |
| PdfPig | 0.1.14 | Apache-2.0 |
| SQLite | 3.53.4 | LICENSE.txt |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | Apache-2.0 |
| SQLitePCLRaw.core | 3.0.5 | Apache-2.0 |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | Apache-2.0 |
| System.ClientModel | 1.5.1 | MIT |
| System.Configuration.ConfigurationManager | 9.0.4 | MIT |
| System.Diagnostics.EventLog | 11.0.0-rc.2.26504.105 | MIT |
| System.Diagnostics.EventLog | 9.0.4 | MIT |
| System.Drawing.Common | 11.0.0-rc.2.26504.105 | MIT |
| System.IdentityModel.Tokens.Jwt | 7.7.1 | MIT |
| System.Memory.Data | 8.0.1 | MIT |
| System.Numerics.Tensors | 10.0.2 | MIT |
| System.Security.Cryptography.Pkcs | 11.0.0-rc.2.26504.105 | MIT |
| System.Security.Cryptography.ProtectedData | 11.0.0-rc.2.26504.105 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | https://raw.githubusercontent.com/xunit/xunit/master/license.txt |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.assert | 2.9.3 | Apache-2.0 |
| xunit.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 |
