using SwarmCore.Agent.Business;

namespace SwarmCore.Services.Http;

/// <summary>
/// Explicit host selection for finite public research. The ordinary reader keeps
/// its normal behavior; a controlled host supplies independently reviewed resources.
/// The ordinary reader owns a private HttpClient. This factory installs the
/// reviewed boundary directly on its transport rather than assuming a factory
/// filter is present.
/// </summary>
public static class ReviewedPublicBusinessPageClientFactory
{
    public static PublicBusinessPageClient Create(ReviewedPublicReadPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Create(policy, ReviewedPublicReadHandler.PhysicalTransport());
    }

    internal static PublicBusinessPageClient Create(ReviewedPublicReadPolicy policy, HttpMessageHandler transport)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(transport);
        return new PublicBusinessPageClient(new ReviewedPublicReadHandler(policy, transport));
    }
}
