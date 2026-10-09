// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !NET11_0_OR_GREATER
// Hosting and RemoteHost source-share this helper; use the process owner's copy.
extern alias RemoteHost;
#endif

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#if NET11_0_OR_GREATER
using TestProcessStartTimeHelper = global::ProcessStartTimeHelper;
using TestProcessSignaler = global::ProcessSignaler;
#else
using TestProcessStartTimeHelper = RemoteHost::ProcessStartTimeHelper;
using TestProcessSignaler = RemoteHost::ProcessSignaler;
#endif

namespace Aspire.Shared.Tests;

[CollectionDefinition(Name)]
public class ProcessTestCollection : ICollectionFixture<ProcessTestFixture>
{
    public const string Name = "Shared process primitives";
}

public sealed class ProcessTestFixture : IAsyncLifetime
{
#if NET11_0_OR_GREATER
    internal const int RuntimeMajor = 11;
#else
    internal const int RuntimeMajor = 10;
#endif
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-process-tests-");
    private string _assemblyPath = "";

    public async ValueTask InitializeAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "Aspire.slnx")))
        {
            repository = repository.Parent ?? throw new InvalidOperationException("Cannot find the repository root.");
        }

        var targetFramework = $"net{RuntimeMajor}.0";
        // MTP's entry point cannot re-enter guardian mode without launching another test run.
        // Compile the production primitives into a minimal executable for this test runtime.
        string[] sources =
        [
            "IChildProcess.cs", "ChildProcess.cs", "ChildProcessOptions.cs", "ProcessScope.cs",
            "ProcessSupervisor.cs", "ProcessSupervisorLogger.cs", "ProcessSupervisor.Windows.cs", "ParentProcessLivenessMonitor.cs",
            "ProcessStartTimeHelper.cs", "ProcessSignaler.cs", "KnownConfigNames.cs"
        ];
        var includes = string.Join(Environment.NewLine, sources.Select(source =>
            $"""<Compile Include="{SecurityElement.Escape(Path.Combine(repository.FullName, "src", "Shared", source))}" />"""));
        var project = Path.Combine(_directory.FullName, "ProcessTestHost.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{{targetFramework}}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                {{includes}}
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(_directory.FullName, "Program.cs"), HostProgram);
        await RunDotnetAsync(repository.FullName,
            ["restore", project, "--configfile", Path.Combine(repository.FullName, "NuGet.config"), "--verbosity", "quiet"]);
        await RunDotnetAsync(repository.FullName, ["build", project, "--no-restore", "--verbosity", "quiet"]);
        _assemblyPath = Path.Combine(_directory.FullName, "bin", "Debug", targetFramework, "ProcessTestHost.dll");
    }

    internal DirectoryInfo CreateDirectory() => _directory.CreateSubdirectory(Guid.NewGuid().ToString("N"));

    internal ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_assemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal ProcessStartInfo CreateSupervisorStartInfo(ProcessStartInfo command, string? completionPath = null)
        => CreateSupervisorStartInfo(command, completionPath, TimeSpan.FromSeconds(5));

    internal ProcessStartInfo CreateSupervisorStartInfo(ProcessStartInfo command, string? completionPath, TimeSpan terminationTimeout)
    {
        var startInfo = ProcessSupervisor.CreateStartInfo(command, completionPath, terminationTimeout);
        // Keep the production command handoff and owner identity, replacing only MTP's
        // executable/argv with the standalone guardian entry point.
        startInfo.FileName = "dotnet";
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add(_assemblyPath);

        return startInfo;
    }

    internal string AssemblyPath => _assemblyPath;

    internal static void RequestGracefulShutdown(int processId) =>
        TestProcessSignaler.RequestGracefulShutdown(processId, expectedStartTime: null, NullLogger.Instance);

    internal static ChildProcess CreateProcess(ProcessStartInfo startInfo, ChildProcessOptions options) =>
        CreateProcess(startInfo, NullLogger.Instance, options);

    internal static ChildProcess CreateProcess(ProcessStartInfo startInfo, ILogger logger, ChildProcessOptions options) =>
        new(startInfo, logger, options, OperatingSystem.IsWindows());

    internal static async Task AssertExitedAsync(ProcessTestIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return;
            }
            var startTime = TestProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(identity.ProcessId);
            if (startTime is null)
            {
                Assert.True(process.HasExited, $"Cannot inspect test process {identity.ProcessId}.");
                return;
            }
            if (startTime != identity.StartTime)
            {
                // PID reuse means the original process has already been reaped.
                return;
            }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // The OS has already reaped the test-owned process.
        }
    }

    internal static void KillIfRunning(ProcessTestIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!process.HasExited &&
                TestProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(identity.ProcessId) == identity.StartTime)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // The OS has already reaped the test-owned process.
        }
        catch (InvalidOperationException)
        {
            // Exit raced with termination.
        }
    }

    private static async Task RunDotnetAsync(string workingDirectory, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            await Task.WhenAll(output, error);
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process test host build failed: {await output}\n{await error}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _directory.Delete(recursive: true);

        return ValueTask.CompletedTask;
    }

    private const string HostProgram = """
        using System.Diagnostics;
        using System.Globalization;
        using System.IO.Pipes;
        using System.Runtime.InteropServices;
        using System.Text.Json;
        using Aspire.Shared;
        using Microsoft.Extensions.Logging.Abstractions;

        if (ProcessSupervisor.IsSupervisor)
        {
            await ProcessSupervisor.RunAsync();
            return;
        }

        ProcessStartInfo Command(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            }
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            return startInfo;
        }

        async Task NotifyReady(string name, string role, int pid)
        {
            var startTime = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(pid)
                ?? throw new InvalidOperationException($"Cannot identify test process {pid}.");
            using var pipe = new NamedPipeClientStream(".", $"{name}-{role}", PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30_000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(FormattableString.Invariant($"{pid}:{startTime}"));
        }

        void Block()
        {
            // Deliberately block user code; cleanup must not depend on its event loop or a timer.
            using var blocked = new ManualResetEventSlim();
            blocked.Wait();
        }

        switch (args[0])
        {
            case "argv":
                Console.WriteLine(JsonSerializer.Serialize(args[1..]));
                break;
            case "output":
                Console.WriteLine($"runtime:{Environment.Version.Major}");
                Console.WriteLine($"stdin:{(await Console.In.ReadToEndAsync()).Length}");
                for (var i = 0; i < 256; i++)
                {
                    Console.WriteLine($"stdout:{i}");
                    Console.Error.WriteLine($"stderr:{i}");
                }
                Environment.ExitCode = int.Parse(args[1], CultureInfo.InvariantCulture);
                break;
            case "wait":
                await NotifyReady(args[1], "runtime", Environment.ProcessId);
                Block();
                break;
            case "worker":
                await NotifyReady(args[1], "worker", Environment.ProcessId);
                await NotifyReady(args[1], "started", Environment.ProcessId);
                Block();
                break;
            case "tree":
            case "tree-exit":
            case "tree-graceful":
                using (var started = new NamedPipeServerStream($"{args[1]}-started", PipeDirection.In,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                using (var worker = Process.Start(Command("worker", args[1]))!)
                {
                    var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var sigterm = args[0] == "tree-graceful" ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                    {
                        context.Cancel = true;
                        shutdown.TrySetResult();
                    }) : null;
                    await started.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    using var reader = new StreamReader(started);
                    await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    await NotifyReady(args[1], "runtime", Environment.ProcessId);
                    if (args[0] == "tree-exit")
                    {
                        Environment.ExitCode = int.Parse(args[2], CultureInfo.InvariantCulture);
                    }
                    else if (args[0] == "tree-graceful")
                    {
                        await shutdown.Task;
                        Environment.ExitCode = 23;
                    }
                    else
                    {
                        Block();
                    }
                }
                break;
            case "owner":
                await using (var guardian = new ChildProcess(
                    Command("tree", args[1]),
                    NullLogger.Instance, new ChildProcessOptions { Lifetime = ChildProcessLifetime.OwnedTree }, OperatingSystem.IsWindows()))
                {
                    await guardian.StartAsync(CancellationToken.None);
                    await using var scope = new ProcessScope(guardian, NullLogger.Instance, "test owner");
                    await NotifyReady(args[1], "guardian", scope.ProcessId);
                    Block();
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown process test command: {args[0]}");
        }
        """;
}

internal sealed class ProcessTestReadiness : IDisposable
{
    internal string Name { get; } = $"asp-p-{Guid.NewGuid():N}"[..18];
    private readonly NamedPipeServerStream _runtime;
    private readonly NamedPipeServerStream _worker;
    private readonly NamedPipeServerStream _guardian;

    internal ProcessTestReadiness()
    {
        _runtime = CreatePipe("runtime");
        _worker = CreatePipe("worker");
        _guardian = CreatePipe("guardian");
    }

    internal Task<ProcessTestIdentity> ReadRuntimeAsync() => ReadIdentityAsync(_runtime);

    internal Task<ProcessTestIdentity> ReadGuardianAsync() => ReadIdentityAsync(_guardian);

    internal Task<ProcessTestIdentity[]> ReadTreeAsync() => Task.WhenAll(ReadIdentityAsync(_runtime), ReadIdentityAsync(_worker));

    private NamedPipeServerStream CreatePipe(string role) =>
        new($"{Name}-{role}", PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<ProcessTestIdentity> ReadIdentityAsync(NamedPipeServerStream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        // Each connection sends "PID:stable-start-time\n", e.g. "12345:1760000000000\n".
        // Identify the process while it is alive so later cleanup cannot target a reused PID.
        var line = await reader.ReadLineAsync(timeout.Token)
            ?? throw new InvalidOperationException("The test process disconnected before reporting readiness.");
        var separator = line.IndexOf(':');
        if (separator < 1)
        {
            throw new InvalidOperationException($"Invalid test process identity: {line}");
        }

        return new ProcessTestIdentity(
            int.Parse(line.AsSpan(0, separator), CultureInfo.InvariantCulture),
            long.Parse(line.AsSpan(separator + 1), CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _worker.Dispose();
        _guardian.Dispose();
    }
}

internal readonly record struct ProcessTestIdentity(int ProcessId, long StartTime)
{
    internal static ProcessTestIdentity Capture(int processId) =>
        new(processId, TestProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(processId)
            ?? throw new InvalidOperationException($"Cannot identify test process {processId}."));
}
