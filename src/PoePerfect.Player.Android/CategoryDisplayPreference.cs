namespace PoePerfect.Player.Android;

public sealed record CategoryDisplayPreference(
    BrowseSection Section,
    string Key,
    bool IsVisible = true,
    int SortOrder = 0);
