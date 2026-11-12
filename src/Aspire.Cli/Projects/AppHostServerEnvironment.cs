// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal static class AppHostServerEnvironment
{
    internal const string LaunchGroupVariableName = "Aspire__LaunchGroup";

    internal static bool IsLaunchGroupVariable(string name) =>
        name.Equals(LaunchGroupVariableName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Aspire:LaunchGroup", StringComparison.OrdinalIgnoreCase);

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
            if (IsLaunchGroupVariable(key))
            {
                // Environment names can differ by case or use ':' instead of '__', but .NET
                // configuration folds them into one key. Remove inherited spellings before
                // setting the profile or CLI selection.
                foreach (var inheritedKey in environment.Keys
                    .Where(IsLaunchGroupVariable)
                    .ToArray())
                {
                    environment.Remove(inheritedKey);
                }
            }

            environment[key] = value;
        }
    }
}
