// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting;

// Writes durable browser automation command artifacts outside of command output so commands can return concise, agent-friendly
// summaries while large payloads stay on disk.
internal interface IBrowserLogsArtifactWriter
{
    Task<BrowserLogsArtifact> WriteArtifactAsync(
        string? appHostKey,
        string resourceName,
        string artifactType,
        string fileExtension,
        string mimeType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

internal sealed class BrowserLogsArtifactWriter : IBrowserLogsArtifactWriter
{
    private const int AppHostKeySegmentLength = 16;
    private readonly Func<string> _rootDirectoryFactory;
    private readonly TimeProvider _timeProvider;

    public BrowserLogsArtifactWriter(TimeProvider timeProvider)
        : this(timeProvider, GetAspireCommandArtifactRoot)
    {
    }

    internal BrowserLogsArtifactWriter(TimeProvider timeProvider, Func<string> rootDirectoryFactory)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(rootDirectoryFactory);

        _timeProvider = timeProvider;
        _rootDirectoryFactory = rootDirectoryFactory;
    }

    public async Task<BrowserLogsArtifact> WriteArtifactAsync(
        string? appHostKey,
        string resourceName,
        string artifactType,
        string fileExtension,
        string mimeType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        var createdAt = _timeProvider.GetUtcNow();
        var normalizedArtifactType = SanitizePathSegment(artifactType, fallback: "artifact");
        var directory = Path.Combine(
            _rootDirectoryFactory(),
            GetAppHostSegment(appHostKey),
            SanitizePathSegment(resourceName, fallback: "resource"),
            normalizedArtifactType);

        Directory.CreateDirectory(directory);

        var timestamp = createdAt.UtcDateTime.ToString("yyyyMMddTHHmmss.fffffffZ", CultureInfo.InvariantCulture);
        var extension = fileExtension.StartsWith(".", StringComparison.Ordinal) ? fileExtension : "." + fileExtension;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var fileName = $"{timestamp}-{normalizedArtifactType}{suffix}{extension}";
            var filePath = Path.Combine(directory, fileName);

            FileStream stream;
            try
            {
                stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            }
            catch (IOException) when (attempt < 99 && File.Exists(filePath))
            {
                continue;
            }

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            return new BrowserLogsArtifact(resourceName, artifactType, filePath, mimeType, content.Length, createdAt);
        }

        throw new IOException($"Unable to create a unique browser automation command artifact file under '{directory}'.");
    }

    private static string GetAppHostSegment(string? appHostKey)
    {
        if (string.IsNullOrWhiteSpace(appHostKey))
        {
            return "unknown-apphost";
        }

        var segment = appHostKey.Length > AppHostKeySegmentLength
            ? appHostKey[..AppHostKeySegmentLength]
            : appHostKey;

        return SanitizePathSegment(segment, fallback: "unknown-apphost");
    }

    private static string GetAspireCommandArtifactRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aspire",
                "CommandArtifacts");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Aspire",
                "CommandArtifacts");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "aspire", "command-artifacts");
    }

    private static string SanitizePathSegment(string value, string fallback)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        var hasValidCharacter = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (Array.IndexOf(invalidChars, c) >= 0 || c == ' ')
            {
                buffer[i] = '_';
            }
            else
            {
                buffer[i] = char.ToLowerInvariant(c);
                hasValidCharacter = true;
            }
        }

        var sanitized = hasValidCharacter ? new string(buffer) : fallback;
        return sanitized is "." or ".." ? fallback : sanitized;
    }
}

internal sealed record BrowserLogsArtifact(
    string ResourceName,
    string ArtifactType,
    string FilePath,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt);
