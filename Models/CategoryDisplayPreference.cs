namespace APTV.Models;

public sealed record CategoryDisplayPreference(
    BrowseSection Section,
    string Key,
    bool IsVisible = true,
    int SortOrder = 0);
