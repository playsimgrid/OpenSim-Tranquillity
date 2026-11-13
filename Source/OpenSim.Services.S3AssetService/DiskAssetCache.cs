using System.Security.Cryptography;
using System.Text;
using log4net;

namespace OpenSim.Services.S3AssetService;

// One instance per shared backend. Never points at the authoritative filesystem store.
internal sealed class DiskAssetCache
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(DiskAssetCache));
    private readonly object gate = new();
    private readonly string directory;
    private readonly long capacity;
    private readonly int maxAssetBytes;
    private readonly Dictionary<string, (long Size, LinkedListNode<string> Node)> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private long bytes;

    public DiskAssetCache(S3AssetOptions options)
    {
        capacity = options.CacheMaxBytes;
        maxAssetBytes = options.MaxAssetBytes;
        if (capacity == 0) return;
        string identity = $"{options.Endpoint}|{options.Bucket}|{options.Prefix}|{options.UseOsgridFormat}";
        if (options.MigrationMode) identity += "|migration";
        directory = Path.Combine(Path.GetFullPath(options.CacheDirectory),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        Directory.CreateDirectory(directory);
        // The cache directory is exclusive to this ROBUST process. Recover its index on restart.
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.cache").OrderBy(f => f.LastWriteTimeUtc))
        {
            string hash = Path.GetFileNameWithoutExtension(file.Name);
            try { AssetBlob.ValidateHash(hash); }
            catch (InvalidDataException) { continue; }
            entries[hash] = (file.Length, lru.AddLast(hash));
            bytes += file.Length;
        }
        EvictFor(0);
        foreach (string file in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(file);
    }

    public byte[] Get(string hash)
    {
        if (capacity == 0) return null;
        AssetBlob.ValidateHash(hash);
        hash = hash.ToUpperInvariant(); // Cache identity is content; remote keys retain original case.
        lock (gate)
        {
            if (!entries.TryGetValue(hash, out var entry)) return null;
            try
            {
                using var stream = File.OpenRead(FileName(hash));
                byte[] data = AssetBlob.ReadLimited(stream, maxAssetBytes);
                AssetBlob.Verify(data, hash);
                lru.Remove(entry.Node);
                lru.AddLast(entry.Node);
                return data;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn("[S3ASSETS]: Discarding unreadable or corrupt cache entry");
                try { Remove(hash); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return null;
            }
        }
    }

    public void Put(string hash, byte[] data)
    {
        if (capacity == 0 || data.LongLength > capacity) return;
        AssetBlob.ValidateHash(hash);
        hash = hash.ToUpperInvariant(); // Cache identity is content; remote keys retain original case.
        lock (gate)
        {
            string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (entries.ContainsKey(hash)) Remove(hash);
                EvictFor(data.LongLength);
                File.WriteAllBytes(temp, data);
                File.Move(temp, FileName(hash), true);
                entries[hash] = (data.LongLength, lru.AddLast(hash));
                bytes += data.LongLength;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A full/unwritable cache must not undo a durable cloud upload or a valid read.
                Log.Warn("[S3ASSETS]: Cache write unavailable; serving from object storage");
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string FileName(string hash) => Path.Combine(directory, hash + ".cache");

    private void EvictFor(long required)
    {
        while (bytes + required > capacity && lru.First != null) Remove(lru.First.Value);
    }

    private void Remove(string hash)
    {
        // Do not forget an entry if deleting it fails: it still consumes disk capacity.
        File.Delete(FileName(hash));
        if (entries.Remove(hash, out var entry))
        {
            bytes -= entry.Size;
            lru.Remove(entry.Node);
        }
    }
}
