using System.Security.Cryptography;
using System.Text;

namespace QbtAutoUpdate.Core;

public sealed class InsufficientDiskSpaceException(string directory, long required, long available)
    : IOException($"Not enough free space in {directory}: {required / (1024 * 1024)} MB needed, {available / (1024 * 1024)} MB available.");

public sealed record DownloadedRelease(QbtVersion Version, string InstallerPath, string SignaturePath, string InstallerFileName);

/// <summary>
/// Downloads an installer and its detached signature from SourceForge. Each attempt lets
/// SourceForge pick a mirror again, and every response is checked before it is kept:
/// no HTML instead of a binary, expected size, PE header, armored signature.
/// </summary>
public sealed class Downloader(SafeHttpClient http, ILog log)
{
    public const int MaxAttempts = 3;
    public const long MaxInstallerBytes = 512L * 1024 * 1024;
    public const long MaxSignatureBytes = 64 * 1024;

    private const long DiskMargin = 64L * 1024 * 1024;
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SignatureTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Delay before attempt n (index 0 unused). Short enough for an interactive run.</summary>
    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    public async Task<DownloadedRelease> DownloadAsync(ResolvedRelease release, string targetDir, CancellationToken cancellationToken)
    {
        if (release.Status != ResolveStatus.Found || release.Installer is null || release.Signature is null)
            throw new ArgumentException("The release was not resolved to an installer.", nameof(release));

        Directory.CreateDirectory(targetDir);
        EnsureDiskSpace(targetDir, (release.Installer.Size ?? 100L * 1024 * 1024) + DiskMargin);

        // The signature first: it is tiny, and a missing one makes the big download pointless.
        string signaturePath = await DownloadFileAsync(release.Version, release.Signature, targetDir, isSignature: true, cancellationToken).ConfigureAwait(false);
        string installerPath = await DownloadFileAsync(release.Version, release.Installer, targetDir, isSignature: false, cancellationToken).ConfigureAwait(false);
        return new DownloadedRelease(release.Version, installerPath, signaturePath, release.Installer.Name);
    }

    private async Task<string> DownloadFileAsync(QbtVersion version, RemoteFile file, string targetDir, bool isSignature, CancellationToken cancellationToken)
    {
        string finalPath = Path.Combine(targetDir, file.Name);
        string partialPath = finalPath + ".partial";
        Uri url = SourceForgeClient.DownloadUrl(version, file.Name);

        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], cancellationToken).ConfigureAwait(false);

            try
            {
                string mirror = await DownloadOnceAsync(url, file, partialPath, isSignature, cancellationToken).ConfigureAwait(false);
                await MoveWithRetryAsync(partialPath, finalPath, cancellationToken).ConfigureAwait(false);
                log.Info($"Downloaded {file.Name} from {mirror} (attempt {attempt}).");
                return finalPath;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts && IsRetryable(ex))
            {
                log.Warn($"Download of {file.Name} failed (attempt {attempt}/{MaxAttempts}): {ex.Message}");
                TryDelete(partialPath);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }
    }

    /// <summary>Downloads to <paramref name="partialPath"/> and validates it. Returns the mirror host.</summary>
    private async Task<string> DownloadOnceAsync(Uri url, RemoteFile file, string partialPath, bool isSignature, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(isSignature ? SignatureTimeout : InstallerTimeout);

        using HttpResponseMessage response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
        SafeHttpClient.EnsureSuccess(response);
        string mirror = response.RequestMessage?.RequestUri?.Host ?? url.Host;

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{mirror} returned an HTML page instead of {file.Name}.");

        long limit = isSignature ? MaxSignatureBytes : MaxInstallerBytes;
        long? declared = response.Content.Headers.ContentLength;
        if (declared > limit)
            throw new InvalidDataException($"{file.Name} is larger than the {limit} byte limit.");

        long length;
        await using (Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
        await using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            length = await SafeHttpClient.CopyWithLimitAsync(source, target, limit, timeout.Token).ConfigureAwait(false);
        }

        if (file.Size is { } expectedSize && length != expectedSize)
            throw new InvalidDataException($"{file.Name} from {mirror} is {length} bytes, SourceForge lists {expectedSize}.");

        ValidateContent(partialPath, isSignature, file.Name, mirror);

        if (file.Sha256 is { } expectedHash)
        {
            string actual = await Sha256Async(partialPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file.Name} from {mirror} does not match the SHA-256 listed by SourceForge.");
        }

        return mirror;
    }

    /// <summary>Rejects HTML error pages and truncated files before the signature check even runs.</summary>
    internal static void ValidateContent(string path, bool isSignature, string name, string mirror)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[64];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];

        if (isSignature)
        {
            string text = Encoding.ASCII.GetString(head).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (!text.StartsWith("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal))
                throw new InvalidDataException($"{name} from {mirror} is not an armored PGP signature.");
        }
        else if (read < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
        {
            throw new InvalidDataException($"{name} from {mirror} is not a Windows executable.");
        }
    }

    internal static void EnsureDiskSpace(string directory, long required)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrEmpty(root))
            return;
        long available = new DriveInfo(root).AvailableFreeSpace;
        if (available < required)
            throw new InsufficientDiskSpaceException(directory, required, available);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Antivirus scanners often hold a freshly written executable for a moment.</summary>
    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception ex) =>
        ex is not InsufficientDiskSpaceException and not BlockedUrlException
        && (VersionSource.IsNetworkFailure(ex) || ex is InvalidDataException or UnauthorizedAccessException);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
