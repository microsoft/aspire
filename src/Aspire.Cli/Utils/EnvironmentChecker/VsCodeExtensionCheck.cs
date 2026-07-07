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

    private readonly IEnvironment _environment;
    private readonly CliExecutionContext _executionContext;
    private readonly Func<string, string?> _commandResolver;

    public VsCodeExtensionCheck(IEnvironment environment, CliExecutionContext executionContext)
        : this(environment, executionContext, PathLookupHelper.FindFullPathFromPath)
    {
    }

    // Defaults commandResolver to the real PATH lookup; the internal constructor lets tests inject a
    // deterministic resolver (see the Detect overload below for why the resolver is a seam).
    internal VsCodeExtensionCheck(IEnvironment environment, CliExecutionContext executionContext, Func<string, string?> commandResolver)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(commandResolver);

        _environment = environment;
        _executionContext = executionContext;
        _commandResolver = commandResolver;
    }

    // Runs after the fast environment/OS checks; this is a cheap filesystem probe with no process spawn.
    public int Order => 60;

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detection = Detect(_environment, _executionContext.HomeDirectory, _commandResolver);

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
        => Detect(environment, homeDirectory, PathLookupHelper.FindFullPathFromPath);

    // The command resolver is injected so tests can exercise the PATH-based detection fallback
    // deterministically; PathLookupHelper.FindFullPathFromPath reads the real process PATH, which
    // cannot be mocked via IEnvironment and would otherwise leave that branch untested (and flaky
    // on machines that happen to have "code" on PATH).
    internal static VsCodeExtensionDetection Detect(IEnvironment environment, DirectoryInfo homeDirectory, Func<string, string?> commandResolver)
    {
        var vsCodeInstalled = IsVsCodeInstalled(environment, commandResolver);
        if (!vsCodeInstalled)
        {
            return new VsCodeExtensionDetection(VsCodeInstalled: false, ExtensionInstalled: false);
        }

        var extensionInstalled = IsExtensionInstalled(environment, homeDirectory);
        return new VsCodeExtensionDetection(VsCodeInstalled: true, ExtensionInstalled: extensionInstalled);
    }

    private static bool IsVsCodeInstalled(IEnvironment environment, Func<string, string?> commandResolver)
    {
        // When doctor is invoked from an integrated terminal, VS Code advertises itself via TERM_PROGRAM.
        // See https://code.visualstudio.com/docs/terminal/shell-integration.
        if (string.Equals(environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Otherwise fall back to probing for the CLI launchers on PATH (stable and Insiders).
        return commandResolver("code") is not null
            || commandResolver("code-insiders") is not null;
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
        // VSCODE_EXTENSIONS overrides the default extensions location entirely: when it is set,
        // VS Code loads extensions only from that directory, so we must probe only it. Falling through
        // to the default roots here could report the Aspire extension as installed from ~/.vscode even
        // though the running VS Code instance (using the override) would not load it — a false "pass".
        var overrideDirectory = environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            yield return overrideDirectory;
            yield break;
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
            // IgnoreInaccessible lets the probe skip an unreadable extension folder and keep scanning
            // the rest, instead of throwing and reporting the whole extensions root as "not found" (a
            // false warning even when the Aspire extension is installed alongside an inaccessible one).
            // The parameterless EnumerateDirectories overload uses legacy behavior that throws instead.
            // AttributesToSkip is reset to None (the default EnumerationOptions skips Hidden/System) so an
            // extension folder is never silently ignored because of an unexpected attribute.
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None
            };

            // Installed extensions live in per-version folders named "<publisher>.<name>-<version>",
            // lowercased by VS Code, for example "microsoft-aspire.aspire-vscode-1.2.3".
            foreach (var directory in Directory.EnumerateDirectories(extensionsDirectory, "*", enumerationOptions))
            {
                if (IsVersionedExtensionFolder(Path.GetFileName(directory)))
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

    // Matches an extension folder name against the Aspire extension id. A case-insensitive prefix match
    // tolerates any installed version without spawning the VS Code CLI. Requiring a digit immediately
    // after the trailing '-' pins the match to the version segment so a different extension whose id
    // starts with ours (e.g. "microsoft-aspire.aspire-vscode-extras-1.0.0") is not treated as a match.
    private static bool IsVersionedExtensionFolder(string folderName)
    {
        const string prefix = ExtensionId + "-";
        return folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && folderName.Length > prefix.Length
            && char.IsAsciiDigit(folderName[prefix.Length]);
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
