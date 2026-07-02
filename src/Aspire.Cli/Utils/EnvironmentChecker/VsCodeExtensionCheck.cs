// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Recommends installing the Aspire VS Code extension when VS Code is present but the extension is not.
/// </summary>
/// <remarks>
/// The check is intentionally silent when VS Code is not detected: there is nothing to recommend
/// outside of a VS Code environment, so it returns an empty result and no row is rendered.
/// </remarks>
internal sealed class VsCodeExtensionCheck : IEnvironmentCheck
{
    internal const string CheckName = "vscode-extension";

    /// <summary>
    /// The unique identifier of the Aspire VS Code extension (<c>&lt;publisher&gt;.&lt;name&gt;</c>).
    /// </summary>
    internal const string ExtensionId = "microsoft-aspire.aspire-vscode";

    /// <summary>
    /// The marketplace URL used as the fix link when the extension is missing.
    /// </summary>
    internal const string MarketplaceUrl = "https://marketplace.visualstudio.com/items?itemName=microsoft-aspire.aspire-vscode";

    private readonly Func<VsCodeExtensionDetection> _detect;

    public VsCodeExtensionCheck(IEnvironment environment, CliExecutionContext executionContext)
        : this(() => Detect(environment, executionContext.HomeDirectory))
    {
    }

    internal VsCodeExtensionCheck(Func<VsCodeExtensionDetection> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);

        _detect = detect;
    }

    // Runs after the fast environment/OS checks; this is a cheap filesystem probe with no process spawn.
    public int Order => 60;

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detection = _detect();

        // Nothing to recommend when the user is not running VS Code.
        if (!detection.VsCodeInstalled)
        {
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
        }

        var extensionInstalled = detection.ExtensionInstalled;
        var result = new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = extensionInstalled ? EnvironmentCheckStatus.Pass : EnvironmentCheckStatus.Warning,
            Message = extensionInstalled
                ? DoctorCommandStrings.VsCodeExtensionInstalledMessage
                : DoctorCommandStrings.VsCodeExtensionMissingMessage,
            Fix = extensionInstalled ? null : DoctorCommandStrings.VsCodeExtensionMissingFix,
            Link = extensionInstalled ? null : MarketplaceUrl,
            Metadata = BuildMetadata(detection)
        };

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([result]);
    }

    internal static VsCodeExtensionDetection Detect(IEnvironment environment, DirectoryInfo homeDirectory)
    {
        var vsCodeInstalled = IsVsCodeInstalled(environment);
        if (!vsCodeInstalled)
        {
            return new VsCodeExtensionDetection(VsCodeInstalled: false, ExtensionInstalled: false);
        }

        var extensionInstalled = IsExtensionInstalled(environment, homeDirectory);
        return new VsCodeExtensionDetection(VsCodeInstalled: true, ExtensionInstalled: extensionInstalled);
    }

    private static bool IsVsCodeInstalled(IEnvironment environment)
    {
        // When doctor is invoked from an integrated terminal, VS Code advertises itself via TERM_PROGRAM.
        // See https://code.visualstudio.com/docs/terminal/shell-integration.
        if (string.Equals(environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Otherwise fall back to probing for the CLI launchers on PATH (stable and Insiders).
        return PathLookupHelper.FindFullPathFromPath("code") is not null
            || PathLookupHelper.FindFullPathFromPath("code-insiders") is not null;
    }

    private static bool IsExtensionInstalled(IEnvironment environment, DirectoryInfo homeDirectory)
    {
        foreach (var extensionsDirectory in GetExtensionDirectories(environment, homeDirectory))
        {
            if (DirectoryContainsExtension(extensionsDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetExtensionDirectories(IEnvironment environment, DirectoryInfo homeDirectory)
    {
        // VSCODE_EXTENSIONS overrides the default extensions location when set.
        var overrideDirectory = environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            yield return overrideDirectory;
        }

        var home = homeDirectory.FullName;

        // Default extension roots for desktop (stable/Insiders) and remote/server installs.
        yield return Path.Combine(home, ".vscode", "extensions");
        yield return Path.Combine(home, ".vscode-insiders", "extensions");
        yield return Path.Combine(home, ".vscode-server", "extensions");
        yield return Path.Combine(home, ".vscode-server-insiders", "extensions");
    }

    private static bool DirectoryContainsExtension(string extensionsDirectory)
    {
        if (!Directory.Exists(extensionsDirectory))
        {
            return false;
        }

        try
        {
            // Installed extensions live in per-version folders named "<publisher>.<name>-<version>",
            // lowercased by VS Code, for example "microsoft-aspire.aspire-vscode-1.2.3". A case-insensitive
            // prefix match tolerates any installed version without spawning the VS Code CLI.
            foreach (var directory in Directory.EnumerateDirectories(extensionsDirectory))
            {
                var folderName = Path.GetFileName(directory);
                if (folderName.StartsWith(ExtensionId + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable extensions directory as "not found" rather than failing the whole doctor run.
            return false;
        }

        return false;
    }

    private static JsonObject BuildMetadata(VsCodeExtensionDetection detection)
        => new()
        {
            ["vsCodeInstalled"] = detection.VsCodeInstalled,
            ["extensionInstalled"] = detection.ExtensionInstalled,
            ["extensionId"] = ExtensionId
        };
}

/// <summary>
/// Captures whether VS Code and the Aspire VS Code extension were detected.
/// </summary>
internal sealed record VsCodeExtensionDetection(bool VsCodeInstalled, bool ExtensionInstalled);
