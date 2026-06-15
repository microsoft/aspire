// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Validates that the installed Helm CLI is new enough for the flags and behaviors
/// Aspire's Kubernetes deployment pipeline depends on.
/// </summary>
/// <remarks>
/// Aspire authors `helm upgrade --install` invocations against Helm 4.x. In particular
/// the `--server-side=true --force-conflicts` combination emitted by
/// <c>WithForceConflicts()</c> matches the Helm 4 form of <c>--server-side</c> (string
/// valued: <c>"true" | "false" | "auto"</c>, https://github.com/helm/helm/pull/13649).
/// Validating up-front turns errors like <c>unknown flag: --force-conflicts</c> or
/// <c>Flag --force has been deprecated, use --force-replace instead</c> into a single
/// clear actionable message.
/// </remarks>
internal static partial class HelmVersionValidator
{
    /// <summary>
    /// Minimum supported Helm version. See class remarks for rationale.
    /// </summary>
    public static readonly Version MinimumHelmVersion = new(4, 2, 0);

    private const string InstallDocsUrl = "https://helm.sh/docs/intro/install/";

    // `helm version --short` returns a single line in the shape
    //   v4.2.0+gfa15ec0
    //   v4.0.0
    //   v3.18.0+gb88f836
    // Match the optional `v` followed by MAJOR.MINOR.PATCH; ignore any `+gitsha`
    // build metadata. Intentionally unanchored: some shells / wrappers (oh-my-zsh
    // plugins, asdf shims, alias output, etc.) can prefix banner or shim lines
    // before the actual version token, so we accept the first valid token anywhere
    // in the captured output rather than requiring it at column 0.
    [GeneratedRegex(@"v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)")]
    private static partial Regex HelmVersionRegex();

    /// <summary>
    /// Runs <c>helm version --short</c>, parses the SemVer, and throws
    /// <see cref="InvalidOperationException"/> if the installed version is older than
    /// <see cref="MinimumHelmVersion"/> or if the output cannot be parsed.
    /// </summary>
    /// <remarks>
    /// We deliberately do not pass <c>--client</c>. That flag existed in Helm 2 (where
    /// Tiller meant there was a separate server version), was kept as a no-op in
    /// Helm 3, and was removed entirely in Helm 4 — which is our minimum. Passing
    /// <c>--client</c> against Helm 4 fails with <c>Error: unknown flag: --client</c>,
    /// which is exactly the cryptic failure mode this validator exists to prevent.
    /// </remarks>
    public static async Task EnsureMinimumVersionAsync(
        IHelmRunner helmRunner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(helmRunner);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        int exitCode;
        try
        {
            exitCode = await helmRunner.RunAsync(
                "version --short",
                onOutputData: line => stdout.AppendLine(line),
                onErrorData: line => stderr.AppendLine(line),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ProcessUtil throws when the process itself can't be spawned (helm not on
            // PATH, permission denied, etc.). The exception message from the runner
            // is typically a low-level "No such file or directory" or
            // "permission denied" that doesn't tell users what to do, so wrap it.
            throw new InvalidOperationException(
                $"Helm CLI not found or could not be invoked. Aspire requires Helm {MinimumHelmVersion} or later. Install it from {InstallDocsUrl} and ensure it is available on your PATH.",
                ex);
        }

        if (exitCode != 0)
        {
            var errorText = stderr.ToString().Trim();
            var detail = string.IsNullOrEmpty(errorText) ? $"exit code {exitCode}" : errorText;
            throw new InvalidOperationException(
                $"'helm version --short' failed ({detail}). Aspire requires Helm {MinimumHelmVersion} or later. See {InstallDocsUrl}.");
        }

        var rawOutput = stdout.ToString().Trim();
        if (!TryParseHelmVersion(rawOutput, out var detected))
        {
            throw new InvalidOperationException(
                $"Could not parse Helm version from 'helm version --short' output: '{rawOutput}'. Aspire requires Helm {MinimumHelmVersion} or later. See {InstallDocsUrl}.");
        }

        if (detected < MinimumHelmVersion)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Helm {0} was detected, but Aspire requires Helm {1} or later to deploy Kubernetes resources. Upgrade Helm from {2}.",
                    detected,
                    MinimumHelmVersion,
                    InstallDocsUrl));
        }
    }

    /// <summary>
    /// Extracts the first <c>MAJOR.MINOR.PATCH</c> token from the given Helm version
    /// output. Returns <see langword="false"/> if no version token is present.
    /// </summary>
    internal static bool TryParseHelmVersion(string output, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = HelmVersionRegex().Match(output);
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups["major"].ValueSpan, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].ValueSpan, CultureInfo.InvariantCulture);
        var patch = int.Parse(match.Groups["patch"].ValueSpan, CultureInfo.InvariantCulture);
        version = new Version(major, minor, patch);
        return true;
    }
}
