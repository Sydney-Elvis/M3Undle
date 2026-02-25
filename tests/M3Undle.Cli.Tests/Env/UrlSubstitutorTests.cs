using System.Collections.Generic;
using M3Undle.Core.Env;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Env;

[TestClass]
public class UrlSubstitutorTests
{
    [TestMethod]
    public void SubstituteCredentials_WithNoEnvVars_ReturnsOriginalUrl()
    {
        var env = new Dictionary<string, string>();
        var url = "https://example.com/get.php?username=user&password=pass";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        Assert.AreEqual(url, result);
        Assert.IsEmpty(replaced);
    }

    [TestMethod]
    public void SubstituteCredentials_WithEnvVars_ReplacesPlaceholders()
    {
        var env = new Dictionary<string, string>
        {
            ["USER"] = "testuser",
            ["PASS"] = "testpass"
        };
        var url = "https://example.com/get.php?username=%USER%&password=%PASS%";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        Assert.AreEqual("https://example.com/get.php?username=testuser&password=testpass", result);
        Assert.HasCount(2, replaced);
        CollectionAssert.Contains(replaced, "USER");
        CollectionAssert.Contains(replaced, "PASS");
    }

    [TestMethod]
    public void SubstituteCredentials_CaseInsensitive_ReplacesPlaceholders()
    {
        var env = new Dictionary<string, string>
        {
            ["user"] = "testuser",
            ["pass"] = "testpass"
        };
        var url = "https://example.com/get.php?username=%USER%&password=%PASS%";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        Assert.AreEqual("https://example.com/get.php?username=testuser&password=testpass", result);
        Assert.HasCount(2, replaced);
    }

    [TestMethod]
    public void SubstituteCredentials_WithDoubleSlashInPath_NormalizesToSingleSlash()
    {
        var env = new Dictionary<string, string>
        {
            ["BASE_URL"] = "https://example.com/",
            ["USER"] = "testuser",
            ["PASS"] = "testpass"
        };
        // This would create https://example.com//get.php after substitution
        var url = "%BASE_URL%/get.php?username=%USER%&password=%PASS%";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        // Should normalize to single slash
        Assert.AreEqual("https://example.com/get.php?username=testuser&password=testpass", result);
        Assert.HasCount(3, replaced);
    }

    [TestMethod]
    public void SubstituteCredentials_WithTripleSlashInPath_NormalizesToSingleSlash()
    {
        var env = new Dictionary<string, string>
        {
            ["BASE_URL"] = "https://example.com//",
            ["USER"] = "testuser"
        };
        // This would create https://example.com///get.php after substitution
        var url = "%BASE_URL%/get.php?username=%USER%";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        // Should normalize to single slash
        Assert.AreEqual("https://example.com/get.php?username=testuser", result);
    }

    [TestMethod]
    public void SubstituteCredentials_PreservesProtocolDoubleSlash()
    {
        var env = new Dictionary<string, string>
        {
            ["PROTOCOL"] = "https",
            ["HOST"] = "example.com"
        };
        // Protocol should keep its double slash
        var url = "%PROTOCOL%://example.com/path";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        Assert.IsNotNull(result);
        StringAssert.StartsWith(result, "https://");
        Assert.AreEqual("https://example.com/path", result);
    }

    [TestMethod]
    public void SubstituteCredentials_WithNullUrl_ReturnsNull()
    {
        var env = new Dictionary<string, string> { ["USER"] = "test" };

        var result = UrlSubstitutor.SubstituteCredentials(null, env, out var replaced);

        Assert.IsNull(result);
        Assert.IsEmpty(replaced);
    }

    [TestMethod]
    public void SubstituteCredentials_WithEmptyUrl_ReturnsEmpty()
    {
        var env = new Dictionary<string, string> { ["USER"] = "test" };

        var result = UrlSubstitutor.SubstituteCredentials(string.Empty, env, out var replaced);

        Assert.IsNotNull(result);
        Assert.AreEqual(string.Empty, result);
        Assert.IsEmpty(replaced);
    }

    [TestMethod]
    public void SubstituteCredentials_WithFilePath_DoesNotNormalize()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/some/path"
        };
        var filePath = "%PATH%//file.txt";

        var result = UrlSubstitutor.SubstituteCredentials(filePath, env, out var replaced);

        // File paths should not be normalized
        Assert.AreEqual("/some/path//file.txt", result);
    }

    [TestMethod]
    public void SubstituteCredentials_WithCustomEnvVars_SubstitutesAll()
    {
        var env = new Dictionary<string, string>
        {
            ["DELTA_URL"] = "https://pinkponyclub.online/",
            ["DELTA_USER"] = "myuser",
            ["DELTA_PASS"] = "mypass"
        };
        var url = "%DELTA_URL%/get.php?username=%DELTA_USER%&password=%DELTA_PASS%&type=m3u_plus";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        Assert.AreEqual("https://pinkponyclub.online/get.php?username=myuser&password=mypass&type=m3u_plus", result);
        Assert.HasCount(3, replaced);
        CollectionAssert.Contains(replaced, "DELTA_URL");
        CollectionAssert.Contains(replaced, "DELTA_USER");
        CollectionAssert.Contains(replaced, "DELTA_PASS");
    }

    [TestMethod]
    public void SubstituteCredentials_WithQueryParamsOnly_NormalizesCorrectly()
    {
        var env = new Dictionary<string, string>
        {
            ["BASE"] = "https://example.com/",
        };
        var url = "%BASE%?param1=value1&param2=value2";

        var result = UrlSubstitutor.SubstituteCredentials(url, env, out var replaced);

        // UriBuilder should handle this correctly
        Assert.IsNotNull(result);
        StringAssert.Contains(result, "example.com");
        StringAssert.Contains(result, "param1=value1");
    }
}

