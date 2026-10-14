// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

internal static class EnvironmentVariableExtensions
{
    internal static bool IsFlagEnabled(this IEnvironment environment, string name)
    {
        var value = environment.GetEnvironmentVariable(name);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
