using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.Promotions;

/// <summary>
/// The small, fixed vocabulary a deal is filed under. The model proposes one
/// of these names; anything else falls back to <see cref="Classify"/>, a
/// keyword pass that also files deals recorded before categories existed
/// without another model call.
/// </summary>
public static class PromoDealCategories
{
    public const string Food = "food";
    public const string Grocery = "grocery";
    public const string Travel = "travel";
    public const string Electronics = "electronics";
    public const string Home = "home";
    public const string Apparel = "apparel";
    public const string Health = "health";
    public const string Entertainment = "entertainment";
    public const string Services = "services";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [Food, Grocery, Travel, Electronics, Home, Apparel, Health, Entertainment, Services, Other];

    /// <summary>Categories shown first on Today when the user has not chosen their own.</summary>
    public static readonly IReadOnlyList<string> DefaultPriority = [Food, Grocery];

    public static string Label(string category) => category switch
    {
        Food => "Food & dining",
        Grocery => "Grocery",
        Travel => "Travel",
        Electronics => "Electronics",
        Home => "Home",
        Apparel => "Clothing",
        Health => "Health & beauty",
        Entertainment => "Entertainment",
        Services => "Services",
        _ => "Other",
    };

    /// <summary>Accepts a model-proposed name; null when it is not in the vocabulary.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim().ToLowerInvariant();
        return key switch
        {
            "food" or "dining" or "restaurant" or "restaurants" or "food & dining" => Food,
            "grocery" or "groceries" or "supermarket" => Grocery,
            "travel" or "flights" or "hotel" or "hotels" or "airline" => Travel,
            "electronics" or "tech" or "technology" => Electronics,
            "home" or "home & garden" or "furniture" or "appliances" => Home,
            "apparel" or "clothing" or "fashion" or "shoes" => Apparel,
            "health" or "beauty" or "health & beauty" or "pharmacy" => Health,
            "entertainment" or "media" or "games" or "streaming" => Entertainment,
            "services" or "service" or "subscription" or "software" => Services,
            "other" => Other,
            _ => All.Contains(key) ? key : null,
        };
    }

    /// <summary>Parses a stored comma-separated priority list into known categories, in order.</summary>
    public static IReadOnlyList<string> ParsePriorityList(string? stored)
    {
        if (stored is null) return DefaultPriority;
        var parsed = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parsed;
    }

    private static readonly (string Category, Regex Pattern)[] Keywords =
    [
        (Grocery, Build("grocer", "groceries", "supermarket", "produce", "pantry", "market basket", "instacart", "kroger", "safeway", "whole foods", "trader joe", "aldi", "costco", "sam's club")),
        (Food, Build("restaurant", "dining", "pizza", "burger", "taco", "sushi", "coffee", "latte", "espresso", "cafe", "café", "bakery", "donut", "doughnut", "sandwich", "bagel", "chicken", "wings", "bbq", "grill", "steak", "menu", "entree", "entrée", "meal", "meals", "breakfast", "brunch", "lunch", "dinner", "dessert", "pie", "takeout", "take-out", "doordash", "uber eats", "grubhub", "chipotle", "starbucks", "dunkin", "mcdonald", "wendy", "domino", "panera", "subway", "food")),
        (Travel, Build("flight", "flights", "airline", "airlines", "airfare", "fare sale", "hotel", "hotels", "resort", "cruise", "rental car", "car rental", "miles", "points", "vacation", "getaway", "trip", "travel", "airbnb", "expedia", "marriott", "hilton", "hyatt", "delta air", "united airlines", "southwest", "jetblue", "alaska air")),
        (Electronics, Build("laptop", "phone", "smartphone", "tablet", "tv", "television", "headphones", "earbuds", "speaker", "camera", "monitor", "console", "gaming", "electronics", "samsung", "best buy", "lg electronics", "sony", "dell")),
        (Home, Build("appliance", "appliances", "furniture", "sofa", "couch", "mattress", "bedding", "kitchen", "cookware", "vacuum", "washer", "dryer", "refrigerator", "fridge", "dishwasher", "decor", "décor", "garden", "patio", "lighting", "home depot", "lowe's", "wayfair", "ikea", "home")),
        (Apparel, Build("shoes", "sneakers", "boots", "socks", "apparel", "clothing", "jeans", "denim", "jacket", "coat", "dress", "shirt", "t-shirt", "hoodie", "sweater", "activewear", "athleisure", "fashion", "outfit", "wardrobe", "nike", "adidas", "gap", "old navy", "levi", "lululemon", "zara", "h&m")),
        (Health, Build("pharmacy", "vitamin", "vitamins", "supplement", "skincare", "skin care", "makeup", "cosmetic", "beauty", "wellness", "fitness", "gym", "protein", "cvs", "walgreens", "sephora", "ulta")),
        (Entertainment, Build("movie", "movies", "theater", "theatre", "concert", "tickets", "streaming", "stream", "game", "games", "playstation", "xbox", "nintendo", "netflix", "hulu", "disney", "spotify", "audible", "kindle", "book", "books")),
        // "subscription" files under Services, as Normalize does for a model-proposed name;
        // a streaming subscription still reads as Entertainment from its other words.
        (Services, Build("insurance", "internet", "wireless", "mobile plan", "cellular", "bank", "credit card", "loan", "mortgage", "tax", "software", "subscription", "cloud", "hosting", "vpn", "domain", "storage plan", "lawn", "cleaning", "repair", "tune-up")),
    ];

    /// <summary>
    /// Keyword classification over the deal's own words plus its merchant. It
    /// is deliberately conservative: an unrecognised offer is filed as other
    /// rather than guessed, so a bad guess cannot jump the priority queue.
    /// </summary>
    public static string Classify(string? merchant, string? title, string? description)
    {
        var text = " " + string.Join(" ", new[] { merchant, title, description }.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant() + " ";
        foreach (var (category, pattern) in Keywords)
        {
            if (pattern.IsMatch(text)) return category;
        }
        return Other;
    }

    private static Regex Build(params string[] terms)
        => new("(?<![a-z0-9])(" + string.Join("|", terms.Select(Regex.Escape)) + ")(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
