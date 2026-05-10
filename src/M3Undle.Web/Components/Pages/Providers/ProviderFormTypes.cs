namespace M3Undle.Web.Components.ProviderForms;

public sealed record XtreamPrefill(
    string Name,
    string BaseUrl,
    string Username,
    string Password,
    bool IncludeXmltv,
    bool Enabled = true,
    int TimeoutSeconds = 120,
    bool LimitConcurrentStreams = false,
    int ConcurrentStreamLimit = 1,
    bool IncludeVod = true,
    bool IncludeSeries = true,
    bool ForceMpegTs = false,
    bool CleanRelayRemux = false,
    string ProfileId = "");

public sealed record UrlProviderPrefill(
    string Name,
    string Url,
    string? XmltvUrl);

public enum UrlProviderMode { Url, File }
