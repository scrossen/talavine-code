namespace SwarmCore.Coding.Routing;

/// <summary>A person's explicit starting route, including an optional supported @effort variant.</summary>
public sealed record CodingModelChoice(string Lane, string Model)
{
    public CodingLane Resolve(IReadOnlyList<CodingLane> lanes)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Choose a model before building.");
        var lane = lanes.FirstOrDefault(l => l.Name.Equals(Lane, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This provider is no longer available. Choose another provider before building.");
        return lane with { Model = Model.Trim() };
    }
}
