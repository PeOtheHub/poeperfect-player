using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PoePerfect.Player.Core.Models;

public sealed class SeriesSeasonItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string FavoriteKey { get; init; }

    public int? SeasonNumber { get; init; }

    public int SortOrder { get; init; }

    public required IReadOnlyList<SeriesEpisodeItem> Episodes { get; init; }

    public int EpisodeCount => Episodes.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
