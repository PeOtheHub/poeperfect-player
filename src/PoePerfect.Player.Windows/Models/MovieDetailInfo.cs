namespace APTV.Models;

public sealed record MovieDetailInfo(
    Channel Channel,
    string Title,
    string? PosterUrl,
    string Plot,
    string Genre,
    string Cast,
    string Director,
    string Rating,
    string Duration,
    string ReleaseDate)
{
    public static MovieDetailInfo FromChannel(Channel channel)
    {
        return new MovieDetailInfo(
            channel,
            channel.Name,
            channel.LogoUrl,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    public string DescriptionText => string.IsNullOrWhiteSpace(Plot)
        ? "Ingen beskrivning hittades för den här filmen ännu."
        : Plot;

    public string GenreText => string.IsNullOrWhiteSpace(Genre)
        ? "Genre saknas"
        : Genre;

    public string CastText => string.IsNullOrWhiteSpace(Cast)
        ? "Cast saknas"
        : Cast;

    public string DirectorText => string.IsNullOrWhiteSpace(Director)
        ? "Regi saknas"
        : Director;

    public string RatingText => string.IsNullOrWhiteSpace(Rating)
        ? "Betyg saknas"
        : $"Betyg {Rating}";

    public string DurationText => string.IsNullOrWhiteSpace(Duration)
        ? "Längd saknas"
        : Duration;

    public string ReleaseDateText => string.IsNullOrWhiteSpace(ReleaseDate)
        ? "Premiär saknas"
        : ReleaseDate;

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(ReleaseDate))
            {
                parts.Add(ReleaseDate);
            }

            if (!string.IsNullOrWhiteSpace(Duration))
            {
                parts.Add(Duration);
            }

            if (!string.IsNullOrWhiteSpace(Rating))
            {
                parts.Add($"Betyg {Rating}");
            }

            if (!string.IsNullOrWhiteSpace(Channel.CategoryName))
            {
                parts.Add(Channel.CategoryName);
            }

            return parts.Count == 0
                ? "Filmdetaljer saknas"
                : string.Join(" - ", parts);
        }
    }
}
