namespace APTV.Models;

public sealed record EpgProgrammeInfo(string Title, DateTimeOffset Start, DateTimeOffset Stop, string? Description = null)
{
    public string TimeRangeText => $"{Start.ToLocalTime():HH:mm}-{Stop.ToLocalTime():HH:mm}";

    public string SummaryText => $"{TimeRangeText} {Title}";
}
