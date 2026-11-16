// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Utils;

internal static class DotnetFileAppProcess
{
    public static string ExecutablePath { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    public static string ResolvedExecutablePath { get; } = PathLookupHelper.FindFullPathFromPath(ExecutablePath) ?? ExecutablePath;

    public static string WriteApp(string directoryPath, string fileName, string content)
    {
        var appPath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(appPath, content);

        return appPath;
    }

    public static async Task<(string ExecutablePath, string[] Arguments)> CreateInputRejectingProcessAsync(
        string directoryPath, string exitCode, CancellationToken cancellationToken)
    {
        var appPath = WriteApp(directoryPath, "reject-input.cs", """
            #:sdk Microsoft.NET.Sdk
            using System.Runtime.InteropServices;
            using Microsoft.Win32.SafeHandles;

            // Console.OpenStandardInput does not own the native handle. Close the handle itself
            // so the parent's write fails while this process is still alive.
            if (OperatingSystem.IsWindows())
            {
                using var input = new SafeFileHandle(GetStdHandle(-10), ownsHandle: true);
            }

            if (args[0] == "wait")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }

            // Emit diagnostics after rejecting input, not just before closing the pipe.
            await Task.Delay(200);
            Console.WriteLine("stdout-after-input-closed");
            Console.Error.WriteLine("stderr-after-input-closed");
            return int.Parse(args[0]);

            [DllImport("kernel32.dll")]
            static extern IntPtr GetStdHandle(int nStdHandle);
            """);
        var outputDirectory = Path.Combine(directoryPath, "bin");
        var (pending, disposable) = ProcessUtil.Run(new ProcessSpec(ResolvedExecutablePath)
        {
            ArgumentList = ["build", appPath, "--output", outputDirectory, "-p:PublishAot=false", "-p:UseAppHost=false"]
        });
        await using (disposable)
        {
            await pending.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }

        var assemblyPath = Path.Combine(outputDirectory, "reject-input.dll");
        // Launch the DLL directly: dotnet run can retain its own copy of stdin. On Unix,
        // replace stdin before starting .NET, whose runtime also duplicates standard handles.
        // /dev/null closes the original pipe but leaves a valid handle for runtime startup.
        // exec preserves the PID so the runner owns the process that stays alive.
        return OperatingSystem.IsWindows()
            ? (ResolvedExecutablePath, [assemblyPath, exitCode])
            : ("/bin/sh", ["-c", """exec 0</dev/null; exec "$@" """, "reject-input", ResolvedExecutablePath, assemblyPath, exitCode]);
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
        int? retainedOutputLineCount = null,
        Action<int>? onStart = null)
    {
        return new ProcessSpec(ExecutablePath)
        {
            ArgumentList = CreateArguments(appPath, arguments),
            OnOutputData = onOutputData,
            OnErrorData = onErrorData,
            OnStart = onStart,
            ThrowOnNonZeroReturnCode = throwOnNonZeroReturnCode,
            ResolveExecutablePath = true,
            RetainedOutputLineCount = retainedOutputLineCount
        };
    }

    public static string[] CreateArguments(string appPath, IReadOnlyList<string>? arguments = null)
    {
        // These fixtures run under the JIT; avoid restoring an unused AOT toolchain
        // whose build diagnostics would contaminate the captured process output.
        return arguments is null or { Count: 0 }
            ? ["run", "--file", appPath, "-p:PublishAot=false"]
            : ["run", "--file", appPath, "-p:PublishAot=false", "--", .. arguments];
    }
}
