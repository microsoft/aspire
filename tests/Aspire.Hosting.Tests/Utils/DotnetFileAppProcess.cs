// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Utils;

#pragma warning disable ASPIREPROCESSCOMMAND001 // Process command APIs are experimental.

internal static class DotnetFileAppProcess
{
    public static string ExecutablePath { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    public static string ResolvedExecutablePath { get; } = PathLookupHelper.FindFullPathFromPath(ExecutablePath) ?? ExecutablePath;

    public static string WriteApp(TestTempDirectory directory, string fileName, string content)
    {
        var appPath = Path.Combine(directory.Path, fileName);
        File.WriteAllText(appPath, content);

        return appPath;
    }

    public static ProcessCommandSpec CreateProcessCommandSpec(
        string appPath,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool inheritEnvironmentVariables = true,
        string? standardInputContent = null)
    {
        return new ProcessCommandSpec(ExecutablePath)
        {
            Arguments = CreateArguments(appPath, arguments),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
            InheritEnvironmentVariables = inheritEnvironmentVariables,
            StandardInputContent = standardInputContent
        };
    }

    public static ProcessCommandExportOptions CreateProcessCommandExportOptions(string appPath, Action<ProcessCommandExportOptions>? configure = null)
        => CreateProcessCommandExportOptions(appPath, arguments: null, configure);

    public static ProcessCommandExportOptions CreateProcessCommandExportOptions(
        string appPath,
        IReadOnlyList<string>? arguments,
        Action<ProcessCommandExportOptions>? configure = null)
    {
        var options = new ProcessCommandExportOptions
        {
            ExecutablePath = ExecutablePath,
            Arguments = CreateArguments(appPath, arguments)
        };

        configure?.Invoke(options);

        return options;
    }

    public static ProcessSpec CreateDcpProcessSpec(
        string appPath,
        IReadOnlyList<string>? arguments = null,
        Action<string>? onOutputData = null,
        Action<string>? onErrorData = null,
        bool throwOnNonZeroReturnCode = false,
        int? retainedOutputLineCount = null)
    {
        return new ProcessSpec(ExecutablePath)
        {
            ArgumentList = CreateArguments(appPath, arguments),
            OnOutputData = onOutputData,
            OnErrorData = onErrorData,
            ThrowOnNonZeroReturnCode = throwOnNonZeroReturnCode,
            ResolveExecutablePath = true,
            RetainedOutputLineCount = retainedOutputLineCount
        };
    }

    public static string[] CreateArguments(string appPath, IReadOnlyList<string>? arguments = null)
    {
        return arguments is null or { Count: 0 }
            ? ["run", "--file", appPath]
            : ["run", "--file", appPath, "--", .. arguments];
    }
}

#pragma warning restore ASPIREPROCESSCOMMAND001
