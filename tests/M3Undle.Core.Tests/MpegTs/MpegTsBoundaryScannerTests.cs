using M3Undle.Core.MpegTs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.MpegTs;

[TestClass]
public sealed class MpegTsBoundaryScannerTests
{
    [TestMethod]
    public void Process_DropsLeadingJunkAndEmitsAlignedPackets()
    {
        var scanner = new MpegTsBoundaryScanner();
        var packet = Packet(256, payload: [0x01, 0x02, 0x03]);
        var input = new byte[] { 0x00, 0x11, 0x22 }.Concat(packet).ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(3, batch.DroppedByteCount);
        Assert.IsTrue(batch.SyncLost);
        Assert.HasCount(MpegTsBoundaryScanner.PacketSize, batch.Data);
        Assert.AreEqual(0x47, batch.Data[0]);
    }

    [TestMethod]
    public void Process_CarriesPartialPacketsAcrossReads()
    {
        var scanner = new MpegTsBoundaryScanner();
        var packet = Packet(256, payload: [0xAA, 0xBB]);

        var first = scanner.Process(packet.AsSpan(0, 80));
        var second = scanner.Process(packet.AsSpan(80));

        Assert.IsNull(first);
        Assert.IsNotNull(second);
        Assert.HasCount(MpegTsBoundaryScanner.PacketSize, second.Data);
        CollectionAssert.AreEqual(packet, second.Data);
    }

    [TestMethod]
    public void Process_ResyncsAfterCorruptBytes()
    {
        var scanner = new MpegTsBoundaryScanner();
        var first = Packet(256, payload: [0x01]);
        var second = Packet(257, payload: [0x02]);
        var input = first.Concat(new byte[] { 0x99, 0x98, 0x97 }).Concat(second).ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(3, batch.DroppedByteCount);
        Assert.IsTrue(batch.SyncLost);
        Assert.HasCount(MpegTsBoundaryScanner.PacketSize * 2, batch.Data);
        Assert.AreEqual(0x47, batch.Data[0]);
        Assert.AreEqual(0x47, batch.Data[MpegTsBoundaryScanner.PacketSize]);
    }

    [TestMethod]
    public void Process_DetectsPatPmtSafeStartup()
    {
        var scanner = new MpegTsBoundaryScanner();
        var input = PatPacket(100).Concat(PmtPacket(100, 256)).ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.PatPmt, batch.StartupKind);
        Assert.HasCount(MpegTsBoundaryScanner.PacketSize * 2, batch.Data);
    }

    [TestMethod]
    public void Process_DetectsH264IdrSafeStartup()
    {
        var scanner = new MpegTsBoundaryScanner();
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x01, 0x02]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x03, 0x04]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x05, 0x06]))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, batch.StartupKind);
    }

    [TestMethod]
    public void Process_FalseSyncByte_SkippedByDoubleSyncCheck()
    {
        // A 0x47 payload byte sits at offset 1 in the junk prefix; the real TS packet
        // starts at offset 3. With double-sync, the false 0x47 at offset 1 is rejected
        // because _pending[1 + 188] != 0x47.
        var scanner = new MpegTsBoundaryScanner();
        var packet = Packet(256, payload: [0x01, 0x02, 0x03]);

        // Junk: [0x00, 0x47, 0x22] — the 0x47 at index 1 is a false sync byte.
        var input = new byte[] { 0x00, 0x47, 0x22 }.Concat(packet).ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(3, batch.DroppedByteCount);
        Assert.IsTrue(batch.SyncLost);
        Assert.HasCount(MpegTsBoundaryScanner.PacketSize, batch.Data);
        Assert.AreEqual(0x47, batch.Data[0]);
    }

    [TestMethod]
    public void Reset_ClearsStateAndAllowsRescan()
    {
        var scanner = new MpegTsBoundaryScanner();
        var fullSequence = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x01]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x02]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x03]))
            .ToArray();

        var firstBatch = scanner.Process(fullSequence);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, firstBatch?.StartupKind);

        scanner.Reset();

        // After reset, a lone PAT packet cannot produce PatPmt (PMT not yet seen).
        var afterReset = scanner.Process(PatPacket(100));
        Assert.AreEqual(MpegTsStartupKind.PacketBoundary, afterReset?.StartupKind);
    }

    [TestMethod]
    public void Process_AudioOnlyPmt_StillSelectsPatPmtKind()
    {
        var scanner = new MpegTsBoundaryScanner();
        // PAT referencing PMT at PID 100; PMT has stream type 0x0F (AAC) — no H.264 video.
        var input = PatPacket(100).Concat(AudioOnlyPmtPacket(100)).ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.PatPmt, batch.StartupKind);
    }

    [TestMethod]
    public void Process_FourByteH264StartCode_DetectsIdr()
    {
        var scanner = new MpegTsBoundaryScanner();
        // Use 4-byte start codes (00 00 00 01) instead of 3-byte (00 00 01).
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x00, 0x01, 0x67, 0x01]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x00, 0x01, 0x68, 0x02]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x00, 0x01, 0x65, 0x03]))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, batch.StartupKind);
    }

    [TestMethod]
    public void Process_PatWithMultiplePrograms_UsesFirstNonNullProgram()
    {
        var scanner = new MpegTsBoundaryScanner();
        // PAT with NIT (program 0) followed by two real programs; scanner picks pmtPid1.
        var input = MultiProgramPatPacket(pmtPid1: 100, pmtPid2: 200)
            .Concat(PmtPacket(100, 256))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.PatPmt, batch.StartupKind);
    }

    [TestMethod]
    public void Process_VideoPesWithPtsAndDts_ReportsDts()
    {
        var scanner = new MpegTsBoundaryScanner();
        const long pts = 5_400_000;
        const long dts = 5_396_400;
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x41, 0x01], pts, dts))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(dts, batch.LatestVideoDts90k);
        Assert.IsNull(batch.IdrDts90k);
    }

    [TestMethod]
    public void Process_VideoPesWithPtsOnly_ReportsPtsAsDts()
    {
        var scanner = new MpegTsBoundaryScanner();
        const long pts = 7_200_000;
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x41, 0x01], pts, dts: null))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(pts, batch.LatestVideoDts90k);
    }

    [TestMethod]
    public void Process_VideoPesWithoutParseableTimestamp_StillDetectsIdrWithNullDts()
    {
        // The plain VideoPacket helper writes a PES header that claims PTS presence but
        // has a zero header-data length; the parser must return null rather than guess.
        var scanner = new MpegTsBoundaryScanner();
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x01, 0x02]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x03, 0x04]))
            .Concat(VideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x05, 0x06]))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, batch.StartupKind);
        Assert.IsNull(batch.LatestVideoDts90k);
        Assert.IsNull(batch.IdrDts90k);
    }

    [TestMethod]
    public void Process_IdrWithTimestamp_ReportsIdrDts()
    {
        var scanner = new MpegTsBoundaryScanner();
        const long idrDts = 9_000_000;
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x67, 0x01], idrDts - 7200, idrDts - 7200))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x68, 0x02], idrDts - 3600, idrDts - 3600))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x65, 0x03], idrDts, idrDts))
            .ToArray();

        var batch = scanner.Process(input);

        Assert.IsNotNull(batch);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, batch.StartupKind);
        Assert.AreEqual(idrDts, batch.IdrDts90k);
        Assert.AreEqual(idrDts, batch.LatestVideoDts90k);
    }

    [TestMethod]
    public void Process_PacketAfterIdr_DoesNotReReportStaleIdr()
    {
        var scanner = new MpegTsBoundaryScanner();
        const long idrDts = 9_000_000;
        var idrSequence = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x67, 0x01], idrDts, idrDts))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x68, 0x02], idrDts, idrDts))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x65, 0x03], idrDts, idrDts))
            .ToArray();

        var idrBatch = scanner.Process(idrSequence);
        Assert.AreEqual(MpegTsStartupKind.H264Idr, idrBatch?.StartupKind);

        // A continuation packet with no start codes must not re-report the consumed IDR.
        var followUp = scanner.Process(Packet(256));

        Assert.IsNotNull(followUp);
        Assert.AreEqual(MpegTsStartupKind.PacketBoundary, followUp.StartupKind);
        Assert.IsNull(followUp.IdrDts90k);
    }

    [TestMethod]
    public void Reset_ClearsTimestampState()
    {
        var scanner = new MpegTsBoundaryScanner();
        var input = PatPacket(100)
            .Concat(PmtPacket(100, 256))
            .Concat(VideoPacketWithTimestamps(256, [0x00, 0x00, 0x01, 0x41, 0x01], 90000, 90000))
            .ToArray();
        Assert.AreEqual(90000, scanner.Process(input)?.LatestVideoDts90k);

        scanner.Reset();

        var afterReset = scanner.Process(PatPacket(100));
        Assert.IsNull(afterReset?.LatestVideoDts90k);
        Assert.IsNull(afterReset?.IdrDts90k);
    }

    private static byte[] PatPacket(int pmtPid)
        => Packet(0, payloadUnitStart: true,
        [
            0x00,
            0x00, 0xB0, 0x0D,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((pmtPid >> 8) & 0x1F)), (byte)pmtPid,
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] PmtPacket(int pmtPid, int videoPid)
        => Packet(pmtPid, payloadUnitStart: true,
        [
            0x00,
            0x02, 0xB0, 0x12,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            (byte)(0xE0 | ((videoPid >> 8) & 0x1F)), (byte)videoPid,
            0xF0, 0x00,
            0x1B,
            (byte)(0xE0 | ((videoPid >> 8) & 0x1F)), (byte)videoPid,
            0xF0, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] VideoPacket(int videoPid, byte[] annexB)
        => Packet(videoPid, payloadUnitStart: true,
            [0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x00, .. annexB]);

    private static byte[] VideoPacketWithTimestamps(int videoPid, byte[] annexB, long pts, long? dts)
    {
        byte[] header = dts is { } dtsValue
            ?
            [
                0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0xC0, 0x0A,
                .. EncodePesTimestamp(0x03, pts),
                .. EncodePesTimestamp(0x01, dtsValue),
            ]
            :
            [
                0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05,
                .. EncodePesTimestamp(0x02, pts),
            ];
        return Packet(videoPid, payloadUnitStart: true, [.. header, .. annexB]);
    }

    private static byte[] EncodePesTimestamp(int prefix, long value)
        =>
        [
            (byte)((prefix << 4) | (int)(((value >> 30) & 0x07) << 1) | 0x01),
            (byte)((value >> 22) & 0xFF),
            (byte)((((value >> 15) & 0x7F) << 1) | 0x01),
            (byte)((value >> 7) & 0xFF),
            (byte)(((value & 0x7F) << 1) | 0x01),
        ];

    private static byte[] AudioOnlyPmtPacket(int pmtPid)
        => Packet(pmtPid, payloadUnitStart: true,
        [
            0x00,
            0x02, 0xB0, 0x12,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            0xE1, 0x00,  // PCR PID
            0xF0, 0x00,  // no program info
            0x0F,        // stream type: AAC audio (not H.264)
            0xE1, 0x00,  // elementary PID
            0xF0, 0x00,  // no ES info
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] MultiProgramPatPacket(int pmtPid1, int pmtPid2)
        => Packet(0, payloadUnitStart: true,
        [
            0x00,
            0x00, 0xB0, 0x15,  // section_length = 21 (NIT + 2 programs + CRC)
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            // NIT entry: program_number=0, skipped by parser
            0x00, 0x00, 0xE0, 0x10,
            // Program 1 — scanner uses this PID
            0x00, 0x01,
            (byte)(0xE0 | ((pmtPid1 >> 8) & 0x1F)), (byte)pmtPid1,
            // Program 2
            0x00, 0x02,
            (byte)(0xE0 | ((pmtPid2 >> 8) & 0x1F)), (byte)pmtPid2,
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] Packet(int pid, bool payloadUnitStart = false, byte[]? payload = null)
    {
        var packet = Enumerable.Repeat((byte)0xFF, MpegTsBoundaryScanner.PacketSize).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = 0x10;

        if (payload is { Length: > 0 })
            payload.CopyTo(packet.AsSpan(4));

        return packet;
    }
}
