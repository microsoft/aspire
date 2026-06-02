// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Aspire.Shared;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Translates a <see cref="DogfoodSessionConfig"/> into the env-var
/// dictionary that the embedded terminal's child shell should inherit. In
/// Phase 1 this is a pure transform; Phase 2 will extend it to drive
/// post-launch automation via <c>Hex1bTerminalAutomator</c>.
/// </summary>
internal interface IDogfoodSessionPreparer
{
    IReadOnlyDictionary<string, string> BuildEnvironment(DogfoodSessionConfig config);
}

internal sealed class DogfoodSessionPreparer : IDogfoodSessionPreparer
{
    public IReadOnlyDictionary<string, string> BuildEnvironment(DogfoodSessionConfig config)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

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
        env[AspireCliIdentityEnvVars.Channel] = channelValue;

        if (!string.IsNullOrWhiteSpace(config.VersionOverride))
        {
            env[AspireCliIdentityEnvVars.Version] = config.VersionOverride;
        }

        if (!string.IsNullOrWhiteSpace(config.CommitOverride))
        {
            env[AspireCliIdentityEnvVars.Commit] = config.CommitOverride;
        }

        if (!string.IsNullOrWhiteSpace(config.NuGetServiceIndexOverride))
        {
            env[AspireCliIdentityEnvVars.NuGetServiceIndex] = config.NuGetServiceIndexOverride;
        }

        return env;
    }
}
