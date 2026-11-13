using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.S3AssetService.Tests;

public sealed class MemoryMetadata : IFSAssetDataPlugin
{
    private readonly ConcurrentDictionary<string, (AssetMetadata Metadata, string Hash)> rows = new();
    public bool FailWrites;
    public bool LieAboutWrites;
    public string Name => "Test metadata";
    public string Version => "1";
    public void Initialise() { }
    public void Initialise(string connection, string realm, int days) { }
    public void Dispose() { }
    public bool[] AssetsExist(UUID[] ids) => ids.Select(id => rows.ContainsKey(id.ToString())).ToArray();
    public bool Delete(string id) => rows.TryRemove(id, out _);
    public AssetMetadata Get(string id, out string hash)
    {
        if (!rows.TryGetValue(id, out var row)) { hash = null; return null; }
        hash = row.Hash;
        var m = row.Metadata;
        return new AssetMetadata { ID = m.ID, Name = m.Name, Description = m.Description, Type = m.Type,
            CreatorID = m.CreatorID, CreationDate = m.CreationDate, Flags = m.Flags };
    }
    public bool Store(AssetMetadata metadata, string hash)
    {
        if (FailWrites) return false;
        if (!LieAboutWrites) rows.TryAdd(metadata.ID, (metadata, hash));
        return true;
    }
    public int Count() => rows.Count;
    public void Import(string connection, string table, int start, int count, bool force, FSStoreDelegate store)
        => throw new NotSupportedException();
}

internal sealed class MemoryObjects : IObjectStore
{
    public readonly ConcurrentDictionary<string, byte[]> Objects = new();
    public Exception ReadFailure;
    public Exception WriteFailure;
    public int Reads;
    public int Writes;
    public Action BeforePut;
    public byte[] Get(string key)
    {
        Interlocked.Increment(ref Reads);
        if (ReadFailure != null) throw ReadFailure;
        return Objects.TryGetValue(key, out var bytes) ? bytes.ToArray() : null;
    }
    public bool Exists(string key)
    {
        if (ReadFailure != null) throw ReadFailure;
        return Objects.ContainsKey(key);
    }

    public bool Put(string key, byte[] bytes, bool onlyIfAbsent = false)
    {
        BeforePut?.Invoke();
        if (WriteFailure != null) throw WriteFailure;
        if (onlyIfAbsent)
        {
            if (!Objects.TryAdd(key, bytes.ToArray())) return false;
        }
        else Objects[key] = bytes.ToArray();
        Interlocked.Increment(ref Writes);
        return true;
    }
}

public sealed class TestBackingService : IAssetService
{
    public static IAssetService Target;
    public TestBackingService(IConfigSource config) { }
    public AssetBase Get(string id) => Target.Get(id);
    public AssetBase Get(string id, string foreign, bool store) => Target.Get(id, foreign, store);
    public AssetMetadata GetMetadata(string id) => Target.GetMetadata(id);
    public byte[] GetData(string id) => Target.GetData(id);
    public AssetBase GetCached(string id) => Target.GetCached(id);
    public bool Get(string id, object sender, AssetRetrieved handler) => Target.Get(id, sender, handler);
    public void Get(string id, string foreign, bool store, SimpleAssetRetrieved callback) => Target.Get(id, foreign, store, callback);
    public bool[] AssetsExist(string[] ids) => Target.AssetsExist(ids);
    public string Store(AssetBase asset) => Target.Store(asset);
    public bool UpdateContent(string id, byte[] data) => Target.UpdateContent(id, data);
    public bool Delete(string id) => Target.Delete(id);
}

public sealed class TestUsers : IUserAccountService
{
    public static readonly UUID Creator = UUID.Random();
    public TestUsers(IConfigSource config) { }
    public UserAccount GetUserAccount(UUID scope, UUID id) => id == Creator
        ? new UserAccount { PrincipalID = id, FirstName = "Test", LastName = "Creator" } : null;
    public UserAccount GetUserAccount(UUID scope, string first, string last) => null;
    public UserAccount GetUserAccount(UUID scope, string email) => null;
    public bool SetDisplayName(UUID id, string name) => false;
    public List<UserAccount> GetUserAccounts(UUID scope, string query) => new();
    public List<UserAccount> GetUserAccountsWhere(UUID scope, string where) => new();
    public List<UserAccount> GetUserAccounts(UUID scope, List<string> ids) => new();
    public bool StoreUserAccount(UserAccount account) => false;
    public void InvalidateCache(UUID id) { }
}
