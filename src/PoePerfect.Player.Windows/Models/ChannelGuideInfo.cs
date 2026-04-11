namespace APTV.Models;

public sealed record ChannelGuideInfo(EpgProgrammeInfo? Current, EpgProgrammeInfo? Next, string? IconUrl = null);
