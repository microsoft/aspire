// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RemoteDependencies.Tools;

internal sealed class Session
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly Task _reader;
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private bool _stopped;

    internal Session(string repo, Fixture fixture, Dictionary<string, string> environment, string name,
        string? localPort = null, string? image = null)
    {
        LogPath = Path.Combine(fixture.StateDirectory, $"verify-{name}.log");
        _log = new StreamWriter(LogPath) { AutoFlush = true };
        var info = Commands.StartInfo(Path.Combine(repo, ".dotnet", "dotnet"),
            Path.Combine(repo, "artifacts", "bin", "RemoteDependencies.AppHost", "Debug", "net10.0", "RemoteDependencies.AppHost.dll"));
        info.WorkingDirectory = repo;
        foreach (var pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }
        info.Environment["REMOTE_HEADLESS"] = "1";
        if (localPort is not null)
        {
            info.Environment["REMOTE_LOCAL_PORT"] = localPort;
        }
        if (image is not null)
        {
            info.Environment["REMOTE_IMAGE"] = image;
        }
        _process = Process.Start(info) ?? throw new InvalidOperationException("Could not start AppHost.");
        _reader = Task.WhenAll(ReadAsync(_process.StandardOutput), ReadAsync(_process.StandardError));
    }

    internal int ProcessId => _process.Id;
    internal string LogPath { get; }
    internal bool State(string state) => _events.Any(e =>
        e.GetProperty("kind").GetString() == "state" && e.GetProperty("resource").GetString() == "cache" &&
        e.GetProperty("state").GetString() == state);
    internal bool Content(string text) => _events.Any(e =>
        e.TryGetProperty("content", out var content) && content.GetString()!.Contains(text, StringComparison.Ordinal));
    internal bool InactiveEndpoint => _events.Any(e =>
        e.TryGetProperty("urls", out var urls) && urls.EnumerateArray().Any(u => u.GetProperty("IsInactive").GetBoolean()));

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_log)
            {
                _log.WriteLine(line);
            }
            if (line.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(line);
                _events.Enqueue(document.RootElement.Clone());
            }
        }
    }

    internal async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }
        try
        {
            if (!_process.HasExited)
            {
                await Commands.RunAsync("kill", "-INT", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                throw new TimeoutException($"AppHost failed to stop gracefully; see {LogPath}");
            }
            await _reader;
            Commands.Require(_process.ExitCode == 0, $"AppHost exit {_process.ExitCode}; see {LogPath}");
        }
        finally
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                await _reader;
            }
            finally
            {
                _stopped = true;
                _log.Dispose();
                _process.Dispose();
            }
        }
    }
}
