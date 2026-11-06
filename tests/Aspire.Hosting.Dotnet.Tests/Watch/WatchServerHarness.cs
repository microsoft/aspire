// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
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
/// it through the real <c>aspire __dotnet-watch-proxy</c>, the same way <see cref="DotnetWatchCoordinator"/> does.
/// </summary>
/// <remarks>
/// Inputs (all optional): <c>ASPIRE_DOTNET_WATCH_TOOL_PATH</c> (watch tool folder or dll; otherwise the tool is
/// acquired the same way <c>aspire run --watch</c> acquires it) and <c>ASPIRE_DOTNET_WATCH_PROXY_PATH</c> (the CLI
/// <c>aspire.dll</c>/executable; defaults to the repo build output). Set <c>ASPIRE_WATCH_CONTRACT_RECORD_DIR</c> to
/// save each run's observations as a recording for <see cref="DotnetWatchRecordedOutputTests"/>.
/// </remarks>
internal sealed class WatchServerHarness : IAsyncDisposable
{
    public const string RootPlaceholder = "$ROOT$";

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(3);

    private readonly List<WatchObservation> _observations = [];
    private readonly StringBuilder _proxyOutput = new();
    private readonly List<Process> _processes = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly ITestOutputHelper _output;
    private readonly string _runDirectory;
    private Task? _statusTask;

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
    }

    public string Directory { get; }
    public string AppDirectory { get; }
    public string ProjectPath { get; }
    public string ResourcePipe { get; }
    public string StatusPipe { get; }
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
        var directory = Path.Combine(Path.GetTempPath(), $"awc-{Guid.NewGuid().ToString("N")[..8]}");
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

        var server = CreateStartInfo(Muxer, Directory, ["exec", toolDll, "server", "--sdk", sdk.SdkDirectory, "--server", ResourcePipe, "--status-pipe", StatusPipe, "--resource", ProjectPath]);
        StartProcess(server, line => Record(WatchObservationKind.Output, line), "server");
    }

    /// <summary>Path of the CLI that implements <c>__dotnet-watch-proxy</c>, or <see langword="null"/> when it isn't built.</summary>
    public static string? FindProxy()
    {
        if (Environment.GetEnvironmentVariable(DotnetWatchCoordinator.ProxyPathConfigurationKey) is { Length: > 0 } configured)
        {
            return configured;
        }

        var path = typeof(WatchServerHarness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AspireCliPath")?.Value;
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Launches one service instance through the real proxy, with the service environment set on the proxy process.</summary>
    public Process LaunchThroughProxy(string proxyPath, IReadOnlyList<string> appArgs, IReadOnlyDictionary<string, string> environment, IReadOnlyCollection<string> forwardedNames)
    {
        List<string> args = [];
        var command = proxyPath;
        if (proxyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            // As in `aspire run`, the framework-dependent CLI runs on the muxer that hosts the CLI, not the watcher's.
            command = Environment.GetEnvironmentVariable(DotnetWatchCoordinator.ProxyHostConfigurationKey) is { Length: > 0 } host
                ? host
                : typeof(WatchServerHarness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AspireCliHost")?.Value is { Length: > 0 } built
                    ? built
                    : "dotnet";
            args.Add(proxyPath);
        }

        args.AddRange([DotnetWatchCoordinator.ProxyCommandName, "--server", ResourcePipe, "--entrypoint", ProjectPath, "--"]);
        args.AddRange(appArgs);

        var startInfo = CreateStartInfo(command, AppDirectory, args);
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

    /// <summary>
    /// Writes the observations in the recording format (<c>status &lt;json&gt;</c> / <c>out &lt;line&gt;</c>, with the
    /// fixture root replaced by <see cref="RootPlaceholder"/>) when <c>ASPIRE_WATCH_CONTRACT_RECORD_DIR</c> is set.
    /// </summary>
    public void SaveRecording(string name)
    {
        var lines = Observations.Select(o => (o.Kind == WatchObservationKind.Status ? "status " : "out ") + Normalize(o.Text)).ToArray();
        _output.WriteLine($"--- recording {name} ---{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
        if (Environment.GetEnvironmentVariable("ASPIRE_WATCH_CONTRACT_RECORD_DIR") is { Length: > 0 } dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, name + ".log"), lines);
        }
    }

    private string Normalize(string text)
    {
        text = text.Replace(JsonEncodedText.Encode(Directory).ToString(), RootPlaceholder, StringComparison.Ordinal)
            .Replace(Directory, RootPlaceholder, StringComparison.Ordinal);
        return Path.GetDirectoryName(Muxer) is { Length: > 0 } dotnetRoot ? text.Replace(dotnetRoot, "$DOTNET$", StringComparison.Ordinal) : text;
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
