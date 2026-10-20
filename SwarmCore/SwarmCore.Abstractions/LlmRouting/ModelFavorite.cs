namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>A person's saved model shortcut. RouteName distinguishes host routes to the same provider.</summary>
public sealed record ModelFavorite(string Provider, string Model, string? RouteName = null);

/// <summary>An explicit membership change, never a replacement of a stale favorites snapshot.</summary>
public sealed record ModelFavoriteChange(ModelFavorite Favorite, bool Pinned);
