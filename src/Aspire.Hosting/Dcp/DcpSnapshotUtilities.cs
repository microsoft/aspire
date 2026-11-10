// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Builds app-model snapshot values from launch metadata and environment values shared by DCP versions.
/// </summary>
internal static class DcpSnapshotUtilities
{
    internal static (ImmutableArray<string> Args, ImmutableArray<int>? ArgsAreSensitive, bool IsSensitive)? GetLaunchArguments(
        IReadOnlyList<AppLaunchArgumentAnnotation>? arguments,
        IReadOnlyList<string>? effectiveArgs)
    {
        if (arguments is null)
        {
            return null;
        }

        var launchArgsBuilder = ImmutableArray.CreateBuilder<string>();
        var argsAreSensitiveBuilder = ImmutableArray.CreateBuilder<int>();
        var anySensitive = false;
        foreach (var argument in arguments)
        {
            anySensitive |= argument.IsSensitive;
            launchArgsBuilder.Add(
                argument.EffectiveArgumentIndex is int index &&
                effectiveArgs is not null &&
                (uint)index < (uint)effectiveArgs.Count
                    ? effectiveArgs[index]
                    : argument.Argument);
            argsAreSensitiveBuilder.Add(Convert.ToInt32(argument.IsSensitive));
        }

        return (launchArgsBuilder.ToImmutable(), argsAreSensitiveBuilder.ToImmutable(), anySensitive);
    }

    internal static ImmutableArray<EnvironmentVariableSnapshot> GetEnvironmentVariables(List<EnvVar>? effectiveSource, List<EnvVar>? specSource)
    {
        if (effectiveSource is null or { Count: 0 })
        {
            return [];
        }

        var environment = ImmutableArray.CreateBuilder<EnvironmentVariableSnapshot>(effectiveSource.Count);
        foreach (var env in effectiveSource)
        {
            if (env.Name is not null)
            {
                var isFromSpec = specSource?.Any(e => string.Equals(e.Name, env.Name, StringComparison.Ordinal)) is true or null;
                environment.Add(new(env.Name, env.Value ?? "", isFromSpec));
            }
        }

        environment.Sort((v1, v2) => string.Compare(v1.Name, v2.Name, StringComparison.Ordinal));
        return environment.ToImmutable();
    }
}
