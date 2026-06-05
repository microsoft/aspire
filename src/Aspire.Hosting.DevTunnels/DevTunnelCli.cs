// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelCli
{
    public const int ResourceConflictsWithExistingExitCode = 1;
    public const int ResourceNotFoundExitCode = 2;

    public static readonly Version MinimumSupportedVersion = new(1, 0, 1435);

    private readonly string _cliPath;

    public static string GetCliPath(IConfiguration configuration) => configuration["ASPIRE_DEVTUNNEL_CLI_PATH"] ?? "devtunnel";

    public DevTunnelCli(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("CLI path must be provided", nameof(filePath));
        }

        _cliPath = filePath;
    }

    public Task<int> GetVersionAsync(TextWriter? outputWriter = null, TextWriter? errorWriter = null, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(["--version", "--nologo"], outputWriter, errorWriter, logger, cancellationToken);

    public Task<int> UserLoginMicrosoftAsync(ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(["user", "login", "--entra", "--json", "--nologo"], null, null, useShellExecute: true, logger, cancellationToken);

    public Task<int> UserLoginGitHubAsync(ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(["user", "login", "--github", "--json", "--nologo"], null, null, useShellExecute: true, logger, cancellationToken);

    public Task<int> UserLogoutAsync(TextWriter? outputWriter = null, TextWriter? errorWriter = null, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(new ArgsBuilder(["user", "logout", "--json", "--nologo"])
        , outputWriter, errorWriter, logger, cancellationToken);

    public Task<int> UserStatusAsync(TextWriter? outputWriter = null, TextWriter? errorWriter = null, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(["user", "show", "--json", "--nologo"], outputWriter, errorWriter, logger, cancellationToken);

    public Task<int> CreateTunnelAsync(
        string? tunnelId = null,
        DevTunnelOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        options ??= new DevTunnelOptions();
        return RunAsync(new ArgsBuilder(["create"])
            .AddIfNotNull(tunnelId)
            .AddIfNotNull("--description", options.Description)
            .AddIfNotNull("--service-uri", GetServiceUri(options))
            .AddIfTrue("--allow-anonymous", options.AllowAnonymous)
            .AddValues("--labels", options.Labels)
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> UpdateTunnelAsync(
        string tunnelId,
        DevTunnelOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        options ??= new DevTunnelOptions();
        return RunAsync(new ArgsBuilder(["update", tunnelId])
            .AddIfNotNull("--description", options.Description)
            .AddValues("--add-labels", options.Labels)
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> ListPortsAsync(
        string tunnelId,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(new ArgsBuilder(["port", "list", tunnelId])
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> ListAccessAsync(
        string tunnelId,
        int? portNumber = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(new ArgsBuilder(["access", "list", tunnelId])
            .AddIfNotNull("--port-number", portNumber?.ToString(CultureInfo.InvariantCulture))
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> ResetAccessAsync(
        string tunnelId,
        int? portNumber = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(new ArgsBuilder(["access", "reset", tunnelId])
            .AddIfNotNull("--port-number", portNumber?.ToString(CultureInfo.InvariantCulture))
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> CreateAccessAsync(
        string tunnelId,
        int? portNumber = null,
        bool anonymous = false,
        bool deny = false,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        if (!anonymous && !deny)
        {
            throw new ArgumentException($"Must specify either {nameof(anonymous)} or {nameof(deny)} as true, or both, but not neither.");
        }

        return RunAsync(new ArgsBuilder(["access", "create", tunnelId])
            .AddIfNotNull("--port-number", portNumber?.ToString(CultureInfo.InvariantCulture))
            .AddIfTrue("--deny", deny)
            .AddIfTrue("--anonymous", anonymous)
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> DeleteTunnelAsync(
        string tunnelId,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
        => RunAsync(["delete", tunnelId, "--force", "--json", "--nologo"], outputWriter, errorWriter, logger, cancellationToken);

    public Task<int> ShowTunnelAsync(
        string tunnelId,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
        => RunAsync(["show", tunnelId, "--json", "--nologo"], outputWriter, errorWriter, logger, cancellationToken);

    public Task<int> CreatePortAsync(
        string tunnelId,
        int portNumber,
        DevTunnelPortOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        options ??= new DevTunnelPortOptions();
        return RunAsync(new ArgsBuilder(["port", "create", tunnelId])
            .Add("--port-number", portNumber.ToString(CultureInfo.InvariantCulture))
            .AddIfNotNull("--protocol", options.Protocol)
            .AddValues("--labels", options.Labels)
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> UpdatePortAsync(
        string tunnelId,
        int portNumber,
        DevTunnelPortOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
    {
        options ??= new DevTunnelPortOptions();
        return RunAsync(new ArgsBuilder(["port", "update", tunnelId])
            .Add("--port-number", portNumber.ToString(CultureInfo.InvariantCulture))
            .AddIfNotNull("--description", options.Description)
            .AddValues("--add-labels", options.Labels)
            .Add("--json")
            .Add("--nologo")
        , outputWriter, errorWriter, logger, cancellationToken);
    }

    public Task<int> DeletePortAsync(
        string tunnelId,
        int portNumber,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = default,
        CancellationToken cancellationToken = default)
        => RunAsync(["port", "delete", tunnelId, "--port-number", portNumber.ToString(CultureInfo.InvariantCulture), "--json", "--nologo"], outputWriter, errorWriter, logger, cancellationToken);

    private Task<int> RunAsync(string[] args, TextWriter? outputWriter = null, TextWriter? errorWriter = null, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RunAsync(args, outputWriter, errorWriter, useShellExecute: false, logger, cancellationToken);

    private Task<int> RunAsync(string[] args, TextWriter? outputWriter = null, TextWriter? errorWriter = null, bool useShellExecute = false, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        return RunAsync((isError, line) =>
        {
            if (isError)
            {
                errorWriter?.WriteLine(line);
            }
            else
            {
                outputWriter?.WriteLine(line);
            }
        }, args, useShellExecute, logger, cancellationToken);
    }

    private async Task<int> RunAsync(Action<bool, string> onOutput, string[] args, bool useShellExecute = false, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = BuildStartInfo(args, useShellExecute),
            EnableRaisingEvents = true
        };

        var stdoutTask = Task.CompletedTask;
        var stderrTask = Task.CompletedTask;

        logger?.LogTrace("Invoking devtunnel CLI{ShellExecuteInfo}: {FileName} {Arguments}", useShellExecute ? " (UseShellExecute=true)" : "", process.StartInfo.FileName, EscapeArgList(process.StartInfo.ArgumentList));

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start devtunnel process.");
            }

            if (!useShellExecute)
            {
                stdoutTask = PumpAsync(process.StandardOutput, line => onOutput(false, line), cancellationToken);
                stderrTask = PumpAsync(process.StandardError, line => onOutput(true, line), cancellationToken);
            }

            using var ctr = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        logger?.LogTrace("Cancellation requested, killing devtunnel process tree.");
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            logger?.LogTrace("devtunnel CLI exited with exit code '{ExitCode}'.", process.ExitCode);
            return process.ExitCode;
        }
        finally
        {
            if (!useShellExecute)
            {
                try
                {
                    logger?.LogTrace("devtunnel CLI exited, draining stdout/stderr.");
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static string? EscapeArgList(Collection<string> args)
    {
        if (args.Count == 0)
        {
            return null;
        }

        StringBuilder? sb = null;

        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a))
            {
                continue;
            }

            sb ??= new StringBuilder(args.Count * 2);
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            var needsQuotes = a.Any(char.IsWhiteSpace) || a.Contains('"');
            if (needsQuotes)
            {
                sb.Append('"');
            }

            foreach (var c in a)
            {
                if (c == '"')
                {
                    sb.Append('\\'); // Escape quote
                }
                sb.Append(c);
            }

            if (needsQuotes)
            {
                sb.Append('"');
            }
        }

        return sb?.ToString();
    }

    private ProcessStartInfo BuildStartInfo(IEnumerable<string> args, bool useShellExecute = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            UseShellExecute = useShellExecute,
            RedirectStandardOutput = !useShellExecute,
            RedirectStandardError = !useShellExecute,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        // Prefer ArgumentList to avoid quoting issues.
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a))
            {
                continue;
            }

            psi.ArgumentList.Add(a);
        }

        // Ensure consistent encoding on Windows terminals
        if (!useShellExecute && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        return psi;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            if (line.Length > 0)
            {
                onLine(line);
            }
        }
    }

    private static string? GetServiceUri(DevTunnelOptions options)
    {
        if (options.Region is null)
        {
            return null;
        }

        return $"https://{options.RegionCode}.rel.tunnels.api.visualstudio.com";
    }
}

internal sealed class ArgsBuilder(IEnumerable<string> initialArgs)
{
    private readonly List<string> _args = [.. initialArgs];

    public ArgsBuilder Add(string value)
    {
        _args.Add(value);
        return this;
    }

    public ArgsBuilder Add(string name, string value)
    {
        _args.Add(name);
        _args.Add(value);
        return this;
    }

    public ArgsBuilder AddIfNotNull(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _args.Add(value);
        }
        return this;
    }

    public ArgsBuilder AddIfNotNull(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _args.Add(name);
            _args.Add(value);
        }
        return this;
    }

    public ArgsBuilder AddIfTrue(string name, bool? condition)
    {
        if (condition == true)
        {
            _args.Add(name);
        }
        return this;
    }

    public ArgsBuilder AddIfFalse(string name, bool? condition)
    {
        if (condition == false)
        {
            _args.Add(name);
        }
        return this;
    }

    internal ArgsBuilder AddValues(string name, List<string>? values, char? separator = null)
    {
        if (values is null || values.Count == 0)
        {
            return this;
        }

        if (separator is null)
        {
            // Add each value separately, e.g. --label a --label b
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    _args.Add(name);
                    _args.Add(v);
                }
            }
            return this;
        }
        else
        {
            // Build a single separated string of values, e.g. --labels a,b
            // Assumes values themselves do not contain the separator character.
            var tokens = values.Where(l => !string.IsNullOrWhiteSpace(l));
            var joined = string.Join(separator.Value, tokens);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                _args.Add(name);
                _args.Add(joined);
            }
        }

        return this;
    }

    public static implicit operator string[](ArgsBuilder builder) => [.. builder._args];
}
