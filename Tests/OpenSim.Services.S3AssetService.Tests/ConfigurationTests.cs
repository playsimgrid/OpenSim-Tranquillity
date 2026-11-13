using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using Service = OpenSim.Services.S3AssetService.S3AssetService;

namespace OpenSim.Services.S3AssetService.Tests;

[TestFixture, NonParallelizable]
public class ConfigurationTests
{
    private static IniConfigSource Config()
    {
        var config = new IniConfigSource(); var c = config.AddConfig("S3AssetService");
        c.Set("Endpoint", "https://objects.invalid"); c.Set("Region", "auto"); c.Set("Bucket", "asset-test");
        c.Set("CacheMaxBytes", "0");
        var asset = config.AddConfig("AssetService");
        asset.Set("StorageProvider", typeof(MemoryMetadata).Assembly.Location + ":MemoryMetadata");
        asset.Set("ConnectionString", "test-only");
        return config;
    }

    [TestCase("Endpoint", "http://objects.invalid")]
    [TestCase("Endpoint", "https://user:password@objects.invalid")]
    [TestCase("Endpoint", "https://objects.invalid/path")]
    [TestCase("Bucket", "../bad")]
    [TestCase("KeyPrefix", "assets/../secret")]
    [TestCase("CacheMaxBytes", "-1")]
    [TestCase("MaxAssetBytes", "0")]
    [TestCase("RequestTimeoutSeconds", "0")]
    [TestCase("MaxRetries", "-1")]
    [TestCase("MaxConcurrentRequests", "0")]
    public void InvalidConfigurationFailsAtStartup(string key, string value)
    {
        var config = Config(); config.Configs["S3AssetService"].Set(key, value);
        Assert.Throws<ArgumentException>(() => S3AssetOptions.FromConfig(config));
    }

    [Test]
    public void CacheCannotOverlapLegacyStore()
    {
        var config = Config(); var c = config.Configs["S3AssetService"];
        c.Set("CacheMaxBytes", "1024"); c.Set("CacheDirectory", "/tmp/assets/cache"); c.Set("LegacyBaseDirectory", "/tmp/assets");
        Assert.Throws<ArgumentException>(() => S3AssetOptions.FromConfig(config));
    }

    [Test]
    public void DefaultsAreReadOnlyAndDisableDeletion()
    {
        var options = S3AssetOptions.FromConfig(Config());
        Assert.That(options.ReadOnly, Is.True); Assert.That(options.AllowMetadataDelete, Is.False);
    }

    [Test]
    public void RealPluginLoaderLoadsModuleAndSharesBackingWithHypergridConstructor()
    {
        var config = Config(); var c = config.Configs["S3AssetService"];
        string accessName = "S3_TEST_ACCESS_" + Guid.NewGuid().ToString("N");
        string secretName = "S3_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        c.Set("AccessKeyEnvironment", accessName); c.Set("SecretKeyEnvironment", secretName);
        Environment.SetEnvironmentVariable(accessName, "test-access"); Environment.SetEnvironmentVariable(secretName, "test-secret");
        try
        {
            string plugin = typeof(Service).Assembly.Location + ":S3AssetService";
            var first = ServerUtils.LoadPlugin<IAssetService>(plugin, new object[] { config, "AssetService" });
            Assert.That(first, Is.TypeOf<Service>());
            // A second configuration entry must reuse the validated client rather than construct it again.
            Environment.SetEnvironmentVariable(accessName, null); Environment.SetEnvironmentVariable(secretName, null);
            var second = ServerUtils.LoadPlugin<IAssetService>(plugin, new object[] { config });
            Assert.That(second, Is.TypeOf<Service>());
            Assert.That(first.Store(new OpenSim.Framework.AssetBase()), Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(accessName, null); Environment.SetEnvironmentVariable(secretName, null);
        }
    }
}
