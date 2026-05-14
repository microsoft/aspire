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

    /// <summary>
    /// Fetches the published SHA-512 checksum for the latest CLI archive on
    /// the given channel without downloading the archive itself. The checksum
    /// is returned as a lowercase hex string with surrounding whitespace
    /// trimmed. Used to short-circuit a full download when the installed
    /// binary already matches the latest published build.
    /// </summary>
    Task<string> GetLatestChecksumAsync(string channelName, CancellationToken cancellationToken);
}

internal class CliDownloader(
    ILogger<CliDownloader> logger,
    IInteractionService interactionService,
    IPackagingService packagingService) : ICliDownloader
{
    private const int ArchiveDownloadTimeoutSeconds = 600;
    private const int ChecksumDownloadTimeoutSeconds = 120;
    
    private static readonly HttpClient s_httpClient = new();

    public async Task<string> GetLatestChecksumAsync(string channelName, CancellationToken cancellationToken)
    {
        var info = await BuildChannelDownloadInfoAsync(channelName, cancellationToken);

        logger.LogDebug("Fetching checksum from {Url}", info.ChecksumUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ChecksumDownloadTimeoutSeconds));

        using var response = await s_httpClient.GetAsync(info.ChecksumUrl, cts.Token);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(cts.Token);
        return NormalizeChecksum(raw);
    }

    public async Task<string> DownloadLatestCliAsync(string channelName, CancellationToken cancellationToken)
    {
        var info = await BuildChannelDownloadInfoAsync(channelName, cancellationToken);

        // Create temp directory for download
        var tempDir = Directory.CreateTempSubdirectory("aspire-cli-download").FullName;

        try
        {
            var archivePath = Path.Combine(tempDir, info.ArchiveFilename);
            var checksumPath = Path.Combine(tempDir, info.ChecksumFilename);
            var archiveDescriptor = GetDownloadDescriptor(info.ArchiveUrl, $"the {info.ChannelName} channel");

            _ = await interactionService.ShowStatusAsync($"Downloading {archiveDescriptor}", async () =>
            {
                logger.LogDebug("Downloading archive from {Url} to {Path}", info.ArchiveUrl, archivePath);
                await DownloadFileAsync(info.ArchiveUrl, archivePath, ArchiveDownloadTimeoutSeconds, cancellationToken);

                logger.LogDebug("Downloading checksum from {Url} to {Path}", info.ChecksumUrl, checksumPath);
                await DownloadFileAsync(info.ChecksumUrl, checksumPath, ChecksumDownloadTimeoutSeconds, cancellationToken);
                
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

    private async Task<ChannelDownloadInfo> BuildChannelDownloadInfoAsync(string channelName, CancellationToken cancellationToken)
    {
        var channels = await packagingService.GetChannelsAsync(cancellationToken);
        var channel = channels.FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            throw new ArgumentException($"Unsupported channel '{channelName}'. Available channels: {string.Join(", ", channels.Select(c => c.Name))}");
        }

        if (string.IsNullOrEmpty(channel.CliDownloadBaseUrl))
        {
            throw new InvalidOperationException($"Channel '{channelName}' does not support CLI downloads.");
        }

        var baseUrl = channel.CliDownloadBaseUrl.TrimEnd('/');
        var (os, arch) = DetectPlatform();
        var runtimeIdentifier = $"{os}-{arch}";
        var extension = os == "win" ? "zip" : "tar.gz";
        var archiveFilename = $"aspire-cli-{runtimeIdentifier}.{extension}";
        var checksumFilename = $"{archiveFilename}.sha512";

        return new ChannelDownloadInfo(
            channel.Name,
            archiveFilename,
            checksumFilename,
            $"{baseUrl}/{archiveFilename}",
            $"{baseUrl}/{checksumFilename}");
    }

    internal static string NormalizeChecksum(string raw)
    {
        // The published .sha512 file format mirrors `sha512sum` output. Two shapes
        // are observed across our publishing infrastructure:
        //   1. "<hex>\n"                                     (just the digest)
        //   2. "<hex>  aspire-cli-<rid>.tar.gz\n"            (digest + filename)
        // Take the leading hex token only and lowercase so comparisons are stable.
        var trimmed = raw.Trim();
        var firstWhitespace = IndexOfWhitespace(trimmed);
        if (firstWhitespace > 0)
        {
            trimmed = trimmed.Substring(0, firstWhitespace);
        }
        return trimmed.ToLowerInvariant();
    }

    private static int IndexOfWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return i;
            }
        }
        return -1;
    }

    private readonly record struct ChannelDownloadInfo(
        string ChannelName,
        string ArchiveFilename,
        string ChecksumFilename,
        string ArchiveUrl,
        string ChecksumUrl);

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

    private static (string os, string arch) DetectPlatform()
    {
        var os = DetectOperatingSystem();
        var arch = DetectArchitecture();
        return (os, arch);
    }

    private static string DetectOperatingSystem()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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

    private static async Task DownloadFileAsync(string url, string outputPath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var response = await s_httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream, cts.Token);
    }

    private static async Task ValidateChecksumAsync(string archivePath, string checksumPath, CancellationToken cancellationToken)
    {
        var expectedChecksum = NormalizeChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken));

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
