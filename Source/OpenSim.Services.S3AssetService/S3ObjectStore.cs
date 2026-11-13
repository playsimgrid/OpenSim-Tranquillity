using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace OpenSim.Services.S3AssetService;

internal interface IObjectStore
{
    byte[] Get(string key);
    bool Exists(string key);
    bool Put(string key, byte[] content, bool onlyIfAbsent = false);
}

internal sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 client;
    private readonly S3AssetOptions options;

    public S3ObjectStore(S3AssetOptions options)
    {
        this.options = options;
        string access = Environment.GetEnvironmentVariable(options.AccessKeyEnvironment);
        string secret = Environment.GetEnvironmentVariable(options.SecretKeyEnvironment);
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("S3 credential environment variables are missing");
        client = new AmazonS3Client(new BasicAWSCredentials(access, secret), ClientConfig(options));
    }

    internal static AmazonS3Config ClientConfig(S3AssetOptions options) => new()
        {
            ServiceURL = options.Endpoint, AuthenticationRegion = options.Region,
            ForcePathStyle = true, MaxErrorRetry = options.MaxRetries, RetryMode = RequestRetryMode.Standard,
            Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

    internal S3ObjectStore(S3AssetOptions options, IAmazonS3 client)
    {
        this.options = options;
        this.client = client;
    }

    public byte[] Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    private async Task<byte[]> GetAsync(string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
                { BucketName = options.Bucket, Key = key }, timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[32768];
            int count;
            while ((count = await response.ResponseStream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                // Allow gzip overhead, but bound compressed input as well as decoded data.
                if (output.Length + count > options.MaxAssetBytes + 1024 * 1024L)
                    throw new InvalidDataException("Stored asset exceeds size limit");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public bool Exists(string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = options.Bucket, Key = key }, timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound
            && (e.ErrorCode is "NoSuchKey" or "NotFound" || string.IsNullOrEmpty(e.ErrorCode)))
        {
            return false;
        }
    }

    public bool Put(string key, byte[] content, bool onlyIfAbsent = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var stream = new MemoryStream(content, false);
        try
        {
            client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket, Key = key, InputStream = stream,
                IfNoneMatch = onlyIfAbsent ? "*" : null,
                ContentType = "application/octet-stream", UseChunkEncoding = false,
                // Standard HTTPS SigV4, without AWS-specific streaming payload framing.
                DisablePayloadSigning = true,
                MD5Digest = Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(content))
            }, timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (AmazonS3Exception e) when (onlyIfAbsent && e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }
}
