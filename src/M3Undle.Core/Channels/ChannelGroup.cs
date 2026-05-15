namespace M3Undle.Core.Channels;

internal record ChannelGroup(string Name, IReadOnlyCollection<ChannelDescriptor> Channels);
