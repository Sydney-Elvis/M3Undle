using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

[TestClass]
public sealed class HdHomeRunDeviceServiceTests
{
    [TestMethod]
    public async Task DeviceIdentity_PersistsAcrossServiceRestarts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"m3undle-hdhr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runtimePaths = new RuntimePaths(
                DataDirectory: tempDir,
                DatabasePath: Path.Combine(tempDir, "test.db"),
                DatabaseConnectionString: $"Data Source={Path.Combine(tempDir, "test.db")}",
                LogDirectory: tempDir,
                SnapshotDirectory: tempDir);

            var options = Options.Create(new HdHomeRunOptions
            {
                FriendlyName = "Test HDHR",
                ModelNumber = "HDHR-TEST",
            });
            var configuration = new ConfigurationBuilder().Build();
            var env = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);

            var firstService = new HdHomeRunDeviceService(
                runtimePaths,
                options,
                configuration,
                env,
                NullLogger<HdHomeRunDeviceService>.Instance);

            var firstDevice = await firstService.GetDeviceDescriptorAsync(CancellationToken.None);
            var secondDevice = await firstService.GetDeviceDescriptorAsync(CancellationToken.None);

            var restartedService = new HdHomeRunDeviceService(
                runtimePaths,
                options,
                configuration,
                env,
                NullLogger<HdHomeRunDeviceService>.Instance);
            var thirdDevice = await restartedService.GetDeviceDescriptorAsync(CancellationToken.None);

            Assert.AreEqual(firstDevice.DeviceId, secondDevice.DeviceId);
            Assert.AreEqual(firstDevice.DeviceAuth, secondDevice.DeviceAuth);
            Assert.AreEqual(firstDevice.DeviceId, thirdDevice.DeviceId);
            Assert.AreEqual(firstDevice.DeviceAuth, thirdDevice.DeviceAuth);
            Assert.AreEqual("Test HDHR", thirdDevice.FriendlyName);
            Assert.AreEqual("HDHR-TEST", thirdDevice.ModelNumber);
            Assert.IsTrue(HdHomeRunDeviceService.IsValidDeviceId(firstDevice.DeviceId));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveBaseUrl_UsesAdvertisedOverride()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"m3undle-hdhr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runtimePaths = new RuntimePaths(
                DataDirectory: tempDir,
                DatabasePath: Path.Combine(tempDir, "test.db"),
                DatabaseConnectionString: $"Data Source={Path.Combine(tempDir, "test.db")}",
                LogDirectory: tempDir,
                SnapshotDirectory: tempDir);

            var options = Options.Create(new HdHomeRunOptions
            {
                AdvertisedBaseUrl = "https://tv.example.com/hdhr/",
            });

            var service = new HdHomeRunDeviceService(
                runtimePaths,
                options,
                new ConfigurationBuilder().Build(),
                new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance),
                NullLogger<HdHomeRunDeviceService>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost", 8080);

            var baseUrl = service.ResolveBaseUrl(context);
            Assert.AreEqual("https://tv.example.com/hdhr", baseUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

