// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IInstallSidecarReader"/> backed by the on-disk
/// <c>.aspire-install.json</c> file. Read with <see cref="JsonDocument"/>
/// (AOT-safe) and returns <see cref="InstallSource.Unknown"/> for missing,
/// empty, or unrecognized <c>source</c> values rather than throwing —
/// callers treat unknown sources as legacy / pre-sidecar installs and fall
/// back to the pre-sidecar layout heuristic.
/// </summary>
internal sealed class InstallSidecarReader : IInstallSidecarReader
{
    private readonly ILogger<InstallSidecarReader> _logger;

    public InstallSidecarReader(ILogger<InstallSidecarReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Well-known file name of the sidecar that each install route writes
    /// next to the CLI binary. Matches the contract in
    /// <c>docs/specs/install-routes.md</c>.
    /// </summary>
    public const string SidecarFileName = ".aspire-install.json";

    /// <summary>
    /// Upper bound on the sidecar file size we'll read into memory. The
    /// canonical payload is ~30 bytes (<c>{"source":"localhive"}</c>); we
    /// pick a small cap so that a pathological or malicious sidecar planted
    /// next to a candidate binary on PATH cannot force a large allocation
    /// during installation discovery.
    /// </summary>
    internal const long MaxSidecarBytes = 64 * 1024;

    /// <inheritdoc />
    public InstallSidecarReadResult TryRead(string binaryDir)
    {
        if (string.IsNullOrEmpty(binaryDir))
        {
            _logger.LogDebug("Install sidecar read skipped because the binary directory is empty.");
            return new InstallSidecarReadResult.NotFound(string.Empty);
        }

        var sidecarPath = Path.Combine(binaryDir, SidecarFileName);
        if (!File.Exists(sidecarPath))
        {
            _logger.LogDebug("Install sidecar file '{SidecarPath}' does not exist.", sidecarPath);
            return new InstallSidecarReadResult.NotFound(sidecarPath);
        }

        if (TryGetOversizedSidecarReason(sidecarPath, out var oversizedReason))
        {
            _logger.LogDebug("Install sidecar file '{SidecarPath}' rejected: {Reason}.", sidecarPath, oversizedReason);
            return new InstallSidecarReadResult.Invalid(sidecarPath, oversizedReason);
        }

        if (!TryReadSidecarFields(sidecarPath, out var fields, out var error))
        {
            if (error is JsonException)
            {
                _logger.LogDebug(error, "Install sidecar file '{SidecarPath}' contains malformed JSON.", sidecarPath);
            }
            else
            {
                _logger.LogDebug(error, "Install sidecar file '{SidecarPath}' could not be read due to a path, permission, or IO error.", sidecarPath);
            }

            return new InstallSidecarReadResult.Invalid(sidecarPath, error?.Message ?? "Install sidecar file could not be read.");
        }

        var effectiveRawSource = fields.Source ?? string.Empty;
        var parsed = InstallSourceExtensions.ParseInstallSource(effectiveRawSource);
        return new InstallSidecarReadResult.Ok(new InstallSidecarInfo(
            sidecarPath,
            parsed,
            effectiveRawSource,
            Channel: fields.Channel,
            Version: fields.Version,
            Commit: fields.Commit,
            NuGetServiceIndexOverride: fields.NuGetServiceIndexOverride,
            Packages: fields.Packages));
    }

    /// <summary>
    /// Reads the <c>source</c> field from a sidecar file at a known path.
    /// Static helper used by <c>BundleService.ComputeDefaultExtractDir</c>,
    /// which runs before DI is wired and cannot take a service dependency.
    /// Returns <see langword="null"/> when the file is missing, unreadable,
    /// or contains malformed / unexpected JSON.
    /// </summary>
    internal static string? ReadSourceField(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        if (TryGetOversizedSidecarReason(sidecarPath, out _))
        {
            return null;
        }

        return TryReadSidecarFields(sidecarPath, out var fields, out _)
            ? fields.Source
            : null;
    }

    private static bool TryGetOversizedSidecarReason(string sidecarPath, out string reason)
    {
        try
        {
            var length = new FileInfo(sidecarPath).Length;
            if (length > MaxSidecarBytes)
            {
                reason = $"Sidecar file size {length} bytes exceeds the {MaxSidecarBytes}-byte limit.";
                return true;
            }
        }
        catch (Exception ex) when (IsSidecarReadException(ex))
        {
            // FileInfo can throw the same IO/permission/path family as the read path.
            // Let the regular reader produce the diagnostic in that case so we don't
            // duplicate error reporting here.
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Parsed identity / override fields read from an on-disk sidecar.
    /// All fields are nullable because the sidecar schema treats them as
    /// optional — older sidecars and routes that do not yet write identity
    /// keep emitting the <c>source</c>-only shape and the missing fields fall
    /// back to the resolver's terminal/assembly defaults. Non-string JSON
    /// values for any of these fields silently resolve to <see langword="null"/>
    /// here so a malformed identity field does not poison the whole sidecar read.
    /// </summary>
    private readonly record struct SidecarFields(
        string? Source,
        string? Channel,
        string? Version,
        string? Commit,
        string? NuGetServiceIndexOverride,
        string? Packages);

    private static SidecarFields ReadSidecarFieldsFromExistingSidecar(string sidecarPath)
    {
        using var stream = File.OpenRead(sidecarPath);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new SidecarFields(
            Source: ReadOptionalString(doc.RootElement, "source"),
            Channel: ReadOptionalString(doc.RootElement, "channel"),
            Version: ReadOptionalString(doc.RootElement, "version"),
            Commit: ReadOptionalString(doc.RootElement, "commit"),
            NuGetServiceIndexOverride: ReadOptionalString(doc.RootElement, "nugetServiceIndexOverride"),
            Packages: ReadOptionalString(doc.RootElement, "packages"));
    }

    private static string? ReadOptionalString(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        return null;
    }

    private static bool TryReadSidecarFields(string sidecarPath, out SidecarFields fields, out Exception? exception)
    {
        fields = default;
        exception = null;

        try
        {
            fields = ReadSidecarFieldsFromExistingSidecar(sidecarPath);
            return true;
        }
        catch (Exception ex) when (IsSidecarReadException(ex))
        {
            exception = ex;
            return false;
        }
    }

    private static bool IsSidecarReadException(Exception ex)
        => ex is JsonException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException;
}
