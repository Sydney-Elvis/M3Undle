namespace M3Undle.Core.Channels;

public record ChannelGroup(string Name, IReadOnlyCollection<ChannelDescriptor> Channels);

