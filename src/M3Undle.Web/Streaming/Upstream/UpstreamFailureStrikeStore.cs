using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamFailureStrikeStore
{
    private readonly ConcurrentDictionary<ChannelSessionKey, DateTimeOffset> _cooldowns = new();

    public void RecordStrike(ChannelSessionKey key, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
            return;

        _cooldowns[key] = DateTimeOffset.UtcNow.Add(cooldown);
    }

    public bool IsCoolingDown(ChannelSessionKey key, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_cooldowns.TryGetValue(key, out var until))
            return false;

        var now = DateTimeOffset.UtcNow;
        if (until <= now)
        {
            _cooldowns.TryRemove(key, out _);
            return false;
        }

        remaining = until - now;
        return true;
    }

    public IReadOnlyList<(ChannelSessionKey Key, TimeSpan Remaining)> GetActiveCooldowns()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<(ChannelSessionKey, TimeSpan)>();
        foreach (var (key, until) in _cooldowns)
        {
            var remaining = until - now;
            if (remaining > TimeSpan.Zero)
                result.Add((key, remaining));
            else
                _cooldowns.TryRemove(key, out _);
        }
        return result;
    }

    public void ClearAll() => _cooldowns.Clear();
}

