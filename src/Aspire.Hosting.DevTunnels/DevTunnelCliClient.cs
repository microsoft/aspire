// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelCliClient : IDevTunnelClient
{
    private readonly int _maxCliAttempts;
    private readonly TimeSpan _cliRetryOnErrorDelay = TimeSpan.FromSeconds(2);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly DevTunnelCli _cli;

    public DevTunnelCliClient(IConfiguration configuration)
        : this(configuration, new DevTunnelCli(DevTunnelCli.GetCliPath(configuration)))
    {
    }

    internal DevTunnelCliClient(IConfiguration configuration, DevTunnelCli cli)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cli);

        _maxCliAttempts = configuration.GetValue<int?>("ASPIRE_DEVTUNNEL_CLI_MAX_ATTEMPTS") ?? 3;
        _cli = cli;
    }

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

    public Task<DevTunnelStatus> CreateTunnelAsync(string tunnelId, DevTunnelOptions options, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RetryProvisioningAsync(() => CreateTunnelCoreAsync(tunnelId, options, logger, cancellationToken), logger, cancellationToken);

    private async Task<DevTunnelStatus> CreateTunnelCoreAsync(string tunnelId, DevTunnelOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelId);
        var resolvedId = options.Region is not null ? $"{tunnelId}.{options.RegionCode}" : tunnelId;
        var existing = await FindExistingAsync<DevTunnelStatus>(
            (stdout, stderr, log, ct) => _cli.ShowTunnelAsync(resolvedId, stdout, stderr, log, ct),
            "tunnel", $"dev tunnel '{resolvedId}'", t => ValidateTunnelIdentity(resolvedId, t.TunnelId), logger, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var access = existing.AccessControl;
            if (access is null)
            {
                access = (await GetAccessAsync(existing.TunnelId, portNumber: null, logger, cancellationToken).ConfigureAwait(false)).AccessControlEntries;
            }

            var descriptionMatches = string.IsNullOrEmpty(options.Description) || string.Equals(existing.Description, options.Description, StringComparison.Ordinal);
            var labelsMatch = options.Labels is null || options.Labels.All(l => (existing.Labels ?? []).Contains(l, StringComparer.Ordinal));
            var expirationMatches = options.ExpirationHours is null || GetExpirationHours(existing.TunnelExpiration) == options.ExpirationHours;
            if (!descriptionMatches || !labelsMatch || !expirationMatches)
            {
                var (updated, updateExitCode, updateError) = await CallCliAsJsonAsync<DevTunnelStatus>(
                    (stdout, stderr, log, ct) => _cli.UpdateTunnelAsync(existing.TunnelId, options, stdout, stderr, log, ct),
                    "tunnel", logger, cancellationToken).ConfigureAwait(false);
                if (updated is not null)
                {
                    ValidateTunnelIdentity(existing.TunnelId, updated.TunnelId);
                }
                existing = updated ?? throw new RetryableProvisioningException($"Failed to update dev tunnel '{existing.TunnelId}'. Exit code {updateExitCode}: {updateError}");
            }

            await EnsureAccessAsync(existing.TunnelId, portNumber: null, options.AllowAnonymous ? true : null, access, logger, cancellationToken).ConfigureAwait(false);
            logger?.LogDebug("Reusing dev tunnel '{TunnelId}'.", existing.TunnelId);
            return existing;
        }
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
                ValidateTunnelIdentity(resolvedId, tunnel.TunnelId);
                logger?.LogTrace("Dev tunnel '{TunnelId}' created successfully.", tunnelId);
                return tunnel;
            }

            if (exitCode == DevTunnelCli.ResourceConflictsWithExistingExitCode)
            {
                // Update the tunnel as it already exists
                logger?.LogTrace("Dev tunnel '{TunnelId}' already exists, will update it instead.", tunnelId);
                var createError = error;
                (tunnel, exitCode, error) = await CallCliAsJsonAsync<DevTunnelStatus>(
                    (stdout, stderr, log, ct) => _cli.UpdateTunnelAsync(resolvedTunnelId, options, stdout, stderr, log, ct),
                    "tunnel", logger, cancellationToken).ConfigureAwait(false);
                if (exitCode == DevTunnelCli.ResourceNotFoundExitCode)
                {
                    // A service ghost can produce:
                    //   create <id>: exit 1, "Conflict with existing entity"
                    //   update <id>: exit 2, "Tunnel not found"
                    // Retrying the same candidate cannot resolve that contradictory service state.
                    throw new DistributedApplicationException(
                        $"Dev tunnel '{resolvedTunnelId}' could not be created because the dev tunnels service reported that it already exists, " +
                        "but then reported it was not found when Aspire tried to update it. This tunnel ID is in an inconsistent service state " +
                        $"and retrying it cannot recover. Specify a different tunnel ID with {nameof(DevTunnelsResourceBuilderExtensions.AddDevTunnel)}" +
                        "(name, tunnelId: \"new-id\") and restart " +
                        $"the AppHost. Create error: '{createError}'. Update error: '{error}'.");
                }

                if (exitCode == 0 && tunnel is not null)
                {
                    ValidateTunnelIdentity(resolvedTunnelId, tunnel.TunnelId);
                    resolvedTunnelId = tunnel.TunnelId;
                    logger?.LogTrace("Dev tunnel '{TunnelId}' updated successfully.", resolvedTunnelId);

                    // Ensure tunnel access controls are set as specified in options by resetting existing policies first.
                    // Port-specific policies are reconciled separately.
                    logger?.LogTrace("Clearing access policies for dev tunnel '{TunnelId}'.", resolvedTunnelId);
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
        if (exitCode == DevTunnelCli.ResourceNotFoundExitCode)
        {
            throw new DevTunnelNotFoundException(tunnelId, error);
        }
        if (tunnel is not null)
        {
            ValidateTunnelIdentity(tunnelId, tunnel.TunnelId);
        }
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

    public Task<DevTunnelPortStatus> CreatePortAsync(string tunnelId, int portNumber, DevTunnelPortOptions portOptions, ILogger? logger = default, CancellationToken cancellationToken = default)
        => RetryProvisioningAsync(() => CreatePortCoreAsync(tunnelId, portNumber, portOptions, logger, cancellationToken), logger, cancellationToken);

    private async Task<DevTunnelPortStatus> CreatePortCoreAsync(string tunnelId, int portNumber, DevTunnelPortOptions portOptions, ILogger? logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelId);
        var existing = await FindExistingAsync<DevTunnelPortStatus>(
            (stdout, stderr, log, ct) => _cli.ShowPortAsync(tunnelId, portNumber, stdout, stderr, log, ct),
            "port", $"port '{portNumber}' on dev tunnel '{tunnelId}'", p => ValidatePortIdentity(tunnelId, portNumber, p), logger, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            tunnelId = existing.TunnelId;
            var protocolMatches = string.Equals(existing.Protocol, portOptions.Protocol ?? "auto", StringComparison.OrdinalIgnoreCase);
            var descriptionMatches = string.IsNullOrEmpty(portOptions.Description) || string.Equals(existing.Description, portOptions.Description, StringComparison.Ordinal);
            var labelsMatch = existing.Labels.ToHashSet(StringComparer.Ordinal).SetEquals(portOptions.Labels ?? []);
            if (protocolMatches && descriptionMatches && labelsMatch)
            {
                var access = existing.AccessControl;
                if (access is null)
                {
                    access = (await GetAccessAsync(tunnelId, portNumber, logger, cancellationToken).ConfigureAwait(false)).AccessControlEntries;
                }
                await EnsureAccessAsync(tunnelId, portNumber, portOptions.AllowAnonymous, access, logger, cancellationToken).ConfigureAwait(false);
                logger?.LogDebug("Reusing dev tunnel port '{PortNumber}' on '{TunnelId}'.", portNumber, tunnelId);
                return existing;
            }

            // Protocol cannot be changed by `devtunnel port update`. Recreate only ports whose
            // modeled configuration changed, preserving unchanged port URLs and access policies.
            await DeletePortAsync(tunnelId, portNumber, logger, cancellationToken).ConfigureAwait(false);
        }
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
                "port", logger, cancellationToken).ConfigureAwait(false);

            if (exitCode == 0 && port is not null)
            {
                ValidatePortIdentity(tunnelId, portNumber, port);
                // Creation alone does not establish the requested policy. A missing or failed
                // access result must retry reconciliation of this port, not recreate it or report success.
                await EnsureAccessAsync(port.TunnelId, portNumber, portOptions.AllowAnonymous, port.AccessControl ?? [], logger, cancellationToken).ConfigureAwait(false);
                logger?.LogTrace("Port '{PortNumber}' on dev tunnel '{TunnelId}' created successfully.", portNumber, port.TunnelId);
                return port;
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

    private async Task<T> RetryProvisioningAsync<T>(Func<Task<T>> operation, ILogger? logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (RetryableProvisioningException ex) when (attempt < _maxCliAttempts)
            {
                // A failed mutation may still have reached the service. Inspect again on retry
                // instead of blindly repeating resets or adding duplicate access policies.
                logger?.LogWarning(ex, "Dev tunnel provisioning failed (attempt {Attempt} of {MaxAttempts}); reconciling again.", attempt, _maxCliAttempts);
                await Task.Delay(_cliRetryOnErrorDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<T?> FindExistingAsync<T>(
        Func<TextWriter, TextWriter, ILogger?, CancellationToken, Task<int>> query,
        string propertyName,
        string description,
        Action<T> validate,
        ILogger? logger,
        CancellationToken cancellationToken) where T : class
    {
        int exitCode = 0;
        string? error = null;
        for (var attempt = 1; attempt <= _maxCliAttempts; attempt++)
        {
            var response = await CallCliAsJsonAsync<T>(query, propertyName, logger, cancellationToken).ConfigureAwait(false);
            (var result, exitCode, error) = response;
            if (result is not null)
            {
                validate(result);
                return result;
            }
            if (exitCode == DevTunnelCli.ResourceNotFoundExitCode)
            {
                return result;
            }
            if (attempt < _maxCliAttempts)
            {
                logger?.LogWarning("Failed to inspect {Resource} (attempt {Attempt} of {MaxAttempts}); retrying.", description, attempt, _maxCliAttempts);
                await Task.Delay(_cliRetryOnErrorDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new DistributedApplicationException($"Failed to inspect {description}. Exit code {exitCode}: {error}");
    }

    private static void ValidateTunnelIdentity(string requestedId, string returnedId)
    {
        if (string.Equals(requestedId, returnedId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A bare ID such as "mytunnel" may resolve to "mytunnel.usw2". A qualified request
        // must remain in that exact cluster; a shared prefix or another region is not a match.
        if (!requestedId.Contains('.')
            && returnedId.StartsWith(requestedId + ".", StringComparison.OrdinalIgnoreCase)
            && returnedId.Length > requestedId.Length + 1
            && returnedId.AsSpan(requestedId.Length + 1).IndexOf('.') < 0
            && returnedId.Skip(requestedId.Length + 1).All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return;
        }

        throw new DistributedApplicationException($"The devtunnel CLI returned tunnel '{returnedId}' when '{requestedId}' was requested.");
    }

    private static void ValidatePortIdentity(string tunnelId, int portNumber, DevTunnelPortStatus port)
    {
        ValidateTunnelIdentity(tunnelId, port.TunnelId);
        if (port.PortNumber != portNumber)
        {
            throw new DistributedApplicationException($"The devtunnel CLI returned port '{port.PortNumber}' when port '{portNumber}' on tunnel '{tunnelId}' was requested.");
        }
    }

    private async Task EnsureAccessAsync(
        string tunnelId,
        int? portNumber,
        bool? allowAnonymous,
        IReadOnlyList<DevTunnelAccessStatus.AccessControlEntry> access,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        // Inherited entries belong to the parent tunnel. A port with no explicit policy must
        // inherit them, whereas AllowAnonymous=false requires an explicit anonymous deny.
        var explicitEntries = access.Where(e => !e.IsInherited).ToArray();
        var matches = allowAnonymous is null
            ? explicitEntries.Length == 0
            : explicitEntries is [var entry]
                && string.Equals(entry.Type, "Anonymous", StringComparison.OrdinalIgnoreCase)
                && entry.IsDeny == !allowAnonymous.Value
                && entry.Subjects.Count == 0
                && entry.Scopes is [var scope]
                && string.Equals(scope, "connect", StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            return;
        }

        if (explicitEntries.Length > 0)
        {
            var (reset, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
                (stdout, stderr, log, ct) => _cli.ResetAccessAsync(tunnelId, portNumber, stdout, stderr, log, ct),
                logger, cancellationToken).ConfigureAwait(false);
            if (reset is null)
            {
                throw new RetryableProvisioningException($"Failed to reset access for dev tunnel '{tunnelId}', port '{portNumber}'. Exit code {exitCode}: {error}");
            }
        }
        if (allowAnonymous is { } allow)
        {
            var (created, createExitCode, createError) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
                (stdout, stderr, log, ct) => _cli.CreateAccessAsync(tunnelId, portNumber, anonymous: true, deny: !allow, stdout, stderr, log, ct),
                logger, cancellationToken).ConfigureAwait(false);
            if (created is null)
            {
                throw new RetryableProvisioningException($"Failed to set access for dev tunnel '{tunnelId}', port '{portNumber}'. Exit code {createExitCode}: {createError}");
            }
        }
    }

    private static decimal? GetExpirationHours(string? expiration)
    {
        // The CLI's JSON uses display strings, such as "1 hours", "30 days", or a combination
        // of days and hours. An unfamiliar representation is not evidence of a match: reapply
        // the requested expiration instead of silently keeping a potentially different value.
        if (expiration is null)
        {
            return null;
        }
        var parts = expiration.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length % 2 != 0)
        {
            return null;
        }
        var hours = 0m;
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (!decimal.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 720)
            {
                return null;
            }
            var multiplier = parts[i + 1].ToLowerInvariant() switch
            {
                "hour" or "hours" => 1,
                "day" or "days" => 24,
                _ => 0
            };
            if (multiplier == 0)
            {
                return null;
            }
            hours += value * multiplier;
        }
        return hours;
    }

    public async Task<DevTunnelPortDeleteResult> DeletePortAsync(string tunnelId, int portNumber, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Deleting port '{PortNumber}' on dev tunnel '{TunnelId}'.", portNumber, tunnelId);
        var (result, exitCode, error) = await CallCliAsJsonAsync<DevTunnelPortDeleteResult>(
            (stdout, stderr, log, ct) => _cli.DeletePortAsync(tunnelId, portNumber, stdout, stderr, log, ct),
            logger, cancellationToken).ConfigureAwait(false);
        return result ?? throw new RetryableProvisioningException($"Failed to delete port '{portNumber}' on dev tunnel '{tunnelId}'. Exit code {exitCode}: {error}");
    }

    public async Task<DevTunnelAccessStatus> GetAccessAsync(string tunnelId, int? portNumber = null, ILogger? logger = default, CancellationToken cancellationToken = default)
    {
        logger?.LogTrace("Getting access details for {PortInfo}dev tunnel '{TunnelId}'.", portNumber.HasValue ? $"port '{portNumber}' on " : string.Empty, tunnelId);
        var (access, exitCode, error) = await CallCliAsJsonAsync<DevTunnelAccessStatus>(
            (stdout, stderr, log, ct) => _cli.ListAccessAsync(tunnelId, portNumber, stdout, stderr, log, ct),
            logger, cancellationToken).ConfigureAwait(false);
        return access ?? throw new RetryableProvisioningException($"Failed to get access details for '{tunnelId}'{(portNumber.HasValue ? $" port {portNumber}" : "")}. Exit code {exitCode}: {error}");
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
            cancellationToken.ThrowIfCancellationRequested();
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
                // For example, update returns {"tunnel":{"tunnelId":"name.usw2",...}} and
                // port create/show return {"port":{...}}. Also accept a flat response, but
                // validate its identity below so an envelope can never deserialize to a null ID.
                using var document = JsonDocument.Parse(output);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("The devtunnel response must be a JSON object.");
                }
                if (document.RootElement.TryGetProperty(propertyName, out var value))
                {
                    output = value.GetRawText();
                    logger?.LogTrace("Extracted JSON property '{PropertyName}':\n{Output}", propertyName, output);
                }
            }
            var result = JsonSerializer.Deserialize<T>(output, _jsonOptions);
            if (result is DevTunnelStatus tunnel && string.IsNullOrWhiteSpace(tunnel.TunnelId)
                || result is DevTunnelPortStatus port && (string.IsNullOrWhiteSpace(port.TunnelId) || port.PortNumber is < 1 or > 65535 || string.IsNullOrWhiteSpace(port.Protocol))
                || result is DevTunnelAccessStatus { AccessControlEntries: null })
            {
                throw new JsonException("The devtunnel response is missing a valid tunnel or port identity.");
            }
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

    private sealed class RetryableProvisioningException(string message) : DistributedApplicationException(message);
}
