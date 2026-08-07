namespace Cascade.Core.Updating;

/// <summary>A release that could be installed: its version, and the single executable asset to fetch.</summary>
public sealed record ReleaseInfo(Version Version, string TagName, long AssetId, string AssetName, long Size);

/// <summary>Where releases come from. Abstracted so the update logic can be tested without a network.</summary>
public interface IReleaseSource
{
    /// <summary>The newest published release, or null if there is none or it has no executable to install.
    /// Anything that went wrong is thrown, so the reason reaches the About box instead of being lost.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct);

    /// <summary>Downloads the release's executable asset to <paramref name="destinationPath"/>.</summary>
    Task DownloadAssetAsync(ReleaseInfo release, string destinationPath, CancellationToken ct);

    /// <summary>Something worth telling the user that did not stop the check - a credential that was
    /// refused, say. Null when there is nothing to say.</summary>
    string? Note => null;
}

/// <summary>An update check that could not be completed, described well enough to act on. Its message is
/// shown verbatim in the About box, so it names what was asked of whom and what came back.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message) : base(message) { }
    public UpdateCheckException(string message, Exception inner) : base(message, inner) { }
    public UpdateCheckException() { }
}
