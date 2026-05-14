// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IInstallationUninstaller"/>. Reads the sidecar at
/// <c>&lt;prefix&gt;/bin/.aspire-install.json</c> to determine the install
/// route, then dispatches per-route removal logic:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description><c>script</c> route — SURGICAL: only deletes the
///   Aspire-owned files inside the shared prefix
///   (<c>bin/aspire[.exe]</c>, the sidecar, the bundle marker, and the
///   single version dir matching the bundle marker). Sibling routes such
///   as <c>dogfood/</c> and <c>hives/</c> are untouched.</description></item>
///   <item><description><c>pr</c> route — the entire prefix is owned by
///   the PR install, so the full prefix plus the matching hive directory
///   are removed. Hive default is <c>~/.aspire/hives/pr-&lt;N&gt;/</c>;
///   the planner only resolves the hive when the prefix follows the
///   default dogfood layout.</description></item>
///   <item><description><c>winget</c> / <c>brew</c> / <c>dotnet-tool</c>
///   — refused. We do not invoke the packager; printing the right
///   packager command is the value-add.</description></item>
///   <item><description><c>Unknown</c> / no sidecar — refused with a
///   manual-removal hint.</description></item>
/// </list>
/// </remarks>
internal sealed class InstallationUninstaller : IInstallationUninstaller
{
    private readonly IInstallSidecarReader _sidecarReader;
    private readonly ILogger<InstallationUninstaller> _logger;

    public InstallationUninstaller(IInstallSidecarReader sidecarReader, ILogger<InstallationUninstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(sidecarReader);
        ArgumentNullException.ThrowIfNull(logger);
        _sidecarReader = sidecarReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public UninstallPlan Plan(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var fullPrefix = Path.GetFullPath(prefix);

        if (!Directory.Exists(fullPrefix))
        {
            return new UninstallPlan(
                fullPrefix,
                InstallSource.Unknown,
                UninstallOutcome.NothingToDo,
                Removals: [],
                ManualSteps: [],
                RefusalReason: $"Install prefix does not exist: {fullPrefix}");
        }

        var binDir = Path.Combine(fullPrefix, "bin");
        var sidecar = _sidecarReader.TryRead(binDir);

        if (sidecar is null)
        {
            return new UninstallPlan(
                fullPrefix,
                InstallSource.Unknown,
                UninstallOutcome.RefusedUnknownRoute,
                Removals: [],
                ManualSteps:
                [
                    $"No install-route sidecar found at {Path.Combine(binDir, InstallSidecarReader.SidecarFileName)}.",
                    "If you want to remove this directory anyway, do it manually:",
                    $"  rm -rf {fullPrefix}",
                ],
                RefusalReason: "No .aspire-install.json sidecar found.");
        }

        if (IsRunningFromPrefix(fullPrefix))
        {
            // Symlink-aware self-protect. Mirrors BundleService.ResolveSymlinks
            // so that homebrew/bin-link style installs cannot fool us.
            return new UninstallPlan(
                fullPrefix,
                sidecar.Source,
                UninstallOutcome.RefusedSelf,
                Removals: [],
                ManualSteps:
                [
                    "Refusing to uninstall the currently running CLI from itself.",
                    "Use a different aspire binary (or your shell directly) to remove this install:",
                    $"  rm -rf {fullPrefix}",
                ],
                RefusalReason: "Target prefix contains the currently running CLI.");
        }

        return sidecar.Source switch
        {
            InstallSource.Pr => PlanPrRouteRemoval(fullPrefix),
            // LocalHive shares the same on-disk layout as Script (shared
            // prefix with sibling routes), so the surgical-removal logic
            // is identical: only Aspire-owned files inside the prefix get
            // removed, never the sibling subtrees.
            InstallSource.Script or InstallSource.LocalHive => PlanScriptRouteRemoval(fullPrefix, binDir, sidecar.Source),
            InstallSource.Winget => PlanPackagerRefusal(fullPrefix, sidecar.Source, "winget uninstall Microsoft.Aspire"),
            InstallSource.Brew => PlanPackagerRefusal(fullPrefix, sidecar.Source, "brew uninstall --cask aspire"),
            InstallSource.DotnetTool => PlanPackagerRefusal(fullPrefix, sidecar.Source, "dotnet tool uninstall -g Aspire.Cli"),
            _ => new UninstallPlan(
                fullPrefix,
                sidecar.Source,
                UninstallOutcome.RefusedUnknownRoute,
                Removals: [],
                ManualSteps:
                [
                    $"Sidecar reports unknown install route '{sidecar.RawSource ?? "(empty)"}'. Refusing to act.",
                    "If you want to remove this directory anyway, do it manually:",
                    $"  rm -rf {fullPrefix}",
                ],
                RefusalReason: $"Unknown install route '{sidecar.RawSource ?? "(empty)"}'."),
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(UninstallPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Outcome != UninstallOutcome.Proceed)
        {
            throw new InvalidOperationException($"Cannot execute a plan with outcome '{plan.Outcome}'.");
        }

        foreach (var target in plan.Removals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // Best-effort cleanup of the now-empty parent dirs the plan left
        // behind for script-route uninstalls. We deliberately only remove
        // <prefix>/versions/ and <prefix>/bin/ if they're empty — never the
        // shared prefix itself.
        TryRemoveIfEmpty(Path.Combine(plan.Prefix, "versions"));
        TryRemoveIfEmpty(Path.Combine(plan.Prefix, "bin"));
    }

    /// <summary>
    /// PR-route plan: the whole <c>~/.aspire/dogfood/pr-N/</c> tree is
    /// dedicated to this install, so it's safe to remove wholesale. The
    /// matching hive at <c>~/.aspire/hives/pr-N/</c> is added when the
    /// prefix follows the default dogfood layout; custom prefixes get a
    /// manual-step hint instead because we can't infer the hive label.
    /// </summary>
    private static UninstallPlan PlanPrRouteRemoval(string fullPrefix)
    {
        var removals = new List<string> { fullPrefix };
        var manualSteps = new List<string>();

        // Layout match: <root>/dogfood/pr-<N> → hive at <root>/hives/pr-<N>.
        // <root> is typically ~/.aspire but we don't hardcode that — we
        // derive it from the prefix so custom --install-path installs work
        // too as long as they used the same dogfood layout.
        var (rootDir, hiveLabel) = TryResolveDogfoodLayout(fullPrefix);
        if (rootDir is not null && hiveLabel is not null)
        {
            var hivePath = Path.Combine(rootDir, "hives", hiveLabel);
            if (Directory.Exists(hivePath))
            {
                removals.Add(hivePath);
            }
        }
        else
        {
            manualSteps.Add(
                "This PR install does not follow the default `~/.aspire/dogfood/pr-<N>/` layout, " +
                "so the associated NuGet hive cannot be resolved automatically. Remove it manually if present.");
        }

        AppendCommonManualSteps(manualSteps, isPrRoute: true);

        return new UninstallPlan(
            fullPrefix,
            InstallSource.Pr,
            UninstallOutcome.Proceed,
            removals,
            manualSteps,
            RefusalReason: null);
    }

    /// <summary>
    /// Script-route plan (also used for the localhive route, which shares
    /// the same shared-prefix layout): the prefix is shared with sibling
    /// routes (<c>dogfood/</c>, <c>hives/</c>, etc.) so the removal must
    /// be surgical. Only Aspire-owned files inside
    /// <c>&lt;prefix&gt;/bin/</c> and the single <c>versions/&lt;id&gt;/</c>
    /// directory matching the bundle marker are removed. If the bundle
    /// marker is missing or unreadable we refuse to touch <c>versions/</c>
    /// rather than guessing which directory belongs to this install.
    /// </summary>
    private static UninstallPlan PlanScriptRouteRemoval(string fullPrefix, string binDir, InstallSource route)
    {
        var removals = new List<string>();
        var manualSteps = new List<string>();

        var aspireBinary = OperatingSystem.IsWindows()
            ? Path.Combine(binDir, "aspire.exe")
            : Path.Combine(binDir, "aspire");
        if (File.Exists(aspireBinary))
        {
            removals.Add(aspireBinary);
        }
        var sidecarPath = Path.Combine(binDir, InstallSidecarReader.SidecarFileName);
        if (File.Exists(sidecarPath))
        {
            removals.Add(sidecarPath);
        }

        // Version-id-specific bundle removal (U-2). Read the marker the
        // bundle service wrote at extract time; that's the only authoritative
        // source for which <id> directory belongs to *this* install. If the
        // marker is gone we leave versions/ alone — the user's other
        // installs may own the remaining contents.
        var markerPath = Path.Combine(fullPrefix, BundleService.VersionMarkerFileName);
        if (removals.Count > 0)
        {
            removals.Add(markerPath); // even if missing, queue it; DeleteAsync no-ops
        }

        var currentVersion = BundleService.ReadVersionMarker(fullPrefix);
        if (!string.IsNullOrEmpty(currentVersion))
        {
            var versionId = BundleService.ComputeVersionId(currentVersion);
            var versionDir = Path.Combine(fullPrefix, BundleService.VersionsDirectoryName, versionId);
            if (Directory.Exists(versionDir))
            {
                removals.Add(versionDir);
            }
        }
        else
        {
            var versionsRoot = Path.Combine(fullPrefix, BundleService.VersionsDirectoryName);
            if (Directory.Exists(versionsRoot))
            {
                manualSteps.Add(
                    $"Bundle version marker missing at '{markerPath}'; cannot determine which `versions/<id>/` directory belongs to this install. " +
                    "Inspect and remove the matching directory manually if needed: " + versionsRoot);
            }
        }

        AppendCommonManualSteps(manualSteps, isPrRoute: false);

        if (removals.Count == 0)
        {
            return new UninstallPlan(
                fullPrefix,
                route,
                UninstallOutcome.NothingToDo,
                Removals: [],
                ManualSteps: manualSteps,
                RefusalReason: $"No Aspire-owned files found under {fullPrefix}.");
        }

        return new UninstallPlan(
            fullPrefix,
            route,
            UninstallOutcome.Proceed,
            removals,
            manualSteps,
            RefusalReason: null);
    }

    private static UninstallPlan PlanPackagerRefusal(string fullPrefix, InstallSource route, string command)
    {
        return new UninstallPlan(
            fullPrefix,
            route,
            UninstallOutcome.RefusedPackagerOwned,
            Removals: [],
            ManualSteps:
            [
                $"This install was placed by {route.ToWireString()} and must be removed via the packager:",
                $"  {command}",
            ],
            RefusalReason: $"{route.ToWireString()}-managed install must be removed via the packager command.");
    }

    /// <summary>
    /// Adds the always-present manual-cleanup notes for proceed plans.
    /// PATH edits in shell profiles and (for PR route) the VS Code
    /// extension are NOT touched by uninstall by design; this is where we
    /// remind the user.
    /// </summary>
    private static void AppendCommonManualSteps(List<string> manualSteps, bool isPrRoute)
    {
        manualSteps.Add(
            "If your shell profile (e.g. ~/.bashrc, ~/.zshrc, ~/.profile, fish config) was updated by the install " +
            "script, remove any line whose preceding comment says `# Added by get-aspire-cli*.sh` and the `export PATH=` " +
            "or `fish_add_path` line below it pointing at the install's `bin/` directory.");

        if (OperatingSystem.IsWindows())
        {
            manualSteps.Add(
                "On Windows the install path may also be on User-scope PATH. Remove it via System Properties → " +
                "Environment Variables, or `setx PATH \"<new value>\"`.");
        }

        if (isPrRoute)
        {
            manualSteps.Add(
                "If the PR install script also installed the Aspire VS Code extension, uninstall it via VS Code " +
                "(Extensions panel) or `code --uninstall-extension ms-dotnettools.aspire-vscode`.");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <see cref="Environment.ProcessPath"/>
    /// — resolved through any symlinks — sits inside <paramref name="prefix"/>.
    /// Resolves both sides so a homebrew-style <c>bin/aspire</c> shim into
    /// a cask staged path is correctly identified as living inside the
    /// staged prefix.
    /// </summary>
    private static bool IsRunningFromPrefix(string prefix)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var canonicalProcess = ResolveSymlinkOrSelf(processPath);
        var canonicalPrefix = ResolveSymlinkOrSelf(prefix);

        if (string.IsNullOrEmpty(canonicalProcess) || string.IsNullOrEmpty(canonicalPrefix))
        {
            return false;
        }

        // Ensure trailing separator for the ancestor check so e.g.
        // "/home/u/.aspire-foo" doesn't match "/home/u/.aspire".
        var prefixWithSep = canonicalPrefix.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalPrefix
            : canonicalPrefix + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return canonicalProcess.StartsWith(prefixWithSep, comparison);
    }

    private static string? ResolveSymlinkOrSelf(string path)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Best-effort layout match for <c>&lt;root&gt;/dogfood/pr-&lt;N&gt;</c>
    /// to derive the matching hive path. Returns null components when the
    /// prefix doesn't match the dogfood layout (e.g. custom
    /// <c>--install-path</c> with a different shape).
    /// </summary>
    private static (string? Root, string? HiveLabel) TryResolveDogfoodLayout(string fullPrefix)
    {
        var trimmed = fullPrefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var labelName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(labelName) || !labelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var dogfoodDir = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(dogfoodDir))
        {
            return (null, null);
        }
        if (!string.Equals(Path.GetFileName(dogfoodDir), "dogfood", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var root = Path.GetDirectoryName(dogfoodDir);
        return (root, labelName);
    }

    private void TryRemoveIfEmpty(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove empty directory {Directory}.", directory);
        }
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (Directory.Exists(path))
            {
                // Synchronous Directory.Delete is fine here — uninstall is
                // not on a hot path and large bundle trees deserve to block
                // explicitly rather than spin up a Task.Run.
                Directory.Delete(path, recursive: true);
                _logger.LogDebug("Removed directory {Path}.", path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Removed file {Path}.", path);
            }
            // Else: nothing to do. Plan may legitimately include best-effort
            // paths (e.g. the bundle marker) that vanished between Plan()
            // and ExecuteAsync().
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tear-down is best effort beyond the first error. Bubble so
            // the command surfaces a partial-removal message.
            _logger.LogWarning(ex, "Failed to remove {Path}.", path);
            throw;
        }

        await Task.CompletedTask;
    }
}
