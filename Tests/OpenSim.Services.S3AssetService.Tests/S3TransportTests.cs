using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;

namespace OpenSim.Services.S3AssetService.Tests;

[TestFixture]
public class S3TransportTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Respond(request, token);
    }
    private sealed class Factory(Handler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) => new(handler, false);
    }
    private static (S3ObjectStore Store, AmazonS3Client Client) Make(Handler handler, string region = "auto", int timeout = 5)
    {
        var options = new S3AssetOptions
        {
            Endpoint = "https://objects.invalid", Region = region, Bucket = "asset-test",
            RequestTimeoutSeconds = timeout, MaxRetries = 0
        };
        var config = S3ObjectStore.ClientConfig(options); config.HttpClientFactory = new Factory(handler);
        var client = new AmazonS3Client(new BasicAWSCredentials("test-access", "test-secret"), config);
        return (new S3ObjectStore(options, client), client);
    }
    private static HttpResponseMessage Error(HttpStatusCode status, string code)
        => new(status) { Content = new StringContent($"<Error><Code>{code}</Code><Message>test</Message></Error>", Encoding.UTF8, "application/xml") };

    [TestCase("auto", true)] [TestCase("us-west-004", false)]
    public void PutUsesProviderRegionSigV4AndUnmodifiedGzipBytes(string region, bool conditional)
    {
        byte[] payload = AssetBlob.Encode(Encoding.UTF8.GetBytes("payload"));
        var handler = new Handler { Respond = async (request, _) =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(request.RequestUri.AbsolutePath, Is.EqualTo("/asset-test/AA/BB/test.gz"));
            Assert.That(request.Headers.GetValues("Authorization").Single(), Does.StartWith("AWS4-HMAC-SHA256 "));
            Assert.That(request.Headers.GetValues("Authorization").Single(), Does.Contain($"/{region}/s3/aws4_request"));
            if (conditional) Assert.That(request.Headers.IfNoneMatch.Single().ToString(), Is.EqualTo("*"));
            else Assert.That(request.Headers.IfNoneMatch, Is.Empty);
            Assert.That(await request.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload));
            Assert.That(request.Content.Headers.ContentMD5, Is.EqualTo(MD5.HashData(payload)));
            Assert.That(request.Headers.TransferEncodingChunked, Is.Not.True);
            Assert.That(request.Content.Headers.ContentEncoding, Is.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }};
        var pair = Make(handler, region); using var client = pair.Client;
        Assert.That(pair.Store.Put("AA/BB/test.gz", payload, conditional), Is.True);
    }

    [TestCase(HttpStatusCode.NotFound, "NoSuchKey", true)]
    [TestCase(HttpStatusCode.NotFound, "NoSuchBucket", false)]
    [TestCase(HttpStatusCode.Forbidden, "AccessDenied", false)]
    [TestCase(HttpStatusCode.ServiceUnavailable, "SlowDown", false)]
    public void OnlyMissingObjectBecomesNull(HttpStatusCode status, string code, bool missing)
    {
        var handler = new Handler { Respond = (_, _) => Task.FromResult(Error(status, code)) };
        var pair = Make(handler); using var client = pair.Client;
        if (missing) Assert.That(pair.Store.Get("key"), Is.Null);
        else Assert.Throws<AmazonS3Exception>(() => pair.Store.Get("key"));
    }

    [Test]
    public void GetReturnsExactStoredBytes()
    {
        byte[] data = AssetBlob.Encode(Encoding.UTF8.GetBytes("asset data"));
        var handler = new Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(data) }) };
        var pair = Make(handler); using var client = pair.Client;
        Assert.That(pair.Store.Get("key.gz"), Is.EqualTo(data));
    }

    [Test]
    public void ConditionalPutDetectsExistingObjectButPermissionErrorPropagates()
    {
        var handler = new Handler { Respond = (_, _) => Task.FromResult(Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed")) };
        var pair = Make(handler); using var client = pair.Client;
        Assert.That(pair.Store.Put("key", new byte[] { 1 }, onlyIfAbsent: true), Is.False);
        handler.Respond = (_, _) => Task.FromResult(Error(HttpStatusCode.Forbidden, "AccessDenied"));
        Assert.Throws<AmazonS3Exception>(() => pair.Store.Put("key", new byte[] { 1 }, onlyIfAbsent: true));
    }

    [Test]
    public void HeadMissingReturnsFalseButPermissionErrorPropagates()
    {
        var handler = new Handler { Respond = (_, _) => Task.FromResult(Error(HttpStatusCode.NotFound, "NotFound")) };
        var pair = Make(handler); using var client = pair.Client;
        Assert.That(pair.Store.Exists("key"), Is.False);
        handler.Respond = (_, _) => Task.FromResult(Error(HttpStatusCode.Forbidden, "AccessDenied"));
        Assert.Throws<AmazonS3Exception>(() => pair.Store.Exists("key"));
    }

    [Test]
    public void TimeoutCancelsSdkRequest()
    {
        var handler = new Handler { Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }};
        var pair = Make(handler, timeout: 1); using var client = pair.Client;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.That(() => pair.Store.Get("key"), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(5));
    }
}
