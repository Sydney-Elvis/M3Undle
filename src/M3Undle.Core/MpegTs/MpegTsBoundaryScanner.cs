using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace M3Undle.Core.MpegTs;

public sealed class MpegTsBoundaryScanner
{
    public const int PacketSize = 188;

    private readonly List<byte> _pending = [];
    private readonly List<byte> _videoTail = [];
    private int? _pmtPid;
    private int? _videoPid;
    private bool _patSeen;
    private bool _pmtSeen;
    private bool _spsSeen;
    private bool _ppsSeen;

    public MpegTsPacketBatch? Process(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty && _pending.Count < PacketSize)
            return null;

        var dropped = 0;
        var syncLost = false;
        _pending.AddRange(input);

        var output = new List<byte>(_pending.Count - (_pending.Count % PacketSize));
        var startupKind = MpegTsStartupKind.None;

        while (_pending.Count >= PacketSize)
        {
            if (_pending[0] != 0x47)
            {
                var syncIndex = FindDoubleSyncIndex();
                if (syncIndex < 0)
                {
                    dropped += _pending.Count;
                    _pending.Clear();
                    syncLost = true;
                    break;
                }

                dropped += syncIndex;
                _pending.RemoveRange(0, syncIndex);
                syncLost = true;
                if (_pending.Count < PacketSize)
                    break;
            }

            if (_pending.Count >= PacketSize && !LooksLikePacketStart(0))
            {
                _pending.RemoveAt(0);
                dropped++;
                syncLost = true;
                continue;
            }

            var packet = _pending.GetRange(0, PacketSize).ToArray();
            _pending.RemoveRange(0, PacketSize);
            output.AddRange(packet);

            var packetKind = InspectPacket(packet);
            if (packetKind > startupKind)
                startupKind = packetKind;
        }

        if (output.Count == 0)
            return dropped > 0 || syncLost
                ? new MpegTsPacketBatch([], MpegTsStartupKind.None, dropped, syncLost, _videoPid is not null)
                : null;

        if (startupKind == MpegTsStartupKind.None)
            startupKind = MpegTsStartupKind.PacketBoundary;

        return new MpegTsPacketBatch([.. output], startupKind, dropped, syncLost, _videoPid is not null);
    }

    public void Reset()
    {
        _pending.Clear();
        _videoTail.Clear();
        _pmtPid = null;
        _videoPid = null;
        _patSeen = false;
        _pmtSeen = false;
        _spsSeen = false;
        _ppsSeen = false;
    }

    private bool LooksLikePacketStart(int index)
        => _pending[index] == 0x47;

    // Finds the first 0x47 that is either (a) followed by another 0x47 at the TS packet
    // boundary (+188 bytes), confirming sync, or (b) near the end of the buffer where a
    // second-sync check is not possible. This prevents false-sync alignment on 0x47 payload
    // bytes when enough data is available to validate the boundary.
    private int FindDoubleSyncIndex()
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            if (_pending[i] != 0x47)
                continue;

            var nextSyncOffset = i + PacketSize;
            if (nextSyncOffset < _pending.Count)
            {
                if (_pending[nextSyncOffset] != 0x47)
                    continue; // False sync byte; keep searching.
            }

            return i;
        }

        return -1;
    }

    private MpegTsStartupKind InspectPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketSize || packet[0] != 0x47)
            return MpegTsStartupKind.None;

        var payloadUnitStart = (packet[1] & 0x40) != 0;
        var pid = ((packet[1] & 0x1f) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x03;

        if (adaptationControl is 0 or 2)
            return MpegTsStartupKind.PacketBoundary;

        var payloadOffset = 4;
        if (adaptationControl == 3)
        {
            if (payloadOffset >= packet.Length)
                return MpegTsStartupKind.PacketBoundary;

            payloadOffset += 1 + packet[payloadOffset];
        }

        if (payloadOffset >= packet.Length)
            return MpegTsStartupKind.PacketBoundary;

        var payload = packet[payloadOffset..];
        if (pid == 0)
        {
            var parsedPat = ParsePat(payload, payloadUnitStart);
            return parsedPat && _pmtSeen ? MpegTsStartupKind.PatPmt : MpegTsStartupKind.PacketBoundary;
        }

        if (_pmtPid == pid)
        {
            var parsedPmt = ParsePmt(payload, payloadUnitStart);
            return parsedPmt && _patSeen ? MpegTsStartupKind.PatPmt : MpegTsStartupKind.PacketBoundary;
        }

        if (_videoPid == pid)
        {
            var parsedIdr = ParseH264(payload, payloadUnitStart);
            return parsedIdr && _patSeen && _pmtSeen && _spsSeen && _ppsSeen
                ? MpegTsStartupKind.H264Idr
                : MpegTsStartupKind.PacketBoundary;
        }

        return MpegTsStartupKind.PacketBoundary;
    }

    private bool ParsePat(ReadOnlySpan<byte> payload, bool payloadUnitStart)
    {
        var section = GetPsiSection(payload, payloadUnitStart);
        if (section.Length < 12 || section[0] != 0x00)
            return false;

        var sectionLength = ((section[1] & 0x0f) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length, 3 + sectionLength);
        var entryEnd = sectionEnd - 4;
        for (var offset = 8; offset + 4 <= entryEnd; offset += 4)
        {
            var programNumber = BinaryPrimitives.ReadUInt16BigEndian(section[offset..(offset + 2)]);
            if (programNumber == 0)
                continue;

            _pmtPid = ((section[offset + 2] & 0x1f) << 8) | section[offset + 3];
            _patSeen = true;
            return true;
        }

        return false;
    }

    private bool ParsePmt(ReadOnlySpan<byte> payload, bool payloadUnitStart)
    {
        var section = GetPsiSection(payload, payloadUnitStart);
        if (section.Length < 16 || section[0] != 0x02)
            return false;

        var sectionLength = ((section[1] & 0x0f) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length, 3 + sectionLength);
        var programInfoLength = ((section[10] & 0x0f) << 8) | section[11];
        var offset = 12 + programInfoLength;
        var entryEnd = sectionEnd - 4;
        while (offset + 5 <= entryEnd)
        {
            var streamType = section[offset];
            var elementaryPid = ((section[offset + 1] & 0x1f) << 8) | section[offset + 2];
            var esInfoLength = ((section[offset + 3] & 0x0f) << 8) | section[offset + 4];
            if (streamType == 0x1b)
            {
                _videoPid = elementaryPid;
                _pmtSeen = true;
                return true;
            }

            offset += 5 + esInfoLength;
        }

        _pmtSeen = true;
        return true;
    }

    private bool ParseH264(ReadOnlySpan<byte> payload, bool payloadUnitStart)
    {
        if (payloadUnitStart && payload.Length >= 9 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1)
        {
            var pesHeaderLength = payload[8];
            var offset = 9 + pesHeaderLength;
            payload = offset <= payload.Length ? payload[offset..] : [];
        }

        AppendVideoPayload(payload);
        var parsedIdr = ScanH264NalUnits(CollectionsMarshal.AsSpan(_videoTail));
        TrimVideoTail();
        return parsedIdr;
    }

    private void AppendVideoPayload(ReadOnlySpan<byte> payload)
    {
        foreach (var b in payload)
            _videoTail.Add(b);
    }

    private bool ScanH264NalUnits(ReadOnlySpan<byte> data)
    {
        var parsedIdr = false;
        for (var i = 0; i + 4 < data.Length; i++)
        {
            var startCodeLength = 0;
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                startCodeLength = 3;
            else if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                startCodeLength = 4;

            if (startCodeLength == 0 || i + startCodeLength >= data.Length)
                continue;

            var nalType = data[i + startCodeLength] & 0x1f;
            if (nalType == 7)
                _spsSeen = true;
            else if (nalType == 8)
                _ppsSeen = true;
            else if (nalType == 5 && _spsSeen && _ppsSeen)
            {
                parsedIdr = true;
            }
        }

        return parsedIdr;
    }

    private void TrimVideoTail()
    {
        const int maxTail = 4096;
        if (_videoTail.Count > maxTail)
            _videoTail.RemoveRange(0, _videoTail.Count - maxTail);
    }

    private static ReadOnlySpan<byte> GetPsiSection(ReadOnlySpan<byte> payload, bool payloadUnitStart)
    {
        if (!payloadUnitStart)
            return payload;

        if (payload.IsEmpty)
            return [];

        var pointer = payload[0];
        var offset = 1 + pointer;
        return offset <= payload.Length ? payload[offset..] : [];
    }
}
