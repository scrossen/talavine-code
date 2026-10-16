using System.Text.Json;

namespace SwarmCore.Abstractions.Serialization;

public static class SwarmJsonOptions
{
    public static readonly JsonSerializerOptions ExternalApi = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
