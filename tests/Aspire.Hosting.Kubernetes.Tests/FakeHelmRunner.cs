// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes.Tests;

/// <summary>
/// In-memory fake of <see cref="IHelmRunner"/> for tests. Records arguments,
/// returns a configurable exit code, and emits canned stdout for
/// <c>helm version</c> probes so the prereq validator sees a valid response by
/// default.
/// </summary>
internal sealed class FakeHelmRunner : IHelmRunner
{
    public bool WasUninstallCalled { get; private set; }

    public bool WasVersionCalled { get; private set; }

    public bool ThrowOnVersion { get; set; }

    public string? LastArguments { get; private set; }

    public int ExitCode { get; set; }

    /// <summary>
    /// Output emitted to <c>onOutputData</c> when arguments start with
    /// <c>"version"</c>. Defaults to a recent stable Helm 4.x release so the
    /// prereq version validator passes.
    /// </summary>
    public string VersionOutput { get; set; } = "v4.2.0+gfa15ec0";

    /// <summary>
    /// Exit code returned specifically for <c>helm version</c> calls. Defaults to
    /// 0 so version probing succeeds regardless of <see cref="ExitCode"/>, which
    /// is used to model failures in the main command under test.
    /// </summary>
    public int VersionExitCode { get; set; }

    public Task<int> RunAsync(
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutputData = null,
        Action<string>? onErrorData = null,
        CancellationToken cancellationToken = default)
    {
        LastArguments = arguments;

        // Match any `helm version ...` probe (the validator passes
        // `version --short`).
        if (arguments.StartsWith("version", StringComparison.OrdinalIgnoreCase))
        {
            WasVersionCalled = true;

            if (ThrowOnVersion)
            {
                throw new InvalidOperationException("Helm version should not be probed.");
            }

            if (onOutputData is not null && !string.IsNullOrEmpty(VersionOutput))
            {
                onOutputData(VersionOutput);
            }

            return Task.FromResult(VersionExitCode);
        }

        if (arguments.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            WasUninstallCalled = true;
        }

        return Task.FromResult(ExitCode);
    }
}
