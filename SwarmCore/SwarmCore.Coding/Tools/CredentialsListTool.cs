using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Catalog;
using SwarmCore.Secrets;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Coding.Tools;

/// <summary>
/// Lists the names of secrets the user has stored (API keys, tokens, site logins) so the
/// model can reference them as <c>{{secret:name}}</c>. Values are never returned: host
/// adapters inject them when a tool runs (shell command or environment, HTTP header).
/// </summary>
public sealed class CredentialsListTool
{
    public const string ToolId = "credentials.list";

    public sealed class Input
    {
        [Description("Optional case-insensitive filter matched against the name, site, and kind (for example \"github\" or \"api-key\").")]
        public string? Filter { get; set; }
    }

    public sealed class Item
    {
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public string? Site { get; init; }
        [Description("Masked username for a site login, for example s***@example.com.")]
        public string? UsernameHint { get; init; }
        [Description("The exact text to put in a command, environment value, or header where the secret belongs.")]
        public required string Reference { get; init; }
    }

    public sealed class Output
    {
        public required List<Item> Credentials { get; init; }
        public required bool VaultAvailable { get; init; }
        public required string Usage { get; init; }
    }

    private readonly IServiceProvider _services;

    public CredentialsListTool(IServiceProvider services) { _services = services; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: false,
        ownerAgentId: "coding",
        description: "List the user's stored credentials by name (API keys, tokens, website logins). Values are never returned. " +
                     "Use a listed name as {{secret:name}} inside a shell.exec command or environment value, or an http.request header; " +
                     "the real value is injected when the tool runs and scrubbed from the output. Call this before asking the user for a key or password.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["credentials", "secrets", "api key", "token", "password", "login", "vault"],
        invoke: async (input, ct) =>
        {
            const string usage = "Reference a credential as {{secret:name}} where its value belongs (shell.exec command text or environment values, http.request headers). " +
                                 "The value is injected at run time and never shown. If the credential you need is missing, ask the user to save it with /secret <name> or in Settings; never ask them to paste it into the chat.";
            var filter = string.IsNullOrWhiteSpace(input.Filter) ? null : input.Filter.Trim();
            var items = new List<Item>();

            var vault = _services.GetService<ISecretVault>();
            if (vault is not null)
            {
                foreach (var e in await vault.ListAsync(ct).ConfigureAwait(false))
                {
                    if (filter is not null && !Matches(filter, e.Name, e.Site, e.Kind)) continue;
                    items.Add(new Item { Name = e.Name, Kind = e.Kind, Site = e.Site, UsernameHint = e.UsernameHint, Reference = SecretReference.Placeholder(e.Name) });
                }
                return new Output { Credentials = items, VaultAvailable = true, Usage = usage };
            }

            var store = _services.GetService<ICredentialStore>();
            if (store is not null)
            {
                foreach (var name in await store.ListNamesAsync(ct).ConfigureAwait(false))
                {
                    if (filter is not null && !Matches(filter, name, null, null)) continue;
                    items.Add(new Item { Name = name, Kind = SecretKinds.Generic, Reference = SecretReference.Placeholder(name) });
                }
                return new Output { Credentials = items, VaultAvailable = true, Usage = usage };
            }

            return new Output { Credentials = items, VaultAvailable = false, Usage = "This host has no credential vault, so {{secret:name}} references cannot be resolved." };
        });

    private static bool Matches(string filter, string name, string? site, string? kind)
        => name.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (site is not null && site.Contains(filter, StringComparison.OrdinalIgnoreCase))
           || (kind is not null && kind.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
