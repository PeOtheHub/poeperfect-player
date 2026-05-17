namespace PoePerfect.Player.Core.Models;

public sealed record MovieDetailInfo(
    string Title,
    string? PosterUrl,
    string Plot,
    string Genre,
    string Cast,
    string Director,
    string Rating,
    string Duration,
    int? DurationSeconds,
    string ReleaseDate);
