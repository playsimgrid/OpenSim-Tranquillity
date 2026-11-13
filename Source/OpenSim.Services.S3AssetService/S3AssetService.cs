using System.Runtime.CompilerServices;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.S3AssetService;

/// <summary>Optional, content-addressed S3 backend using the existing fsassets metadata schema.</summary>
public sealed class S3AssetService : ServiceBase, IAssetService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(S3AssetService));
    private static readonly ConditionalWeakTable<IConfigSource, System.Lazy<Backend>> Backends = new();
    private readonly Backend backend;

    public S3AssetService(IConfigSource config) : this(config, "AssetService") { }

    public S3AssetService(IConfigSource config, string configName) : base(config)
    {
        if (configName != "AssetService")
            throw new ArgumentException("S3AssetService must use the AssetService section");
        backend = Backends.GetValue(config, c => new System.Lazy<Backend>(() => CreateBackend(c))).Value;
        // The generic HGAssetService creates a second backing service with the same config.
        // Share clients/cache/locks and only load defaults once.
        lock (backend.DefaultsLock)
        {
            if (!backend.DefaultsLoaded && !backend.Options.ReadOnly)
            {
                var c = config.Configs["AssetService"];
                string loaderName = c.GetString("DefaultAssetLoader", "");
                if (loaderName.Length > 0)
                {
                    var loader = LoadPlugin<IAssetLoader>(loaderName)
                        ?? throw new ArgumentException("Could not load default asset loader");
                    loader.ForEachDefaultXmlAsset(c.GetString("AssetLoaderArgs", ""), asset =>
                    {
                        if (string.IsNullOrEmpty(Store(asset)))
                            throw new IOException("Failed to persist a default asset");
                    });
                }
                backend.DefaultsLoaded = true;
            }
        }
    }

    internal S3AssetService(IFSAssetDataPlugin metadata, IObjectStore store, S3AssetOptions options,
        IAssetService fallback = null) : base(new IniConfigSource())
    {
        backend = new Backend(metadata, store, options, fallback);
    }

    private Backend CreateBackend(IConfigSource config)
    {
        var options = S3AssetOptions.FromConfig(config);
        var c = config.Configs["AssetService"] ?? throw new ArgumentException("Missing [AssetService]");
        var db = config.Configs["DatabaseService"];
        string provider = c.GetString("StorageProvider", db?.GetString("StorageProvider", "") ?? "");
        string connection = c.GetString("ConnectionString", db?.GetString("ConnectionString", "") ?? "");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connection))
            throw new ArgumentException("S3 assets require an existing FS metadata provider and database");
        // Validate cloud credentials before loading the metadata plugin.
        var store = new S3ObjectStore(options);
        var metadata = LoadPlugin<IFSAssetDataPlugin>(provider)
            ?? throw new ArgumentException("Could not load FS asset metadata provider");
        metadata.Initialise(connection, c.GetString("Realm", "fsassets"), c.GetInt("DaysBetweenAccessTimeUpdates", 30));
        string fallbackName = c.GetString("FallbackService", "");
        IAssetService fallback = null;
        if (fallbackName.Length > 0)
        {
            if (fallbackName.Contains("S3AssetService", StringComparison.OrdinalIgnoreCase)
                || fallbackName.Contains("HGAssetService", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Asset fallback cannot refer back to the S3 or HG service");
            fallback = LoadPlugin<IAssetService>(fallbackName, new object[] { config })
                ?? throw new ArgumentException("Could not load legacy asset fallback");
        }
        Log.Info($"[S3ASSETS]: Enabled; read-only={options.ReadOnly}, cache limit={options.CacheMaxBytes} bytes");
        return new Backend(metadata, store, options, fallback);
    }

    public AssetBase Get(string id)
    {
        if (!TryId(id, out string canonical)) return null;
        var meta = backend.Metadata.Get(canonical, out string hash);
        if (meta == null) return backend.Fallback?.Get(canonical);
        byte[] data = ReadBlob(hash, false, meta.Type == (sbyte)AssetType.Object);
        if (data == null) return backend.Fallback?.Get(canonical);
        return MakeAsset(meta, data);
    }

    public AssetMetadata GetMetadata(string id)
    {
        if (!TryId(id, out string canonical)) return null;
        return backend.Metadata.Get(canonical, out _) ?? backend.Fallback?.GetMetadata(canonical);
    }

    public byte[] GetData(string id) => Get(id)?.Data;

    public AssetBase GetCached(string id)
    {
        if (!TryId(id, out string canonical)) return null;
        var meta = backend.Metadata.Get(canonical, out string hash);
        if (meta == null) return null;
        byte[] data = ReadBlob(hash, true);
        return data == null ? null : MakeAsset(meta, data);
    }

    public bool Get(string id, object sender, AssetRetrieved handler)
    {
        bool valid = TryId(id, out _);
        handler?.Invoke(id, sender, valid ? Get(id) : null);
        return valid;
    }

    // Remote URL fetching belongs to the region/HG connectors, never to this storage plugin.
    public AssetBase Get(string id, string foreignAssetService, bool storeOnLocalGrid) => null;
    public void Get(string id, string foreignAssetService, bool storeOnLocalGrid, SimpleAssetRetrieved callback)
        => callback?.Invoke(null);

    public bool[] AssetsExist(string[] ids)
    {
        if (ids == null) return Array.Empty<bool>();
        var valid = ids.Select(id => UUID.TryParse(id, out var uuid) ? uuid : UUID.Zero).ToArray();
        bool[] result = backend.Metadata.AssetsExist(valid);
        for (int i = 0; i < valid.Length; i++) if (valid[i].IsZero()) result[i] = false;
        return result;
    }

    public string Store(AssetBase asset)
    {
        if (backend.Options.ReadOnly || asset?.Data == null || asset.Data.Length == 0
            || asset.Data.Length > backend.Options.MaxAssetBytes) return string.Empty;
        if (string.IsNullOrEmpty(asset.ID) || asset.ID == UUID.Zero.ToString()) asset.FullID = UUID.Random();
        if (!TryId(asset.ID, out string id)) return string.Empty;
        byte[] data = asset.Data;
        if (asset.Type == (sbyte)AssetType.Object)
            data = Utils.StringToBytes(ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(data)));
        if (data.Length == 0 || data.Length > backend.Options.MaxAssetBytes) return string.Empty;
        string hash = AssetBlob.Hash(data); // Hash AFTER object sanitization, unlike the old FS writer.
        lock (backend.Stripe(hash))
        {
            try
            {
                using var lease = backend.Acquire();
                var existing = backend.Metadata.Get(id, out string oldHash);
                // fsassets is immutable by UUID; never report success for changed content it won't store.
                if (existing != null && !hash.Equals(oldHash, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                string storedHash = existing == null ? hash : oldHash;
                string key = Key(storedHash) + ".gz";
                // A local cache hit is not proof of cloud durability.
                if (backend.Options.MigrationMode)
                {
                    if (!backend.Store.Put(key, AssetBlob.Encode(data), onlyIfAbsent: true))
                    {
                        byte[] stored = backend.Store.Get(key)
                            ?? throw new IOException("Object disappeared after conditional upload");
                        AssetBlob.Decode(stored, true, storedHash, backend.Options.MaxAssetBytes);
                    }
                }
                else if (!backend.Store.Exists(key)) backend.Store.Put(key, AssetBlob.Encode(data));
                asset.Data = data;
                asset.Name = Truncate(asset.Name, AssetBase.MAX_ASSET_NAME);
                asset.Description = Truncate(asset.Description, AssetBase.MAX_ASSET_DESC);
                if (!backend.Metadata.Store(asset.Metadata, storedHash)) return string.Empty;
                // Some release metadata plugins return true even after a failed INSERT. Verify persistence.
                if (backend.Metadata.Get(id, out string confirmedHash) == null || confirmedHash != storedHash)
                    return string.Empty;
                backend.Cache.Put(storedHash, data);
                return id;
            }
            catch (Exception e)
            {
                // Do not log SDK exception payloads, which can include signed request information.
                Log.Error($"[S3ASSETS]: Store failed for {id} ({e.GetType().Name}); no success acknowledged");
                return string.Empty;
            }
        }
    }

    // Preserve the immutable UUID/content contract of FSAssetConnector.
    public bool UpdateContent(string id, byte[] data) => false;

    public bool Delete(string id)
    {
        if (backend.Options.ReadOnly || !backend.Options.AllowMetadataDelete || !TryId(id, out string canonical))
            return false;
        // Blob hashes are shared by UUIDs; deletion must never remove a shared cloud object.
        return backend.Metadata.Delete(canonical);
    }

    private string Key(string hash) => backend.Options.Prefix + AssetBlob.RelativeKey(hash, backend.Options.UseOsgridFormat);

    private byte[] ReadBlob(string hash, bool cacheOnly, bool sceneObject = false)
    {
        try { return ReadBlobCore(hash, cacheOnly, sceneObject); }
        catch (Exception e)
        {
            // BaseHttpServer has an IOException catch that only logs. Use a distinct
            // non-IO exception so its general handler sends HTTP 500, never an empty 200.
            // Do not expose SDK exception details or signed request material.
            throw new AssetStorageException($"S3 asset read failed ({e.GetType().Name})");
        }
    }

    private byte[] ReadBlobCore(string hash, bool cacheOnly, bool sceneObject)
    {
        AssetBlob.ValidateHash(hash);
        byte[] cached = backend.Cache.Get(hash);
        if (cached != null || cacheOnly) return cached;
        lock (backend.Stripe(hash))
        {
            cached = backend.Cache.Get(hash);
            if (cached != null) return cached;
            byte[] data;
            using (backend.Acquire())
            {
                string key = Key(hash);
                byte[] bytes = backend.Store.Get(key + ".gz");
                data = bytes == null ? null : AssetBlob.Decode(bytes, true, hash, backend.Options.MaxAssetBytes);
                if (data == null)
                {
                    bytes = backend.Store.Get(key);
                    if (bytes != null) data = AssetBlob.Decode(bytes, false, hash, backend.Options.MaxAssetBytes);
                }
            }
            // Only a genuine object miss permits filesystem fallback. Transport errors propagate
            // to the server's error handler, never becoming a missing asset/negative cache entry.
            if (data != null)
            {
                backend.Cache.Put(hash, data);
                return data;
            }
            data = ReadLegacy(hash, sceneObject);
            if (data != null && !backend.Options.MigrationMode) backend.Cache.Put(hash, data);
            return data;
        }
    }

    private byte[] ReadLegacy(string hash, bool sceneObject)
    {
        var o = backend.Options;
        var candidates = new List<(string Path, bool Compressed)>();
        if (o.LegacySpoolDirectory.Length > 0)
            candidates.Add((Path.Combine(o.LegacySpoolDirectory, hash + ".asset"), false));
        if (o.LegacyBaseDirectory.Length > 0)
        {
            string file = Path.Combine(o.LegacyBaseDirectory, AssetBlob.RelativeKey(hash, o.UseOsgridFormat));
            candidates.Add((file + ".gz", true));
            candidates.Add((file, false));
        }
        foreach (var candidate in candidates)
        {
            byte[] bytes;
            try
            {
                using var stream = File.OpenRead(candidate.Path);
                bytes = AssetBlob.ReadLimited(stream, o.MaxAssetBytes + 1024 * 1024);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            return AssetBlob.Decode(bytes, candidate.Compressed, hash, o.MaxAssetBytes,
                recoverLegacyObject: o.MigrationMode && sceneObject);
        }
        return null;
    }

    private static AssetBase MakeAsset(AssetMetadata meta, byte[] data)
    {
        // Preserve the existing FS Get sanitization without changing the stored/hash-verified bytes.
        if (meta.Type == (sbyte)AssetType.Object)
            data = Utils.StringToBytes(ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(data)));
        return new AssetBase { Metadata = meta, Data = data };
    }

    private static bool TryId(string id, out string canonical)
    {
        bool valid = UUID.TryParse(id, out UUID uuid) && !uuid.IsZero();
        canonical = uuid.ToString();
        return valid;
    }

    private static string Truncate(string value, int length) => (value ?? "")[..Math.Min(value?.Length ?? 0, length)];

    private sealed class Backend
    {
        public readonly IFSAssetDataPlugin Metadata;
        public readonly IObjectStore Store;
        public readonly S3AssetOptions Options;
        public readonly DiskAssetCache Cache;
        public readonly IAssetService Fallback;
        public readonly object DefaultsLock = new();
        public bool DefaultsLoaded;
        private readonly SemaphoreSlim requests;
        public IDisposable Acquire()
        {
            if (!requests.Wait(TimeSpan.FromSeconds(Options.RequestTimeoutSeconds)))
                throw new TimeoutException("S3 asset concurrency limit reached");
            return new RequestLease(requests);
        }
        private sealed class RequestLease(SemaphoreSlim semaphore) : IDisposable
        {
            public void Dispose() => semaphore.Release();
        }
        private readonly object[] stripes = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();
        public object Stripe(string hash) => stripes[Convert.ToByte(hash[..2], 16)];

        public Backend(IFSAssetDataPlugin metadata, IObjectStore store, S3AssetOptions options, IAssetService fallback)
        {
            Metadata = metadata; Store = store; Options = options; Fallback = fallback;
            Cache = new DiskAssetCache(options);
            requests = new SemaphoreSlim(options.MaxConcurrentRequests);
        }
    }
}
