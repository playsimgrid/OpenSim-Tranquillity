namespace OpenSim.Services.S3AssetService;

/// <summary>A backend read failure; must not be translated to asset-not-found.</summary>
public sealed class AssetStorageException(string message) : Exception(message);
