// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Captures the result of each environment probe run on app startup. The
/// validation screen reads this; if any required probe is not
/// <see cref="EnvironmentProbeStatus.Ok"/>, the user cannot enter the main
/// screen until they remediate and retry.
/// </summary>
internal sealed class EnvironmentValidationState
{
    public EnvironmentValidationState(ChangeNotifier notifier)
    {
        _notifier = notifier;
    }

    private readonly ChangeNotifier _notifier;

    public EnvironmentProbeResult DotnetProbe { get; private set; } = EnvironmentProbeResult.Pending("dotnet");
    public EnvironmentProbeResult GhAuthProbe { get; private set; } = EnvironmentProbeResult.Pending("gh auth");
    public EnvironmentProbeResult GhTokenProbe { get; private set; } = EnvironmentProbeResult.Pending("gh token");

    /// <summary>
    /// The GitHub token captured from <c>gh auth token</c> during the GH-token
    /// probe. Cached in-process only — never persisted to disk — so the future
    /// PR-catalog GitHub API client can authenticate without prompting the user
    /// again. Null until <see cref="GhTokenProbe"/> succeeds.
    /// </summary>
    public string? GitHubToken { get; private set; }

    public bool AllProbesOk =>
        DotnetProbe.Status == EnvironmentProbeStatus.Ok &&
        GhAuthProbe.Status == EnvironmentProbeStatus.Ok &&
        GhTokenProbe.Status == EnvironmentProbeStatus.Ok;

    public void UpdateDotnet(EnvironmentProbeResult result)
    {
        DotnetProbe = result;
        _notifier.Notify();
    }

    public void UpdateGhAuth(EnvironmentProbeResult result)
    {
        GhAuthProbe = result;
        _notifier.Notify();
    }

    public void UpdateGhToken(EnvironmentProbeResult result, string? token)
    {
        GhTokenProbe = result;
        GitHubToken = token;
        _notifier.Notify();
    }

    public void Reset()
    {
        DotnetProbe = EnvironmentProbeResult.Pending("dotnet");
        GhAuthProbe = EnvironmentProbeResult.Pending("gh auth");
        GhTokenProbe = EnvironmentProbeResult.Pending("gh token");
        GitHubToken = null;
        _notifier.Notify();
    }
}

internal enum EnvironmentProbeStatus
{
    Pending,
    Running,
    Ok,
    Failed,
}

/// <summary>
/// One probe's outcome. <see cref="Detail"/> is shown in the validation UI on
/// success (e.g. the discovered <c>dotnet</c> version) and as remediation
/// guidance on failure (e.g. "Run <c>gh auth login</c>").
/// </summary>
internal sealed record EnvironmentProbeResult(
    string Name,
    EnvironmentProbeStatus Status,
    string Detail)
{
    public static EnvironmentProbeResult Pending(string name) =>
        new(name, EnvironmentProbeStatus.Pending, "Not yet run.");

    public static EnvironmentProbeResult Running(string name) =>
        new(name, EnvironmentProbeStatus.Running, "Running…");

    public static EnvironmentProbeResult Ok(string name, string detail) =>
        new(name, EnvironmentProbeStatus.Ok, detail);

    public static EnvironmentProbeResult Failed(string name, string detail) =>
        new(name, EnvironmentProbeStatus.Failed, detail);
}
