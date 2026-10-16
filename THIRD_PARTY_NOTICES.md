# Third-party notices

The repository's MIT license covers Talavine-owned source, not a relicensing
of upstream components. Preserve the following files and their notices:

| Material | Location | Upstream license |
| --- | --- | --- |
| PDF.js | `SwarmCode.App/wwwroot/vendor/pdfjs` | Apache-2.0; see its `LICENSE` |
| PDF.js character maps | `SwarmCode.App/wwwroot/vendor/pdfjs/cmaps` | See its `LICENSE` |
| Bundled PDF standard fonts | `SwarmCode.App/wwwroot/vendor/pdfjs/standard_fonts` | See `LICENSE_FOXIT` and `LICENSE_LIBERATION` |
| Public Suffix List | `SwarmCore/SwarmCore.Abstractions/Networking/public_suffix_list.txt` | MPL-2.0; its source header and `LICENSES/MPL-2.0.txt` |

NuGet dependencies retain their own licenses. Generate and review the transitive
dependency inventory for the exact release; restoring a package is not a license
clearance. Native CLI tools, model weights, browsers, operating-system SDKs, and
commercial services are separate dependencies and are not licensed by this project.

The prepared [CLI dependency inventory](docs/cli-dependencies.md) includes
Microsoft.Data.SqlClient.SNI.runtime, which uses Microsoft software license terms,
and SQLite, which is public domain. These are not relicensed under MIT.
