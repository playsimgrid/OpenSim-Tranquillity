using Nini.Config;

namespace OpenSim.Services.S3AssetService;

internal sealed class S3AssetOptions
{
    public string Endpoint { get; init; }
    public string Region { get; init; }
    public string Bucket { get; init; }
    public string Prefix { get; init; } = "";
    public string AccessKeyEnvironment { get; init; }
    public string SecretKeyEnvironment { get; init; }
    public string CacheDirectory { get; init; }
    public long CacheMaxBytes { get; init; }
    public int MaxAssetBytes { get; init; } = 64 * 1024 * 1024;
    public int RequestTimeoutSeconds { get; init; } = 30;
    public int MaxConcurrentRequests { get; init; } = 16;
    public int MaxRetries { get; init; } = 2;
    public bool ReadOnly { get; init; } = true;
    public bool AllowMetadataDelete { get; init; }
    public bool UseOsgridFormat { get; init; }
    public bool MigrationMode { get; init; }
    public string LegacyBaseDirectory { get; init; } = "";
    public string LegacySpoolDirectory { get; init; } = "";

    public static S3AssetOptions FromConfig(IConfigSource source)
    {
        var c = source.Configs["S3AssetService"]
            ?? throw new ArgumentException("Missing [S3AssetService] configuration");
        string endpoint = c.GetString("Endpoint", "");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0)
            throw new ArgumentException("S3 Endpoint must be an HTTPS origin without a path or credentials");
        string bucket = c.GetString("Bucket", "");
        if (bucket.Length < 3 || bucket.Length > 63 || bucket.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '.')
            || bucket != bucket.ToLowerInvariant())
            throw new ArgumentException("Invalid S3 Bucket");
        string region = c.GetString("Region", "");
        if (string.IsNullOrWhiteSpace(region))
            throw new ArgumentException("S3 Region is required (auto for R2; the endpoint region for B2)");
        string prefix = c.GetString("KeyPrefix", "").Trim('/');
        if (prefix.Contains('\\') || prefix.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Invalid S3 KeyPrefix");
        if (!long.TryParse(c.GetString("CacheMaxBytes", "10737418240"), out long cacheBytes) || cacheBytes < 0)
            throw new ArgumentException("CacheMaxBytes must be a nonnegative integer");
        int maxBytes = c.GetInt("MaxAssetBytes", 64 * 1024 * 1024);
        int timeout = c.GetInt("RequestTimeoutSeconds", 30);
        int concurrency = c.GetInt("MaxConcurrentRequests", 16);
        if (concurrency < 1 || concurrency > 128) throw new ArgumentException("MaxConcurrentRequests must be 1..128");
        int retries = c.GetInt("MaxRetries", 2);
        if (maxBytes < 1 || maxBytes > 512 * 1024 * 1024 || timeout < 1 || timeout > 300 || retries < 0 || retries > 10)
            throw new ArgumentException("Invalid S3 asset size, timeout or retry limit");
        string cache = c.GetString("CacheDirectory", "");
        if (cacheBytes > 0 && string.IsNullOrWhiteSpace(cache))
            throw new ArgumentException("CacheDirectory is required when caching is enabled");
        string legacy = c.GetString("LegacyBaseDirectory", "");
        string spool = c.GetString("LegacySpoolDirectory", "");
        if (cacheBytes > 0)
        {
            string cachePath = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar);
            foreach (string old in new[] { legacy, spool }.Where(p => !string.IsNullOrEmpty(p)))
            {
                string oldPath = Path.GetFullPath(old).TrimEnd(Path.DirectorySeparatorChar);
                if (cachePath == oldPath || cachePath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || oldPath.StartsWith(cachePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Cache and legacy asset directories must not overlap");
            }
        }
        return new S3AssetOptions
        {
            Endpoint = uri.GetLeftPart(UriPartial.Authority), Region = region, Bucket = bucket,
            Prefix = prefix.Length == 0 ? "" : prefix + "/",
            AccessKeyEnvironment = c.GetString("AccessKeyEnvironment", "OPENSIM_S3_ACCESS_KEY_ID"),
            SecretKeyEnvironment = c.GetString("SecretKeyEnvironment", "OPENSIM_S3_SECRET_ACCESS_KEY"),
            CacheDirectory = cache, CacheMaxBytes = cacheBytes, MaxAssetBytes = maxBytes,
            RequestTimeoutSeconds = timeout, MaxRetries = retries, MaxConcurrentRequests = concurrency,
            ReadOnly = c.GetBoolean("ReadOnly", true),
            AllowMetadataDelete = c.GetBoolean("AllowMetadataDelete", false),
            UseOsgridFormat = source.Configs["AssetService"]?.GetBoolean("UseOsgridFormat", false) ?? false,
            MigrationMode = c.GetBoolean("MigrationMode", false),
            LegacyBaseDirectory = legacy, LegacySpoolDirectory = spool
        };
    }
}
