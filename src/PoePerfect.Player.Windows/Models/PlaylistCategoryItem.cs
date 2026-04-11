namespace APTV.Models;

public sealed class PlaylistCategoryItem
{
    public required BrowseSection Section { get; init; }

    public required string Key { get; init; }

    public required string Label { get; init; }

    public required int Count { get; init; }

    public bool IsVisible { get; set; } = true;
}
