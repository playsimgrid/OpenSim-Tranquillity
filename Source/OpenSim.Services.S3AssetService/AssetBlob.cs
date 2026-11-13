using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenSim.Services.S3AssetService;

internal static class AssetBlob
{
    private static readonly Regex LegacyNamespaces = new(@"(?<![\w:])xmlns:(?=(?:xsd|xsi)\s*=)",
        RegexOptions.CultureInvariant);
    private static readonly Regex SceneObjectXml = new(@"\A(?:<\?xml[^>]*\?>\s*)?<SceneObjectGroup(?:[\s>])",
        RegexOptions.CultureInvariant);

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static void ValidateHash(string hash)
    {
        if (hash == null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Asset metadata contains an invalid SHA-256 key");
    }

    // Preserve the case in existing metadata: S3 object keys are case sensitive.
    public static string RelativeKey(string hash, bool osgrid)
    {
        ValidateHash(hash);
        return osgrid ? $"{hash[..3]}/{hash.Substring(3, 3)}/{hash}"
            : $"{hash[..2]}/{hash.Substring(2, 2)}/{hash.Substring(4, 2)}/{hash.Substring(6, 4)}/{hash}";
    }

    public static byte[] ReadLimited(Stream stream, int limit)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[32768];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("Asset exceeds MaxAssetBytes");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static byte[] Decode(byte[] stored, bool compressed, string hash, int limit, bool recoverLegacyObject = false)
    {
        using var input = new MemoryStream(stored, false);
        using Stream decoded = compressed ? new GZipStream(input, CompressionMode.Decompress) : input;
        byte[] data = ReadLimited(decoded, limit);
        if (Hash(data).Equals(hash, StringComparison.OrdinalIgnoreCase)) return data;
        if (recoverLegacyObject && data.Length > 1 && data[^1] == 0)
        {
            // Latin-1 round-trips every byte while the ASCII namespace markers are restored.
            string prefix = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 4096)).TrimStart();
            if (SceneObjectXml.IsMatch(prefix))
            {
                byte[] trimmed = data[..^1];
                if (Hash(trimmed).Equals(hash, StringComparison.OrdinalIgnoreCase)) return trimmed;
                if (data.Length <= 8 * 1024 * 1024)
                {
                    byte[] restored = Encoding.Latin1.GetBytes(
                        LegacyNamespaces.Replace(Encoding.Latin1.GetString(data), "xmlns:xmlns:"));
                    if (restored.Length <= limit && Hash(restored).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        return restored;
                }
            }
        }
        throw new InvalidDataException("Asset SHA-256 does not match its metadata key");
    }

    public static void Verify(byte[] data, string hash)
    {
        if (!Hash(data).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset SHA-256 does not match its metadata key");
    }

    public static byte[] Encode(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(data);
        return output.ToArray();
    }
}
