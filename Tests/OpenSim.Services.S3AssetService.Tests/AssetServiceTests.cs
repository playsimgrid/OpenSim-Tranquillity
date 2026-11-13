using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using Service = OpenSim.Services.S3AssetService.S3AssetService;

namespace OpenSim.Services.S3AssetService.Tests;

[TestFixture, NonParallelizable]
public class AssetServiceTests
{
    private string temp;
    private MemoryMetadata metadata;
    private MemoryObjects objects;

    [SetUp]
    public void Setup()
    {
        temp = Path.Combine(Path.GetTempPath(), "s3assets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        metadata = new MemoryMetadata();
        objects = new MemoryObjects();
    }
    [TearDown] public void Cleanup() => Directory.Delete(temp, true);

    private S3AssetOptions Options(bool readOnly = false, long capacity = 1024 * 1024,
        string legacy = "", string spool = "", bool osgrid = false, string bucket = "test-assets",
        bool migration = false) => new()
    {
        Endpoint = "https://test.invalid", Bucket = bucket, Region = "auto", CacheDirectory = temp,
        CacheMaxBytes = capacity, ReadOnly = readOnly, LegacyBaseDirectory = legacy,
        LegacySpoolDirectory = spool, UseOsgridFormat = osgrid, MigrationMode = migration
    };
    private static AssetBase Asset(string body = "test payload", AssetType type = AssetType.Texture)
        => new(UUID.Random(), "sample", (sbyte)type, TestUsers.Creator.ToString()) { Data = Encoding.UTF8.GetBytes(body) };
    private void Seed(AssetBase asset, bool lower = false, bool gzip = true, bool osgrid = false)
    {
        string hash = AssetBlob.Hash(asset.Data);
        if (lower) hash = hash.ToLowerInvariant();
        metadata.Store(asset.Metadata, hash);
        objects.Objects[AssetBlob.RelativeKey(hash, osgrid) + (gzip ? ".gz" : "")]
            = gzip ? AssetBlob.Encode(asset.Data) : asset.Data;
    }

    [Test]
    public void UploadIsDurableBeforeMetadataAndRoundTrips()
    {
        var asset = Asset();
        objects.BeforePut = () => Assert.That(metadata.Get(asset.ID, out _), Is.Null);
        var service = new Service(metadata, objects, Options());
        Assert.That(service.Store(asset), Is.EqualTo(asset.ID));
        Assert.That(service.Get(asset.ID).Data, Is.EqualTo(asset.Data));
        Assert.That(metadata.Get(asset.ID, out string hash), Is.Not.Null);
        Assert.That(hash, Is.EqualTo(AssetBlob.Hash(asset.Data)));
        Assert.That(objects.Objects.Keys.Single(), Is.EqualTo(AssetBlob.RelativeKey(hash, false) + ".gz"));
    }

    [Test]
    public void FailedUploadDoesNotCreateMetadata()
    {
        objects.WriteFailure = new IOException("offline");
        var asset = Asset();
        Assert.That(new Service(metadata, objects, Options()).Store(asset), Is.Empty);
        Assert.That(metadata.Count(), Is.Zero);
    }

    [TestCase(false)] [TestCase(true)]
    public void MetadataFailureNeverAcknowledgesUpload(bool lie)
    {
        metadata.FailWrites = !lie; metadata.LieAboutWrites = lie;
        Assert.That(new Service(metadata, objects, Options()).Store(Asset()), Is.Empty);
        Assert.That(objects.Writes, Is.EqualTo(1)); // Safe orphan, not a dangling metadata row.
    }

    [Test]
    public void DuplicateContentUsesOneBlobAndPreservesBothIds()
    {
        var service = new Service(metadata, objects, Options());
        var a = Asset(); var b = Asset();
        Assert.That(service.Store(a), Is.EqualTo(a.ID));
        Assert.That(service.Store(b), Is.EqualTo(b.ID));
        Assert.That(metadata.Count(), Is.EqualTo(2));
        Assert.That(objects.Writes, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentDuplicateUploadsRemainReadable()
    {
        var service = new Service(metadata, objects, Options());
        var assets = Enumerable.Range(0, 20).Select(_ => Asset()).ToArray();
        System.Threading.Tasks.Parallel.ForEach(assets, a => Assert.That(service.Store(a), Is.EqualTo(a.ID)));
        Assert.That(objects.Writes, Is.EqualTo(1));
        Assert.That(metadata.Count(), Is.EqualTo(20));
    }

    [Test]
    public void ChangedContentUnderExistingUuidIsRejected()
    {
        var service = new Service(metadata, objects, Options()); var a = Asset();
        service.Store(a); byte[] original = a.Data;
        a.Data = Encoding.UTF8.GetBytes("different");
        Assert.That(service.Store(a), Is.Empty);
        Assert.That(service.GetData(a.ID), Is.EqualTo(original));
        Assert.That(service.UpdateContent(a.ID, a.Data), Is.False);
    }

    [TestCase(true, true, false)] [TestCase(false, false, false)] [TestCase(false, true, true)]
    public void ExistingKeysPreserveCaseCompressionAndLayout(bool lower, bool gzip, bool osgrid)
    {
        var a = Asset(); Seed(a, lower, gzip, osgrid);
        Assert.That(new Service(metadata, objects, Options(osgrid: osgrid)).GetData(a.ID), Is.EqualTo(a.Data));
    }

    [Test]
    public void CacheOnlyNeverFetchesFromCloudAndWarmCacheSurvivesOutage()
    {
        var a = Asset(); Seed(a); var service = new Service(metadata, objects, Options());
        Assert.That(service.GetCached(a.ID), Is.Null); Assert.That(objects.Reads, Is.Zero);
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        objects.ReadFailure = new IOException("offline");
        Assert.That(service.GetCached(a.ID).Data, Is.EqualTo(a.Data));
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(objects.Reads, Is.EqualTo(1));
    }

    [Test]
    public void CacheHitDoesNotCountAsProofOfUploadDurability()
    {
        var a = Asset(); Seed(a); var service = new Service(metadata, objects, Options());
        service.Get(a.ID); objects.Objects.Clear(); objects.WriteFailure = new IOException("offline");
        Assert.That(service.Store(a), Is.Empty);
    }

    [Test]
    public void CloudFailureIsNotNotFoundOrLegacyFallback()
    {
        var a = Asset(); Seed(a); objects.ReadFailure = new IOException("offline");
        Assert.Throws<AssetStorageException>(() => new Service(metadata, objects, Options()).Get(a.ID));
    }

    [Test]
    public void MissingBlobReturnsNullWithoutNegativeCache()
    {
        var a = Asset(); metadata.Store(a.Metadata, AssetBlob.Hash(a.Data));
        var service = new Service(metadata, objects, Options());
        Assert.That(service.Get(a.ID), Is.Null); Seed(a);
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
    }

    [Test]
    public void MigrationReadsFsAssetsUntilR2CopyAppears()
    {
        var a = Asset();
        string hash = AssetBlob.Hash(a.Data);
        metadata.Store(a.Metadata, hash);
        string root = Path.Combine(temp, "source");
        string key = AssetBlob.RelativeKey(hash, false) + ".gz";
        string file = Path.Combine(root, key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, AssetBlob.Encode(a.Data));
        var service = new Service(metadata, objects, Options(legacy: root, migration: true));
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(service.GetCached(a.ID), Is.Null);
        Assert.That(objects.Writes, Is.Zero);
        Seed(a);
        File.Delete(file);
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(service.GetCached(a.ID).Data, Is.EqualTo(a.Data));
        Assert.That(objects.Reads, Is.EqualTo(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MigrationServesKnownLegacyXmlMismatchOnlyWithProof(bool namespaceRewrite)
    {
        const string xml = "<SceneObjectGroup xmlns:xsd='a' xmlns:xsi='b'/>";
        string original = namespaceRewrite ? xml.Replace("xmlns:", "xmlns:xmlns:") + "\0" : xml;
        var a = Asset(original, AssetType.Object);
        string hash = AssetBlob.Hash(a.Data);
        metadata.Store(a.Metadata, hash);
        string root = Path.Combine(temp, "source");
        string file = Path.Combine(root, AssetBlob.RelativeKey(hash, false) + ".gz");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, AssetBlob.Encode(Encoding.UTF8.GetBytes(xml + "\0")));
        var service = new Service(metadata, objects, Options(legacy: root, migration: true));
        Assert.That(Encoding.UTF8.GetString(service.GetData(a.ID)).TrimEnd('\0'), Is.EqualTo(xml));
        File.WriteAllBytes(file, AssetBlob.Encode(Encoding.UTF8.GetBytes(xml.Replace("'a'", "'c'") + "\0")));
        Assert.Throws<AssetStorageException>(() => service.GetData(a.ID));
    }

    [Test]
    public void ConcurrentWriterCannotOverwriteConflictingR2Bytes()
    {
        var a = Asset();
        string key = AssetBlob.RelativeKey(AssetBlob.Hash(a.Data), false) + ".gz";
        byte[] conflicting = AssetBlob.Encode(Encoding.UTF8.GetBytes("different content"));
        objects.BeforePut = () => objects.Objects.TryAdd(key, conflicting);
        Assert.That(new Service(metadata, objects, Options(migration: true)).Store(a), Is.Empty);
        Assert.That(objects.Objects[key], Is.EqualTo(conflicting));
        Assert.That(metadata.Count(), Is.Zero);
    }

    [Test]
    public void CorruptCloudDataIsRejected()
    {
        var a = Asset(); Seed(a);
        objects.Objects[objects.Objects.Keys.Single()] = AssetBlob.Encode(Encoding.UTF8.GetBytes("corrupt"));
        Assert.Throws<AssetStorageException>(() => new Service(metadata, objects, Options()).Get(a.ID));
    }

    [Test]
    public void CorruptCacheIsRepairedFromCloud()
    {
        var a = Asset(); Seed(a); var service = new Service(metadata, objects, Options());
        service.Get(a.ID);
        File.WriteAllText(Directory.GetFiles(temp, "*.cache", SearchOption.AllDirectories).Single(), "broken");
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(objects.Reads, Is.EqualTo(2));
    }

    [Test]
    public void CacheEvictsOldestAndRespectsCapacityAcrossRestart()
    {
        var options = Options(capacity: 20); var cache = new DiskAssetCache(options);
        byte[] a = Encoding.UTF8.GetBytes("aaaaaaaaaaaa"), b = Encoding.UTF8.GetBytes("bbbbbbbbbbbb");
        cache.Put(AssetBlob.Hash(a), a); cache.Put(AssetBlob.Hash(b), b);
        Assert.That(cache.Get(AssetBlob.Hash(a)), Is.Null);
        Assert.That(new DiskAssetCache(options).Get(AssetBlob.Hash(b)), Is.EqualTo(b));
        Assert.That(Directory.GetFiles(temp, "*.cache", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), Is.LessThanOrEqualTo(20));
    }

    [Test]
    public void CacheIsIsolatedByBucket()
    {
        var a = Asset(); var cache = new DiskAssetCache(Options()); cache.Put(AssetBlob.Hash(a.Data), a.Data);
        Assert.That(new DiskAssetCache(Options(bucket: "different")).Get(AssetBlob.Hash(a.Data)), Is.Null);
    }

    [TestCase(false)] [TestCase(true)]
    public void FilesystemFallbackReadsWithoutChangingSourceOrCloud(bool spool)
    {
        var a = Asset(); string hash = AssetBlob.Hash(a.Data); metadata.Store(a.Metadata, hash);
        string old = Path.Combine(temp, "legacy"); Directory.CreateDirectory(old);
        string path = spool ? Path.Combine(old, hash + ".asset") : Path.Combine(old, AssetBlob.RelativeKey(hash, false) + ".gz");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] bytes = spool ? a.Data : AssetBlob.Encode(a.Data); File.WriteAllBytes(path, bytes);
        var service = new Service(metadata, objects, Options(legacy: spool ? "" : old, spool: spool ? old : ""));
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes)); Assert.That(objects.Writes, Is.Zero);
    }

    [Test]
    public void ReadOnlyBlocksMutations()
    {
        var a = Asset(); Seed(a); var service = new Service(metadata, objects, Options(readOnly: true));
        Assert.That(service.Store(Asset()), Is.Empty);
        Assert.That(service.Delete(a.ID), Is.False);
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
    }

    [Test]
    public void DeleteRemovesOnlyMetadataAndNeverSharedBlob()
    {
        var options = new S3AssetOptions { ReadOnly = false, AllowMetadataDelete = true, CacheMaxBytes = 0 };
        var a = Asset(); var b = Asset(); Seed(a); Seed(b);
        var service = new Service(metadata, objects, options);
        Assert.That(service.Delete(a.ID), Is.True);
        Assert.That(objects.Objects.Count, Is.EqualTo(1));
        Assert.That(service.GetData(b.ID), Is.EqualTo(b.Data));
    }

    [Test]
    public void InvalidHashCannotTraverseFilesystem()
    {
        var a = Asset(); metadata.Store(a.Metadata, "../../secret");
        Assert.Throws<AssetStorageException>(() => new Service(metadata, objects, Options()).Get(a.ID));
        Assert.That(objects.Reads, Is.Zero);
    }

    [Test]
    public void DecompressedPayloadLimitIsEnforced()
    {
        byte[] data = new byte[10000];
        Assert.Throws<InvalidDataException>(() => AssetBlob.Decode(AssetBlob.Encode(data), true, AssetBlob.Hash(data), 100));
    }

    [Test]
    public void InvalidIdsAndUnsupportedRemoteCallbackAreHandled()
    {
        var service = new Service(metadata, objects, Options()); int calls = 0;
        Assert.That(service.Get("bad", null, (id, sender, a) => { calls++; Assert.That(a, Is.Null); }), Is.False);
        service.Get("bad", "https://untrusted.invalid", false, a => { calls++; Assert.That(a, Is.Null); });
        Assert.That(calls, Is.EqualTo(2)); Assert.That(service.AssetsExist(new[] { "bad" }), Is.EqualTo(new[] { false }));
    }

    [Test]
    public void ObjectSanitizationHappensBeforeHashing()
    {
        var a = Asset("<Scene xmlns:xmlns:x='urn:test'><Name>example</Name></Scene>", AssetType.Object);
        var service = new Service(metadata, objects, Options());
        Assert.That(service.Store(a), Is.EqualTo(a.ID));
        Assert.That(Encoding.UTF8.GetString(a.Data), Does.Not.Contain("xmlns:xmlns:"));
        metadata.Get(a.ID, out string hash);
        Assert.That(hash, Is.EqualTo(AssetBlob.Hash(a.Data)));
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
    }

    [Test]
    public void HistoricalHashMismatchRaisesServerErrorInsteadOfServingWrongData()
    {
        var a = Asset(); metadata.Store(a.Metadata, new string('A', 64));
        objects.Objects[AssetBlob.RelativeKey(new string('A', 64), false) + ".gz"] = AssetBlob.Encode(a.Data);
        var service = new Service(metadata, objects, Options());
        var error = Assert.Throws<AssetStorageException>(() => service.Get(a.ID));
        Assert.That(error, Is.Not.InstanceOf<IOException>());
    }

    [Test]
    public void ZeroAndOversizedUploadsAreRejectedWithoutMetadata()
    {
        var service = new Service(metadata, objects, new S3AssetOptions { ReadOnly = false, MaxAssetBytes = 4 });
        Assert.That(service.Store(Asset("")), Is.Empty);
        Assert.That(service.Store(Asset("12345")), Is.Empty);
        Assert.That(metadata.Count(), Is.Zero); Assert.That(objects.Writes, Is.Zero);
    }

    [Test]
    public void CacheWriteFailureDoesNotFailDurableStore()
    {
        var options = Options(); var service = new Service(metadata, objects, options);
        string cacheDirectory = Directory.GetDirectories(temp).Single();
        Directory.Delete(cacheDirectory);
        File.WriteAllText(cacheDirectory, "not a directory");
        var a = Asset();
        Assert.That(service.Store(a), Is.EqualTo(a.ID));
        Assert.That(objects.Writes, Is.EqualTo(1));
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
    }

    [Test]
    public void LegacyDatabaseFallbackIsReadWithoutPromotion()
    {
        var legacyMetadata = new MemoryMetadata(); var legacyObjects = new MemoryObjects();
        var legacy = new Service(legacyMetadata, legacyObjects, Options(bucket: "legacy"));
        var a = Asset(); legacy.Store(a);
        var service = new Service(metadata, objects, Options(), legacy);
        Assert.That(service.GetData(a.ID), Is.EqualTo(a.Data));
        Assert.That(metadata.Count(), Is.Zero); Assert.That(objects.Writes, Is.Zero);
    }

    private HGAssetService Hypergrid(Service backing)
    {
        TestBackingService.Target = backing;
        var config = new IniConfigSource(); var hg = config.AddConfig("HGAssetService");
        string dll = typeof(TestUsers).Assembly.Location;
        hg.Set("UserAccountsService", dll + ":TestUsers"); hg.Set("BackingService", dll + ":TestBackingService");
        hg.Set("HomeURI", "https://home.invalid");
        hg.Set("DisallowExport", "lsltext"); hg.Set("DisallowImport", "lsltext");
        return new HGAssetService(config, "HGAssetService");
    }

    [Test]
    public void ExistingHypergridWrapperPreservesPermissionsAndCreatorIdentity()
    {
        var service = new Service(metadata, objects, Options()); var hg = Hypergrid(service);
        var texture = Asset(); var script = Asset("script", AssetType.LSLText);
        service.Store(texture); service.Store(script);
        Assert.That(hg.Get(script.ID), Is.Null); Assert.That(hg.GetData(script.ID), Is.Null);
        Assert.That(hg.GetCached(script.ID), Is.Null); Assert.That(hg.Store(script), Is.Empty);
        Assert.That(hg.Delete(texture.ID), Is.False); Assert.That(hg.UpdateContent(texture.ID, texture.Data), Is.False);
        var exported = hg.Get(texture.ID);
        Assert.That(exported.Data, Is.EqualTo(texture.Data));
        Assert.That(exported.Metadata.CreatorID, Does.Contain("https://home.invalid/Test Creator"));
        Assert.That(service.Get(texture.ID).Metadata.CreatorID, Is.EqualTo(TestUsers.Creator.ToString()));
    }
}
