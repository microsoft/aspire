// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

/// <summary>
/// Handles downloading the Aspire CLI.
/// </summary>
internal interface ICliDownloader
{
    Task<string> DownloadLatestCliAsync(string channelName, CancellationToken cancellationToken);
}

internal class CliDownloader(
    IEnvironment environment,
    ILogger<CliDownloader> logger,
    IInteractionService interactionService,
    IPackagingService packagingService) : ICliDownloader
{
    internal const string StagingDownloadBaseUrlEnvVar = "ASPIRE_CLI_STAGING_DOWNLOAD_BASE_URL";

    private const int ArchiveDownloadTimeoutSeconds = 600;
    private const int ChecksumDownloadTimeoutSeconds = 120;

    private static readonly HttpClient s_httpClient = new();
    private static readonly HttpClient s_loopbackHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });

    public async Task<string> DownloadLatestCliAsync(string channelName, CancellationToken cancellationToken)
    {
        // Get the channel information from PackagingService
        var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
        var channel = channels.FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            throw new ArgumentException($"Unsupported channel '{channelName}'. Available channels: {string.Join(", ", channels.Select(c => c.Name))}");
        }

        if (string.IsNullOrEmpty(channel.CliDownloadBaseUrl))
        {
            throw new InvalidOperationException($"Channel '{channelName}' does not support CLI downloads.");
        }

        var baseUrl = ResolveDownloadBaseUrl(channel.Name, channel.CliDownloadBaseUrl, environment);
        var requireLoopback = string.Equals(channel.Name, PackageChannelNames.Staging, StringComparisons.ChannelName) &&
            environment.GetEnvironmentVariable(StagingDownloadBaseUrlEnvVar) is { Length: > 0 };

        var (os, arch) = DetectPlatform();
        var runtimeIdentifier = $"{os}-{arch}";
        var extension = os == "win" ? "zip" : "tar.gz";
        var archiveFilename = $"aspire-cli-{runtimeIdentifier}.{extension}";
        var checksumFilename = $"{archiveFilename}.sha512";
        var archiveUrl = $"{baseUrl}/{archiveFilename}";
        var checksumUrl = $"{baseUrl}/{checksumFilename}";

        // Create temp directory for download
        var tempDir = Directory.CreateTempSubdirectory("aspire-cli-download").FullName;

        try
        {
            var archivePath = Path.Combine(tempDir, archiveFilename);
            var checksumPath = Path.Combine(tempDir, checksumFilename);
            var archiveDescriptor = GetDownloadDescriptor(archiveUrl, $"the {channel.Name} channel");

            _ = await interactionService.ShowStatusAsync($"Downloading {archiveDescriptor}", async () =>
            {
                logger.LogDebug("Downloading archive from {Url} to {Path}", archiveUrl, archivePath);
                await DownloadFileAsync(archiveUrl, archivePath, ArchiveDownloadTimeoutSeconds, cancellationToken, requireLoopback);

                logger.LogDebug("Downloading checksum from {Url} to {Path}", checksumUrl, checksumPath);
                await DownloadFileAsync(checksumUrl, checksumPath, ChecksumDownloadTimeoutSeconds, cancellationToken, requireLoopback);

                return 0; // Return dummy value for ShowStatusAsync
            });

            // Validate checksum
            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, "Validating downloaded file...");
            await ValidateChecksumAsync(archivePath, checksumPath, cancellationToken);

            interactionService.DisplaySuccess("Download completed successfully");
            return archivePath;
        }
        catch
        {
            // Clean up temp directory on failure
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up temporary directory {TempDir}", tempDir);
            }
            throw;
        }
    }

    internal static string GetDownloadDescriptor(string url, string? source = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var fileName = Path.GetFileName(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return url;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return fileName;
        }

        return $"{fileName} from {source}";
    }

    internal static string ResolveDownloadBaseUrl(string channelName, string defaultBaseUrl, IEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBaseUrl);
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.Equals(channelName, PackageChannelNames.Staging, StringComparisons.ChannelName) ||
            environment.GetEnvironmentVariable(StagingDownloadBaseUrlEnvVar) is not { Length: > 0 } overrideUrl)
        {
            return defaultBaseUrl.TrimEnd('/');
        }

        // This hook exists only for hermetic self-update tests. Restricting it to loopback prevents
        // repository or ambient machine configuration from redirecting executable replacement to a
        // remote host. Both the archive and checksum are served by this URL, so remote overrides
        // would otherwise allow arbitrary native-code execution.
        if (!Uri.TryCreate(overrideUrl, UriKind.Absolute, out var uri) ||
            !uri.IsLoopback ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{StagingDownloadBaseUrlEnvVar} must be an absolute loopback HTTP(S) URL.");
        }

        return overrideUrl.TrimEnd('/');
    }

    private (string os, string arch) DetectPlatform()
    {
        var os = DetectOperatingSystem();
        var arch = DetectArchitecture();
        return (os, arch);
    }

    private string DetectOperatingSystem()
    {
        if (environment.IsWindows())
        {
            return "win";
        }
        else if (environment.IsLinux())
        {
            // Check if it's musl-based (Alpine, etc.)
            try
            {
                var lddPath = "/usr/bin/ldd";
                if (File.Exists(lddPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = lddPath,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using var process = Process.Start(psi);
                    if (process is not null)
                    {
                        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        if (output.Contains("musl", StringComparison.OrdinalIgnoreCase))
                        {
                            return "linux-musl";
                        }
                    }
                }
            }
            catch
            {
                // Fall back to regular linux
            }
            return "linux";
        }
        else if (environment.IsMacOS())
        {
            return "osx";
        }
        else
        {
            throw new PlatformNotSupportedException($"Unsupported operating system: {RuntimeInformation.OSDescription}");
        }
    }

    private static string DetectArchitecture()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {arch}")
        };
    }

    internal static async Task DownloadFileAsync(
        string url,
        string outputPath,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool requireLoopback = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var httpClient = requireLoopback ? s_loopbackHttpClient : s_httpClient;
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        if (requireLoopback &&
            (response.RequestMessage?.RequestUri is not { IsLoopback: true } responseUri ||
             (responseUri.Scheme != Uri.UriSchemeHttp && responseUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                $"{StagingDownloadBaseUrlEnvVar} downloads must remain on an absolute loopback HTTP(S) URL.");
        }

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream, cts.Token);
    }

    private static async Task ValidateChecksumAsync(string archivePath, string checksumPath, CancellationToken cancellationToken)
    {
        var expectedChecksum = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Trim().ToLowerInvariant();

        using var sha512 = SHA512.Create();
        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hashBytes = await sha512.ComputeHashAsync(fileStream, cancellationToken);
        var actualChecksum = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (expectedChecksum != actualChecksum)
        {
            throw new InvalidOperationException($"Checksum validation failed. Expected: {expectedChecksum}, Actual: {actualChecksum}");
        }
    }
}
