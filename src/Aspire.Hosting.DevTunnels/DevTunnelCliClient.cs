// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelCliClient(IConfiguration configuration) : IDevTunnelClient
{
    private readonly int _maxCliAttempts = configuration.GetValue<int?>("ASPIRE_DEVTUNNEL_CLI_MAX_ATTEMPTS") ?? 3;
    private readonly TimeSpan _cliRetryOnErrorDelay = TimeSpan.FromSeconds(2);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly DevTunnelCli _cli = new(DevTunnelCli.GetCliPath(configuration));

    public async Task<Version> GetVersionAsync(ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();

        var exitCode = await _cli.GetVersionAsync(outputWriter, errorWriter, logger, cancellationToken).ConfigureAwait(false);
        var output = outputWriter.ToString().Trim();

        if (exitCode == 0)
        {
            // Find the line with the version number. It will look like "Tunnel CLI version: 1.0.1435+d49a94cc24"
            var prefix = "Tunnel CLI version:";
            var versionLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var versionString = versionLine?.Length > prefix.Length
                ? versionLine[prefix.Length..].Trim()
                : output;

            // Trim the commit SHA suffix if present
            if (versionString.IndexOf('+') is >= 0 and var plusIndex)
            {
                versionString = versionString[..plusIndex];
            }

            if (Version.TryParse(versionString, out var version))
            {
                return version;
            }
        }

        var error = errorWriter.ToString().Trim();
        throw new DistributedApplicationException($"Failed to get devtunnel CLI version. Output: '{output}'. Error: '{error}'");
    }

    public async Task<DevTunnelStatus> CreateTunnelAsync(string tunnelId, DevTunnelOptions options, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        var exitCode = 0;
        string? error = null;
        string resolvedTunnelId = options.Region is not null ? $"{tunnelId}.{options.RegionCode}" : tunnelId;

        while (attempts < _maxCliAttempts)
        {
            logger?.LogTrace("Creating dev tunnel '{TunnelId}' with options: {Options}", tunnelId, options.ToLoggerString());
            if (attempts++ > 1)
            {
                logger?.LogTrace("Attempt {Attempt} of {MaxAttempts} to create dev tunnel '{TunnelId}'", attempts, _maxCliAttempts, tunnelId);
            }
            (var tunnel, exitCode, error) = await CallCliAsJsonAsync<DevTunnelStatus>((stdout, stderr, log, ct) => _cli.CreateTunnelAsync(tunnelId, options, stdout, stderr, log, ct),
                "tunnel",
                logger, cancellationToken).ConfigureAwait(false);

            if (exitCode == 0 && tunnel is not null)
            {
                logger?.LogTrace("Dev tunnel '{TunnelId}' created successfully.", tunnelId);
                return tunnel;
            }

            if (exitCode == DevTunnelCli.ResourceConflictsWithExistingExitCode)
            {
                // Update the tunnel as it already exists
                logger?.LogTrace("Dev tunnel '{TunnelId}' already exists, will update it instead.", tunnelId);
                (tunnel, exitCode, error) = await CallCliAsJsonAsync<DevTunnelStatus>(
                    (stdout, stderr, log, ct) => _cli.UpdateTunnelAsync(resolvedTunnelId, options, stdout, stderr, log, ct),
                    logger, cancellationToken).ConfigureAwait(false);
                if (exitCode == 0 && tunnel is not null)
                {
                    logger?.LogTrace("Dev tunnel '{TunnelId}' updated successfully.", resolvedTunnelId);

                    // Ensure tunnel access controls are set as specified in options by resetting existing policies first.
                    // Ports get deleted and recreated separately, so we only need to reset access on the tunnel itself here.
                    logger?.LogTrace("Clearing access policies for dev tunnel '{TunnelId}'.", tunnelId);
                    (var accessStatus, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
                        (stdout, stderr, log, ct) => _cli.ResetAccessAsync(resolvedTunnelId, portNumber: null, stdout, stderr, log, ct),
                        logger, cancellationToken).ConfigureAwait(false);
                    if (exitCode == 0 && accessStatus is { AccessControlEntries: [] })
                    {
                        logger?.LogTrace("Dev tunnel '{TunnelId}' access policies cleared successfully.", resolvedTunnelId);
                        if (options.AllowAnonymous)
                        {
                            // Set anonymous access as specified
                            logger?.LogTrace("Allowing anonymous access for dev tunnel '{TunnelId}'.", resolvedTunnelId);
                            (accessStatus, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
                                (stdout, stderr, log, ct) => _cli.CreateAccessAsync(resolvedTunnelId, portNumber: null, anonymous: true, deny: false, stdout, stderr, log, ct),
                                logger, cancellationToken).ConfigureAwait(false);
                            if (exitCode == 0 && accessStatus is not null)
                            {
                                logger?.LogTrace("Dev tunnel '{TunnelId}' anonymous access set successfully.", resolvedTunnelId);
                            }
                        }
                        if (exitCode == 0 && accessStatus is not null)
                        {
                            return tunnel;
                        }
                    }
                }
            }

            logger?.LogError("Failed to create dev tunnel '{TunnelId}' (attempt {Attempt} of {MaxAttempts}). Exit code {ExitCode}: {Error}", tunnelId, attempts, _maxCliAttempts, exitCode, error);
            if (attempts < _maxCliAttempts)
            {
                logger?.LogTrace("Waiting {WaitSeconds} seconds before retrying to create dev tunnel '{TunnelId}'", _cliRetryOnErrorDelay.TotalSeconds, tunnelId);
                await Task.Delay(_cliRetryOnErrorDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DistributedApplicationException($"Failed to create dev tunnel '{tunnelId}' after {attempts} attempts. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelStatus> GetTunnelAsync(string tunnelId, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Getting details for dev tunnel '{TunnelId}'.", tunnelId);
        var (tunnel, exitCode, error) = await CallCliAsJsonAsync<DevTunnelStatus>(
            (stdout, stderr, log, ct) => _cli.ShowTunnelAsync(tunnelId, stdout, stderr, log, ct),
            "tunnel",
            logger, cancellationToken).ConfigureAwait(false);
        return tunnel ?? throw new DistributedApplicationException($"Failed to get dev tunnel '{tunnelId}'. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelPortList> GetPortListAsync(string tunnelId, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Getting port list for dev tunnel '{TunnelId}'.", tunnelId);
        var (ports, exitCode, error) = await CallCliAsJsonAsync<DevTunnelPortList>(
            (stdout, stderr, log, ct) => _cli.ListPortsAsync(tunnelId, stdout, stderr, log, ct),
            logger, cancellationToken).ConfigureAwait(false);
        return ports ?? throw new DistributedApplicationException($"Failed to get port list for dev tunnel '{tunnelId}'. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelPortStatus> CreatePortAsync(string tunnelId, int portNumber, DevTunnelPortOptions portOptions, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        var exitCode = 0;
        string? error = null;
        DevTunnelPortStatus? port = null;

        while (attempts < _maxCliAttempts)
        {
            logger?.LogTrace("Creating port '{PortNumber}' on dev tunnel '{TunnelId}' with options: {Options}", portNumber, tunnelId, portOptions.ToLoggerString());
            if (attempts++ > 1)
            {
                logger?.LogTrace("Attempt {Attempt} of {MaxAttempts} to create port '{PortNumber}' on dev tunnel '{TunnelId}'", attempts, _maxCliAttempts, portNumber, tunnelId);
            }

            (port, exitCode, error) = await CallCliAsJsonAsync<DevTunnelPortStatus>(
                (outWriter, errWriter, log, ct) => _cli.CreatePortAsync(tunnelId, portNumber, portOptions, outWriter, errWriter, log, ct),
                logger, cancellationToken).ConfigureAwait(false);

            if (exitCode == 0 && port is not null)
            {
                if (portOptions.AllowAnonymous.HasValue)
                {
                    // AllowAnonymous=true: anonymous=true, deny=false
                    // AllowAnonymous=false: anonymous=true, deny=true
                    var anonymous = true;
                    var deny = !portOptions.AllowAnonymous.Value;
                    if (deny)
                    {
                        logger?.LogTrace("Denying anonymous access for port '{PortNumber}' on dev tunnel '{TunnelId}'.", portNumber, tunnelId);
                    }
                    else
                    {
                        logger?.LogTrace("Allowing anonymous access for port '{PortNumber}' on dev tunnel '{TunnelId}'.", portNumber, tunnelId);
                    }
                    (var result, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
                        (stdout, stderr, log, ct) => _cli.CreateAccessAsync(tunnelId, portNumber, anonymous, deny, stdout, stderr, log, ct),
                        logger, cancellationToken).ConfigureAwait(false);
                }
                if (exitCode == 0)
                {
                    logger?.LogTrace("Port '{PortNumber}' on dev tunnel '{TunnelId}' created successfully.", portNumber, tunnelId);
                    return port;
                }
            }
            else if (exitCode == DevTunnelCli.ResourceConflictsWithExistingExitCode)
            {
                logger?.LogTrace("Port '{PortNumber}' already exists on dev tunnel '{TunnelId}', deleting and trying again.", portNumber, tunnelId);
                (var deleteResult, exitCode, error) = await CallCliAsJsonAsync<DevTunnelDeleteResult>(
                    (stdout, stderr, log, ct) => _cli.DeletePortAsync(tunnelId, portNumber, stdout, stderr, log, ct),
                    logger, cancellationToken).ConfigureAwait(false);
                if (exitCode == 0)
                {
                    logger?.LogTrace("Deleted existing port '{PortNumber}' on dev tunnel '{TunnelId}'.", portNumber, tunnelId);
                    continue; // Retry create
                }
            }

            logger?.LogError("Failed to create port '{PortNumber}' for dev tunnel '{TunnelId}' (attempt {Attempt} of {MaxAttempts}). Exit code {ExitCode}: {Error}", portNumber, tunnelId, attempts, _maxCliAttempts, exitCode, error);
            if (attempts < _maxCliAttempts)
            {
                logger?.LogTrace("Waiting {WaitSeconds} seconds before retrying to create port '{PortNumber}' on dev tunnel '{TunnelId}'", _cliRetryOnErrorDelay.TotalSeconds, portNumber, tunnelId);
                await Task.Delay(_cliRetryOnErrorDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DistributedApplicationException($"Failed to create port '{portNumber}' for tunnel '{tunnelId}' after {attempts} attempts. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelPortDeleteResult> DeletePortAsync(string tunnelId, int portNumber, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Deleting port '{PortNumber}' on dev tunnel '{TunnelId}'.", portNumber, tunnelId);
        var (result, exitCode, error) = await CallCliAsJsonAsync<DevTunnelPortDeleteResult>(
            (stdout, stderr, log, ct) => _cli.DeletePortAsync(tunnelId, portNumber, stdout, stderr, log, ct),
            logger, cancellationToken).ConfigureAwait(false);
        return result ?? throw new DistributedApplicationException($"Failed to delete port '{portNumber}' on dev tunnel '{tunnelId}'. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelAccessStatus> GetAccessAsync(string tunnelId, int? portNumber = null, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Getting access details for {PortInfo}dev tunnel '{TunnelId}'.", portNumber.HasValue ? $"port '{portNumber}' on " : string.Empty, tunnelId);
        var (access, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
            (stdout, stderr, log, ct) => _cli.ListAccessAsync(tunnelId, portNumber, stdout, stderr, log, ct),
            logger, cancellationToken).ConfigureAwait(false);
        return access ?? throw new DistributedApplicationException($"Failed to get access details for '{tunnelId}'{(portNumber.HasValue ? $" port {portNumber}" : "")}. Exit code {exitCode}: {error}");
    }

    public async Task<UserLoginStatus> GetUserLoginStatusAsync(ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Getting dev tunnel user login status.");
        var (login, exitCode, error) = await CallCliAsJsonAsync<UserLoginStatus>(
            _cli.UserStatusAsync,
            logger, cancellationToken).ConfigureAwait(false);
        return login ?? throw new DistributedApplicationException($"Failed to get user login status. Exit code {exitCode}: {error}");
    }

    public async Task<UserLoginStatus> UserLoginAsync(LoginProvider provider, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Logging in to dev tunnel service using {LoginProvider}.", provider);
        var exitCode = provider switch
        {
            LoginProvider.Microsoft => await _cli.UserLoginMicrosoftAsync(logger, cancellationToken).ConfigureAwait(false),
            LoginProvider.GitHub => await _cli.UserLoginGitHubAsync(logger, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Unsupported provider. Supported providers are 'microsoft' and 'github'.", nameof(provider)),
        };

        if (exitCode == 0)
        {
            // Login succeeded, get the login status
            return await GetUserLoginStatusAsync(logger, cancellationToken).ConfigureAwait(false);
        }

        throw new DistributedApplicationException($"Failed to perform user login. Process finished with exit code: {exitCode}");
    }

    private async Task<(T? Result, int ExitCode, string? Error)> CallCliAsJsonAsync<T>(Func<TextWriter, TextWriter, ILogger?, CancellationToken, Task<int>> cliCall, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        return await CallCliAsJsonAsync<T>(cliCall, propertyName: null, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(T? Result, int ExitCode, string? Error)> CallCliAsJsonAsync<T>(Func<TextWriter, TextWriter, ILogger?, CancellationToken, Task<int>> cliCall, string? propertyName, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        // PERF: Could pool these writers
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await cliCall(stdout, stderr, logger, cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var error = stderr.ToString().Trim();
            logger?.LogError("CLI call returned non-zero exit code '{ExitCode}'. stderr output:\n{Error}", exitCode, error);
            return (default, exitCode, error);
        }

        var output = stdout.ToString().Trim();
        logger?.LogTrace("CLI call output:\n{Output}", output);

        if (cancellationToken.IsCancellationRequested)
        {
            logger?.LogDebug("Operation was cancelled.");
            return (default, exitCode, "Operation was cancelled.");
        }

        if (string.IsNullOrEmpty(output))
        {
            logger?.LogError("CLI call returned empty output with exit code '{ExitCode}'.", exitCode);
            return (default, exitCode, "CLI call returned empty output.");
        }

        try
        {
            if (!string.IsNullOrEmpty(propertyName))
            {
                output = JsonDocument.Parse(output).RootElement.GetProperty(propertyName).GetRawText();
                logger?.LogTrace("Extracted JSON property '{PropertyName}':\n{Output}", propertyName, output);
            }
            var result = JsonSerializer.Deserialize<T>(output, _jsonOptions);
            logger?.LogTrace("JSON output successfully deserialized to '{TypeName}' instance", typeof(T).Name);
            return (result, 0, default);
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "Failed to parse JSON output into type '{TypeName}':\n{Output}", typeof(T).Name, output);
            throw new DistributedApplicationException($"Failed to parse JSON output into type '{typeof(T).Name}':\n{output}", ex);
        }
    }

    private record DevTunnelDeleteResult(string DeletedTunnel);
}
