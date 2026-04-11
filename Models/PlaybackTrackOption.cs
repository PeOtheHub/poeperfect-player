namespace APTV.Models;

public sealed class PlaybackTrackOption
{
    public PlaybackTrackOption(int id, string label)
    {
        Id = id;
        Label = label;
    }

    public int Id { get; }

    public string Label { get; }

    public override string ToString() => Label;
}
