using System.Security.Cryptography;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using PlaySim.R2Probe;

if (args.Length != 5)
    throw new ArgumentException("Usage: R2AssetProbe module.dll endpoint bucket hash expected-decoded-bytes");
log4net.Config.BasicConfigurator.Configure();
ProbeMetadata.Hash = args[3];
var config = new IniConfigSource();
var assetConfig = config.AddConfig("AssetService");
assetConfig.Set("StorageProvider", typeof(ProbeMetadata).Assembly.Location + ":ProbeMetadata");
assetConfig.Set("ConnectionString", "in-memory-probe-only");
var cloud = config.AddConfig("S3AssetService");
cloud.Set("Endpoint", args[1]); cloud.Set("Region", "auto"); cloud.Set("Bucket", args[2]);
cloud.Set("CacheMaxBytes", "0"); cloud.Set("ReadOnly", "true");
var service = new ServiceBase(config).LoadPlugin<IAssetService>(args[0] + ":S3AssetService", new object[] { config })
    ?? throw new Exception("Module did not load");
var asset = service.Get(ProbeMetadata.Id.ToString()) ?? throw new Exception("Asset not found in R2");
string actual = Convert.ToHexString(SHA256.HashData(asset.Data));
if (!actual.Equals(ProbeMetadata.Hash, StringComparison.OrdinalIgnoreCase) || asset.Data.Length != long.Parse(args[4]))
    throw new Exception("Asset readback mismatch");
Console.WriteLine($"PASS: actual S3AssetService loaded and read verified R2 asset; decoded_bytes={asset.Data.Length}; sha256={actual}; no database or source changes");

namespace PlaySim.R2Probe
{
    public sealed class ProbeMetadata : IFSAssetDataPlugin
    {
        public static readonly UUID Id = new("00000000-0000-0000-0000-000000000001");
        public static string Hash;
        public string Name => "In-memory R2 read probe";
        public string Version => "1";
        public void Initialise() { }
        public void Initialise(string connection, string realm, int days) { }
        public void Dispose() { }
        public bool[] AssetsExist(UUID[] ids) => ids.Select(id => id == Id).ToArray();
        public bool Delete(string id) => throw new NotSupportedException();
        public AssetMetadata Get(string id, out string hash)
        {
            hash = Hash;
            return id == Id.ToString() ? new AssetMetadata { ID = id, Type = (sbyte)AssetType.Texture } : null;
        }
        public bool Store(AssetMetadata metadata, string hash) => throw new NotSupportedException();
        public int Count() => 1;
        public void Import(string connection, string table, int start, int count, bool force, FSStoreDelegate store)
            => throw new NotSupportedException();
    }
}
