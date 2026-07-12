namespace M3Undle.Web.Contracts;

public sealed class EncryptionStatusResponse
{
    public bool EncryptionAvailable { get; set; }
    public string? ActiveKeyId { get; set; }
    public IReadOnlyList<string> KnownKeyIds { get; set; } = [];
    public int ProvidersOnActiveKey { get; set; }
    public int ProvidersOnOtherKey { get; set; }
    public int DownstreamIntegrationsOnActiveKey { get; set; }
    public int DownstreamIntegrationsOnOtherKey { get; set; }
}

public sealed class RotateEncryptionResponse
{
    public string? ActiveKeyId { get; set; }
    public string? BackupFilePath { get; set; }
    public int ProvidersMigrated { get; set; }
    public int DownstreamIntegrationsMigrated { get; set; }
    public int RowsAlreadyCurrent { get; set; }
}
