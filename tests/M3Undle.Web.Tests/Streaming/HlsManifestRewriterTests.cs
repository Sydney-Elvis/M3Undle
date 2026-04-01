using M3Undle.Web.Streaming.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class HlsManifestRewriterTests
{
    [TestMethod]
    public void Rewrite_RewritesPlainSegmentUris()
    {
        var manifest = """
            #EXTM3U
            #EXTINF:4.0,
            segment_0001.ts
            """;
        var rewriter = new HlsManifestRewriter();
        var baseUri = new Uri("https://provider.test/live/master.m3u8");

        var rewritten = rewriter.Rewrite(manifest, baseUri, uri => $"https://proxy.test/hls?u={Uri.EscapeDataString(uri.ToString())}");

        StringAssert.Contains(rewritten, "https://proxy.test/hls?u=https%3A%2F%2Fprovider.test%2Flive%2Fsegment_0001.ts");
    }

    [TestMethod]
    public void Rewrite_RewritesUriBearingDirectiveTags()
    {
        var manifest = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="keys/key.bin",IV=0x01
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="eng",URI="audio.m3u8"
            #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=72800,URI="iframe.m3u8"
            #EXT-X-PART:DURATION=0.33334,URI="part0.1.ts"
            #EXT-X-PRELOAD-HINT:TYPE=PART,URI="part0.2.ts"
            #EXTINF:4.0,
            seg0.ts
            """;
        var rewriter = new HlsManifestRewriter();
        var baseUri = new Uri("https://provider.test/live/master.m3u8");

        var rewritten = rewriter.Rewrite(manifest, baseUri, uri => $"https://proxy.test/p?u={Uri.EscapeDataString(uri.ToString())}");

        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Fkeys%2Fkey.bin\"");
        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Finit.mp4\"");
        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Faudio.m3u8\"");
        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Fiframe.m3u8\"");
        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Fpart0.1.ts\"");
        StringAssert.Contains(rewritten, "URI=\"https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Fpart0.2.ts\"");
        StringAssert.Contains(rewritten, "https://proxy.test/p?u=https%3A%2F%2Fprovider.test%2Flive%2Fseg0.ts");
    }
}
