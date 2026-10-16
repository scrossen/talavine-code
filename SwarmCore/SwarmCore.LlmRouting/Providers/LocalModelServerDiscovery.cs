using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Read-only discovery of OpenAI-compatible catalogs on active local listeners.</summary>
public sealed class LocalModelServerDiscovery(ConfiguredModelCatalog catalog)
{
    public static IReadOnlyList<Uri> ListeningEndpoints()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))
                .Select(e => new Uri($"http://{(e.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1")}:{e.Port}/v1"))
                .Distinct().ToArray();
        }
        catch (NetworkInformationException) { return []; }
        catch (PlatformNotSupportedException) { return []; }
    }

    public async Task<IReadOnlyList<ConfiguredModelSource>> DiscoverAsync(IEnumerable<Uri> endpoints,
        IEnumerable<string> excludedEndpoints, CancellationToken ct = default)
    {
        var excluded = excludedEndpoints.Select(e => Uri.TryCreate(e, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>().Where(u => u.IsLoopback).Select(u => u.Port).ToHashSet();
        using var gate = new SemaphoreSlim(8);
        var results = await Task.WhenAll(endpoints.Where(u => u.IsLoopback && u.Scheme == "http" && !excluded.Contains(u.Port))
            .DistinctBy(u => u.Port).Select(async endpoint =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
                    var source = new ConfiguredModelSource
                    {
                        Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)))[..32].ToLowerInvariant(),
                        Name = $"local-{endpoint.Port}", Kind = "Compatible", Endpoint = endpoint.AbsoluteUri.TrimEnd('/'), RequiresApiKey = false
                    };
                    var models = await catalog.DiscoverAsync(source, timeout.Token).ConfigureAwait(false);
                    // Internal model workers can expose /v1/models with a file path instead
                    // of a public model ID. Their ephemeral ports are not user-facing routes.
                    var model = models.FirstOrDefault(m => !Path.IsPathFullyQualified(m)
                        && !(Uri.TryCreate(m, UriKind.Absolute, out var path) && path.IsFile));
                    return model is null ? null : source with { Model = model };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return null; }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        return results.OfType<ConfiguredModelSource>().ToArray();
    }
}
