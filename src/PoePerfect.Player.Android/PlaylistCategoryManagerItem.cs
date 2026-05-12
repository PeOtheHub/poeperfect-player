using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PoePerfect.Player.Android;

public sealed class PlaylistCategoryManagerItem : INotifyPropertyChanged
{
    private double _dragTranslationY;
    private bool _isBeingDragged;
    private bool _isDropTarget;
    private bool _isVisible;
    private double _layoutTranslationY;
    private double _rowHeight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Key { get; init; }

    public required string Label { get; init; }

    public required int Count { get; init; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsBeingDragged
    {
        get => _isBeingDragged;
        set
        {
            if (_isBeingDragged == value)
            {
                return;
            }

            _isBeingDragged = value;
            OnPropertyChanged();
        }
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value)
            {
                return;
            }

            _isDropTarget = value;
            OnPropertyChanged();
        }
    }

    public double DragTranslationY
    {
        get => _dragTranslationY;
        set
        {
            if (Math.Abs(_dragTranslationY - value) < 0.01)
            {
                return;
            }

            _dragTranslationY = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenderTranslationY));
        }
    }

    public double LayoutTranslationY
    {
        get => _layoutTranslationY;
        set
        {
            if (Math.Abs(_layoutTranslationY - value) < 0.01)
            {
                return;
            }

            _layoutTranslationY = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenderTranslationY));
        }
    }

    public double RowHeight
    {
        get => _rowHeight;
        set
        {
            if (Math.Abs(_rowHeight - value) < 0.01)
            {
                return;
            }

            _rowHeight = value;
            OnPropertyChanged();
        }
    }

    public double RenderTranslationY => DragTranslationY + LayoutTranslationY;

    public string CountLabel => Count == 1 ? "1 objekt" : $"{Count} objekt";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
