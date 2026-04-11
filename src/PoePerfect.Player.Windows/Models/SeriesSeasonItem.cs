using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace APTV.Models;

public sealed class SeriesSeasonItem : INotifyPropertyChanged
{
    private bool _isFavorite;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string FavoriteKey { get; init; }

    public int? SeasonNumber { get; init; }

    public int SortOrder { get; init; }

    public required IReadOnlyList<SeriesEpisodeItem> Episodes { get; init; }

    public int EpisodeCount => Episodes.Count;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }
}
