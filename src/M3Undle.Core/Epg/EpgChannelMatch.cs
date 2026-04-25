namespace M3Undle.Core.Epg;

public sealed record EpgChannelMatch(
    EpgChannelRecord Channel,
    string Mode,
    float Confidence);
