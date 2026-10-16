# Security

Please do not disclose exploitable vulnerabilities or credentials in public issues.
Use GitHub's private vulnerability reporting feature when enabled for this
repository. If it is unavailable, use the Talavine website's contact options to
arrange a private reporting channel before sending sensitive details.

Talavine Code can run shell commands, edit files, connect tools, and interact
with browsers and external services under the user's selected authority.
Use a disposable repository for evaluation, review generated changes, and choose
the permission mode appropriate to the work. A working directory is not a
complete operating-system sandbox. Native CLI lanes also enforce their own
permissions and may expose capabilities outside the host broker.

Model API and native CLI requests can send repository content to the selected
provider. Choosing local storage does not by itself make model execution local.
Browser access may expose authenticated accounts to authorized tools.

Use the credential vault or provider environment variables. Do not put credentials
in prompts or command arguments. Redaction reduces accidental disclosure but is
not a guarantee that arbitrary tool output cannot contain sensitive data.
Vault availability depends on platform keystore support; verify the active
protection mode before storing sensitive values. Conversation and ordinary
workspace files are not promised to be encrypted at rest.

Install only trusted extensions, MCP servers, native CLIs, and dependencies.
Security reports should identify the affected version or commit, prerequisites,
impact, and a minimal synthetic reproduction. Do not send live secrets.
