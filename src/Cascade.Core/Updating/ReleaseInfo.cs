namespace Cascade.Core.Updating;

/// <summary>A release that could be installed: its version, and the single executable asset to fetch.</summary>
public sealed record ReleaseInfo(Version Version, string TagName, long AssetId, string AssetName, long Size);

/// <summary>Where releases come from. Abstracted so the update logic can be tested without a network.</summary>
public interface IReleaseSource
{
    /// <summary>The newest published release, or null if there is none or it could not be read.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct);

    /// <summary>Downloads the release's executable asset to <paramref name="destinationPath"/>.</summary>
    Task DownloadAssetAsync(ReleaseInfo release, string destinationPath, CancellationToken ct);
}
