// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Dotnet.Watch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Dotnet.Tests.Watch;

/// <summary>One observation from the real watch server: a status-pipe event or a console line.</summary>
internal sealed record WatchObservation(WatchObservationKind Kind, string Text)
{
    public string? StatusType => Kind == WatchObservationKind.Status ? Status.GetProperty("type").GetString() : null;

    public JsonElement Status => JsonDocument.Parse(Text).RootElement;
}

internal enum WatchObservationKind
{
    Status,
    Output,
}

/// <summary>
/// Runs the real <c>Microsoft.DotNet.HotReload.Watch.Aspire server</c> against a copy of a fixture project and launches
/// it through the real watch host proxy (<c>dotnet exec Aspire.Hosting.Dotnet.WatchHost.dll proxy</c>), the same way
/// <see cref="DotnetWatchCoordinator"/> does.
/// </summary>
/// <remarks>
/// Inputs (all optional): <c>ASPIRE_DOTNET_WATCH_TOOL_PATH</c> (watch tool folder or dll; otherwise the tool is
/// acquired the same way <c>aspire run --watch</c> acquires it). The watch host comes from the repo layout that
/// Aspire.Hosting.Dotnet writes at build (this assembly's <c>aspiredotnetwatchhostdir</c> metadata).
/// Console lines are recorded for diagnostics only; the tests assert on status events and the app's responses.
/// </remarks>
internal sealed class WatchServerHarness : IAsyncDisposable
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(3);

    private readonly List<WatchObservation> _observations = [];
    private readonly StringBuilder _proxyOutput = new();
    private readonly List<Process> _processes = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly ITestOutputHelper _output;
    private readonly string _runDirectory;
    private Task? _statusTask;
    private DotnetWatchControlPipeServer? _controlServer;

    private WatchServerHarness(ITestOutputHelper output, string directory)
    {
        _output = output;
        Directory = directory;
        _runDirectory = Path.Combine(directory, "run");
        System.IO.Directory.CreateDirectory(_runDirectory);
        AppDirectory = Path.Combine(directory, "ContractApp");
        ProjectPath = Path.Combine(AppDirectory, "ContractApp.csproj");
        ResourcePipe = OperatingSystem.IsWindows() ? $"aspire-watch-contract-{Guid.NewGuid():N}" : Path.Combine(_runDirectory, "res.sock");
        StatusPipe = OperatingSystem.IsWindows() ? $"aspire-watch-contract-status-{Guid.NewGuid():N}" : Path.Combine(_runDirectory, "status.sock");
        ControlPipe = OperatingSystem.IsWindows() ? $"aspire-watch-contract-control-{Guid.NewGuid():N}" : Path.Combine(_runDirectory, "control.sock");
    }

    public string Directory { get; }
    public string AppDirectory { get; }
    public string ProjectPath { get; }
    public string ResourcePipe { get; }
    public string StatusPipe { get; }
    public string ControlPipe { get; }
    public string Muxer { get; private set; } = "dotnet";

    public string ProxyOutput
    {
        get
        {
            lock (_proxyOutput)
            {
                return _proxyOutput.ToString();
            }
        }
    }

    public IReadOnlyList<WatchObservation> Observations
    {
        get
        {
            lock (_observations)
            {
                return _observations.ToArray();
            }
        }
    }

    /// <summary>Copies the fixture app to a temp directory and starts the watch server on it.</summary>
    public static async Task<WatchServerHarness> StartAsync(ITestOutputHelper output, Action<string>? prepareApp = null)
    {
        // Short root: Unix socket paths are limited to ~108 characters.
        var directory = System.IO.Directory.CreateTempSubdirectory("awc-").FullName;
        var harness = new WatchServerHarness(output, directory);
        try
        {
            await harness.StartCoreAsync(prepareApp).ConfigureAwait(false);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(Action<string>? prepareApp)
    {
        // Watch mode supports .NET 10 SDKs only: select the newest installed one for the fixture, then pin it exactly
        // (as the coordinator does). Isolate the fixture from the repo build, whose global.json selects a newer SDK.
        System.IO.Directory.CreateDirectory(Directory);
        var globalJson = Path.Combine(Directory, "global.json");
        File.WriteAllText(globalJson, $$"""{ "sdk": { "version": "{{DotnetWatchToolResolver.SupportedSdkMajor}}.0.100", "rollForward": "latestFeature" } }""");
        var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(Directory, _cts.Token).ConfigureAwait(false);
        Muxer = sdk.Muxer;
        File.WriteAllText(globalJson, $$"""{ "sdk": { "version": "{{sdk.SdkVersion}}", "rollForward": "disable" } }""");

        System.IO.Directory.CreateDirectory(AppDirectory);
        foreach (var file in System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Watch", "Fixtures", "ContractApp")))
        {
            File.Copy(file, Path.Combine(AppDirectory, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(Directory, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(Directory, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(Directory, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        File.WriteAllText(ProjectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net{DotnetWatchToolResolver.SupportedSdkMajor}.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """);
        prepareApp?.Invoke(AppDirectory);

        var toolDll = DotnetWatchToolResolver.ResolveToolPath(
            Environment.GetEnvironmentVariable(DotnetWatchToolResolver.ToolPathConfigurationKey),
            DotnetWatchToolResolver.GetToolsDirectory(typeof(WatchServerHarness).Assembly),
            sdk.SdkVersion,
            NullLogger.Instance);
        _output.WriteLine($"SDK {sdk.SdkVersion} ({sdk.SdkDirectory}); watch tool {toolDll}");

        // The listener must exist before the watcher connects to it.
        var statusServer = new NamedPipeServerStream(StatusPipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _statusTask = Task.Run(() => ReadStatusAsync(statusServer, _cts.Token));

        _controlServer = new DotnetWatchControlPipeServer(ControlPipe,
            exception => Record(WatchObservationKind.Output, $"Control transport failed: {exception}"));
        _controlServer.Start();

        var server = CreateStartInfo(Muxer, Directory, ["exec", toolDll, "server", "--sdk", sdk.SdkDirectory, "--server", ResourcePipe, "--status-pipe", StatusPipe, "--control-pipe", ControlPipe, "--resource", ProjectPath]);
        StartProcess(server, line => Record(WatchObservationKind.Output, line), "server");
    }

    /// <summary>Sends a <c>rebuild</c> command for <paramref name="projects"/> on the watcher's control pipe.</summary>
    public Task<bool> RequestRebuildAsync(params string[] projects) =>
        _controlServer!.SendRebuildAsync(projects, _cts.Token);

    /// <summary>How many times the watcher connected to the control pipe.</summary>
    public int ControlConnections => _controlServer?.ConnectionCount ?? 0;

    /// <summary>Path of the watch host, or <see langword="null"/> when Aspire.Hosting.Dotnet hasn't laid it out.</summary>
    public static string? FindWatchHost()
    {
        var directory = DotnetWatchToolResolver.GetWatchHostDirectory(typeof(WatchServerHarness).Assembly);
        var path = directory is null ? null : Path.Combine(directory, "Aspire.Hosting.Dotnet.WatchHost.dll");
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Launches one service instance through the real proxy, with the service environment set on the proxy process.</summary>
    public Process LaunchThroughProxy(string watchHostPath, IReadOnlyList<string> appArgs, IReadOnlyDictionary<string, string> environment, IReadOnlyCollection<string> forwardedNames)
    {
        // As in the coordinator, the watch host runs on the watcher's muxer.
        List<string> args = ["exec", watchHostPath, "proxy", "--server", ResourcePipe, "--entrypoint", ProjectPath, "--"];
        args.AddRange(appArgs);

        var startInfo = CreateStartInfo(Muxer, AppDirectory, args);
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        startInfo.Environment[DotnetWatchCoordinator.ForwardedEnvironmentNamesVariable] = string.Join(';', forwardedNames);
        return StartProcess(startInfo, line =>
        {
            lock (_proxyOutput)
            {
                _proxyOutput.AppendLine(line);
            }
        }, "proxy");
    }

    /// <summary>Waits for an observation at or after <paramref name="fromIndex"/> that matches <paramref name="predicate"/>.</summary>
    public async Task<(WatchObservation Observation, int Index)> WaitForAsync(int fromIndex, Func<WatchObservation, bool> predicate, string description, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < (timeout ?? s_defaultTimeout))
        {
            var observations = Observations;
            for (var i = fromIndex; i < observations.Count; i++)
            {
                if (predicate(observations[i]))
                {
                    return (observations[i], i);
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {description}. Observations:{Environment.NewLine}{string.Join(Environment.NewLine, Observations.Select(o => $"{o.Kind}: {o.Text}"))}");
    }

    public Task<(WatchObservation Observation, int Index)> WaitForStatusAsync(int fromIndex, string type, Func<JsonElement, bool>? predicate = null) =>
        WaitForAsync(fromIndex, o => o.StatusType == type && (predicate?.Invoke(o.Status) ?? true), $"status '{type}'");

    public async Task WaitForProxyOutputAsync(string text, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!ProxyOutput.Contains(text, StringComparison.Ordinal))
        {
            if (deadline.Elapsed > (timeout ?? s_defaultTimeout))
            {
                throw new TimeoutException($"Timed out waiting for proxy output '{text}'. Proxy output:{Environment.NewLine}{ProxyOutput}");
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private void Record(WatchObservationKind kind, string text)
    {
        lock (_observations)
        {
            _observations.Add(new WatchObservation(kind, text));
        }
    }

    private async Task ReadStatusAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    Record(WatchObservationKind.Status, line);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string workingDirectory, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Don't leak the test host's MSBuild/SDK selection into the watcher's builds.
        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) || k is "MSBUILD_EXE_PATH" or "DOTNET_HOST_PATH" || k.StartsWith("VSTEST", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        // Don't leave build servers behind after the test.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["UseSharedCompilation"] = "false";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        return startInfo;
    }

    private Process StartProcess(ProcessStartInfo startInfo, Action<string> onLine, string label)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { onLine(line); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { onLine(line); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        _output.WriteLine($"Started {label} (pid {process.Id}): {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}");
        return process;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        // Proxies first: disconnecting makes the server terminate the app it launched for them.
        foreach (var process in Enumerable.Reverse(_processes))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        if (_controlServer is not null)
        {
            await _controlServer.DisposeAsync().ConfigureAwait(false);
        }

        if (_statusTask is not null)
        {
            await _statusTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _cts.Dispose();
    }
}
