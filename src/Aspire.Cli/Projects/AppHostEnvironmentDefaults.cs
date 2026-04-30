// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Resolves the effective AppHost environment for CLI-launched processes.
/// </summary>
internal static class AppHostEnvironmentDefaults
{
    private const string EnvironmentArgumentName = "--environment";
    private const string EnvironmentArgumentAlias = "-e";
    private const string AspNetCoreEnvironmentVariableName = "ASPNETCORE_ENVIRONMENT";

    internal const string AspireEnvironmentVariableName = "ASPIRE_ENVIRONMENT";
    internal const string DotNetEnvironmentVariableName = "DOTNET_ENVIRONMENT";
    internal const string DevelopmentEnvironmentName = "Development";
    internal const string ProductionEnvironmentName = "Production";

    /// <summary>
    /// Determines whether the variable name should be treated as an environment-selection variable
    /// when filtering launch profile values.
    /// </summary>
    internal static bool IsEnvironmentVariableName(string variableName) =>
        variableName is DotNetEnvironmentVariableName or AspNetCoreEnvironmentVariableName or AspireEnvironmentVariableName;

    /// <summary>
    /// Applies the effective environment to the launch environment variables.
    /// </summary>
    /// <param name="environmentVariables">The environment variables passed to the launched process.</param>
    /// <param name="defaultEnvironment">The fallback environment used when no explicit environment is provided.</param>
    /// <param name="inheritedEnvironmentVariables">Optional inherited environment variables used by tests.</param>
    /// <param name="args">Optional command-line arguments that may contain <c>--environment</c>.</param>
    internal static void ApplyEffectiveEnvironment(
        IDictionary<string, string> environmentVariables,
        string? defaultEnvironment = null,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables = null,
        string[]? args = null)
    {
        if (TryResolveEnvironment(environmentVariables, inheritedEnvironmentVariables, args, out var environment))
        {
            environmentVariables[DotNetEnvironmentVariableName] = environment;
        }
        else if (defaultEnvironment is not null)
        {
            environmentVariables[DotNetEnvironmentVariableName] = defaultEnvironment;
        }
    }

    private static bool TryResolveEnvironment(
        IDictionary<string, string> environmentVariables,
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables,
        string[]? args,
        out string environment)
    {
        // Match DistributedApplicationBuilder precedence:
        // explicit --environment, then DOTNET_ENVIRONMENT, then ASPIRE_ENVIRONMENT.
        if (TryGetRequestedEnvironment(args, out environment) ||
            TryGetEnvironmentValue(environmentVariables, DotNetEnvironmentVariableName, out environment) ||
            TryGetInheritedEnvironmentValue(inheritedEnvironmentVariables, DotNetEnvironmentVariableName, out environment) ||
            TryGetEnvironmentValue(environmentVariables, AspireEnvironmentVariableName, out environment) ||
            TryGetInheritedEnvironmentValue(inheritedEnvironmentVariables, AspireEnvironmentVariableName, out environment))
        {
            return true;
        }

        environment = null!;
        return false;
    }

    private static bool TryGetRequestedEnvironment(string[]? args, out string environment)
    {
        if (args is not null)
        {
            // Walk from the end so the last --environment flag wins.
            for (var i = args.Length - 1; i >= 0; i--)
            {
                if (TryGetRequestedEnvironment(args, i, out environment))
                {
                    return true;
                }
            }
        }

        environment = null!;
        return false;
    }

    private static bool TryGetRequestedEnvironment(string[] args, int index, out string environment)
    {
        var argument = args[index];

        if (argument.StartsWith(EnvironmentArgumentName + "=", StringComparison.Ordinal))
        {
            return TryGetEnvironmentArgumentValue(argument[(EnvironmentArgumentName.Length + 1)..], out environment);
        }

        if (argument.StartsWith(EnvironmentArgumentAlias + "=", StringComparison.Ordinal))
        {
            return TryGetEnvironmentArgumentValue(argument[(EnvironmentArgumentAlias.Length + 1)..], out environment);
        }

        if (argument is EnvironmentArgumentName or EnvironmentArgumentAlias)
        {
            if (index + 1 < args.Length)
            {
                return TryGetEnvironmentArgumentValue(args[index + 1], out environment);
            }
        }

        environment = null!;
        return false;
    }

    private static bool TryGetEnvironmentArgumentValue(string value, out string environment)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment = value;
            return true;
        }

        environment = null!;
        return false;
    }

    private static bool TryGetEnvironmentValue(
        IDictionary<string, string> environmentVariables,
        string variableName,
        out string environment)
    {
        if (environmentVariables.TryGetValue(variableName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            environment = value;
            return true;
        }

        environment = null!;
        return false;
    }

    private static bool TryGetInheritedEnvironmentValue(
        IReadOnlyDictionary<string, string?>? inheritedEnvironmentVariables,
        string variableName,
        out string environment)
    {
        if (inheritedEnvironmentVariables is not null)
        {
            if (inheritedEnvironmentVariables.TryGetValue(variableName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                environment = value;
                return true;
            }
        }
        else
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment = value;
                return true;
            }
        }

        environment = null!;
        return false;
    }
}
