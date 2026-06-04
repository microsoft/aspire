// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Aspire.Shared;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Pure transform from a <see cref="DogfoodSessionConfig"/> to the set of
/// environment-variable overrides the embedded shell should apply. The
/// SessionTerminalPanel consumes this plan and types the
/// equivalent shell commands into the spawned shell via <c>Hex1bTerminalAutomator</c>
/// rather than mutating <c>Hex1bTerminalProcessOptions.Environment</c> behind
/// the user's back — making the dogfooding setup auditable in the terminal
/// scrollback.
/// </summary>
internal interface IDogfoodSessionPreparer
{
    SessionEnvironmentPlan BuildPlan(DogfoodSessionConfig config);
}

/// <param name="IdentityOverrides">
/// Ordered list of <c>(name, value)</c> pairs for the <c>ASPIRE_CLI_*</c>
/// identity overrides. Ordered (not a dictionary) so the commands appear in
/// a stable, predictable sequence when typed into the shell.
/// </param>
/// <param name="PathPrependDir">
/// Directory to prepend to <c>PATH</c> (typically the locally-built
/// <c>artifacts/bin/Aspire.Cli/...</c>), or null when no local CLI was found.
/// </param>
internal sealed record SessionEnvironmentPlan(
    IReadOnlyList<KeyValuePair<string, string>> IdentityOverrides,
    string? PathPrependDir);

internal sealed class DogfoodSessionPreparer : IDogfoodSessionPreparer
{
    public DogfoodSessionPreparer(ILocalAspireCliLocator cliLocator)
    {
        _cliLocator = cliLocator;
    }

    private readonly ILocalAspireCliLocator _cliLocator;

    public SessionEnvironmentPlan BuildPlan(DogfoodSessionConfig config)
    {
        var overrides = new List<KeyValuePair<string, string>>();

        // Channel is a closed enum on our side but a free-form string on the
        // CLI's side (the CLI accepts anything because PRs are dynamic). Emit
        // the lowercased name except for Pr which carries its number.
        var channelValue = config.Channel switch
        {
            ChannelKind.Stable => "stable",
            ChannelKind.Staging => "staging",
            ChannelKind.Daily => "daily",
            ChannelKind.Local => "local",
            ChannelKind.Pr when config.PrNumber is int n => $"pr-{n}",
            // Pr channel with no PrNumber is a UI-state bug; emit a placeholder
            // so the misconfiguration is visible in the launched terminal
            // rather than silently degrading to whatever the CLI's default is.
            ChannelKind.Pr => "pr-MISSING",
            _ => "local",
        };
        overrides.Add(new(AspireCliIdentityEnvVars.Channel, channelValue));

        if (!string.IsNullOrWhiteSpace(config.VersionOverride))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.Version, config.VersionOverride));
        }

        if (!string.IsNullOrWhiteSpace(config.CommitOverride))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.Commit, config.CommitOverride));
        }

        if (!string.IsNullOrWhiteSpace(config.NuGetServiceIndexOverride))
        {
            overrides.Add(new(AspireCliIdentityEnvVars.NuGetServiceIndex, config.NuGetServiceIndexOverride));
        }

        return new SessionEnvironmentPlan(overrides, _cliLocator.CliDirectory);
    }
}
