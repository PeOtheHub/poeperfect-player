namespace PoePerfect.Player.Android;

public sealed class BrowseCategoryChip
{
    public const string FavoritesKey = "__favorites__";
    public const string LatestKey = "__latest__";
    public const string RecentKey = "__recent__";

    public BrowseCategoryChip(string key, string label, int count, bool isSelected)
    {
        Key = key;
        Label = label;
        Count = count;
        IsSelected = isSelected;
    }

    public string Key { get; }

    public string Label { get; }

    public int Count { get; }

    public bool IsSelected { get; }

    public string DisplayText => Count > 0 ? $"{Label} ({Count})" : Label;
}
