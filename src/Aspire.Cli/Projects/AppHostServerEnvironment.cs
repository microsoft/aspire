// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal static class AppHostServerEnvironment
{
    internal const string LaunchGroupVariableName = "Aspire__LaunchGroup";

    internal static void Apply(
        IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (key, value) in overrides)
        {
            if (key.Equals(LaunchGroupVariableName, StringComparison.OrdinalIgnoreCase))
            {
                // Unix permits ASPIRE__LAUNCHGROUP and Aspire__LaunchGroup to coexist, but .NET
                // configuration folds them into one key. Remove inherited spellings from the
                // actual child environment before setting the profile or CLI selection.
                foreach (var inheritedKey in environment.Keys
                    .Where(name => name.Equals(LaunchGroupVariableName, StringComparison.OrdinalIgnoreCase))
                    .ToArray())
                {
                    environment.Remove(inheritedKey);
                }
            }

            environment[key] = value;
        }
    }
}
