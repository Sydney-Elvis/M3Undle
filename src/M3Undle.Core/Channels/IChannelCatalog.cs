namespace M3Undle.Core.Channels;

internal interface IChannelCatalog
{
    Task<IReadOnlyCollection<ChannelGroup>> GetGroupsAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChannelDescriptor> StreamChannelUpdatesAsync(CancellationToken cancellationToken = default);
}
