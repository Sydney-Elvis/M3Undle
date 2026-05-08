namespace M3Undle.Web.Streaming.Configuration;

public sealed class GeneratedHlsOptions
{
    public bool Enabled { get; set; } = true;

    public string Directory { get; set; } = "hls-work";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int SegmentDurationSeconds { get; set; } = 2;

    public int PlaylistSize { get; set; } = 6;

    public int DeleteThreshold { get; set; } = 1;

    public int StartupTimeoutSeconds { get; set; } = 15;

    public int InactivityTimeoutSeconds { get; set; } = 60;

    public int CleanupIntervalSeconds { get; set; } = 15;

    public int StartupStaleAgeHours { get; set; } = 24;
}
