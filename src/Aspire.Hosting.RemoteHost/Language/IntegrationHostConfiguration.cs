// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Snapshots and validates integration settings once for the server session.
/// </summary>
internal sealed class IntegrationHostConfiguration
{
    internal static IntegrationHostConfiguration Default { get; } = new(new ConfigurationBuilder().Build());
    private readonly Lazy<IReadOnlyList<IntegrationHostDescriptor>> _descriptors;

    public IntegrationHostConfiguration(IConfiguration configuration)
    {
        Enabled = configuration.GetValue<bool>(KnownConfigNames.IntegrationHostsEnabled);
        Bootstrap = configuration.GetValue<bool>(KnownConfigNames.IntegrationHostBootstrap);
        SocketPath = configuration["REMOTE_APP_HOST_SOCKET_PATH"];
        Token = configuration[KnownConfigNames.RemoteAppHostToken];
        RegistrationTimeout = ReadDuration(configuration, nameof(RegistrationTimeout), TimeSpan.FromMinutes(2));
        DiscoveryTimeout = ReadDuration(configuration, nameof(DiscoveryTimeout), TimeSpan.FromMinutes(2));
        InvocationTimeout = ReadDuration(configuration, nameof(InvocationTimeout), TimeSpan.FromMinutes(1));
        // Guest callbacks can await integration calls. Lowering the integration deadline
        // must not also retire the guest before the integration can recover.
        CallbackTimeout = ReadDuration(configuration, nameof(CallbackTimeout), TimeSpan.FromMinutes(1));
        ShutdownTimeout = ReadDuration(configuration, nameof(ShutdownTimeout), TimeSpan.FromSeconds(5));
        OutputDrainIdleTimeout = ReadDuration(configuration, nameof(OutputDrainIdleTimeout), TimeSpan.FromSeconds(5));
        StableHostPeriod = ReadDuration(configuration, nameof(StableHostPeriod), TimeSpan.FromMinutes(1));
        RestartDelay = ReadDuration(configuration, nameof(RestartDelay), TimeSpan.FromSeconds(1));
        MaxRestartDelay = ReadDuration(configuration, nameof(MaxRestartDelay), TimeSpan.FromSeconds(4));
        MaxRestartAttempts = configuration.GetValue("IntegrationHost:MaxRestartAttempts", 3);
        if (MaxRestartAttempts < 0)
        {
            throw new InvalidOperationException("IntegrationHost:MaxRestartAttempts must be non-negative.");
        }
        if (RestartDelay > MaxRestartDelay)
        {
            throw new InvalidOperationException("IntegrationHost:RestartDelay must not exceed IntegrationHost:MaxRestartDelay.");
        }

        var entries = configuration.GetSection("IntegrationHosts").GetChildren()
            .Select(entry => (entry.Path, Language: entry["Language"], PackageName: entry["PackageName"], HostEntryPoint: entry["HostEntryPoint"]))
            .ToArray();
        // Descriptor errors belong to hosted-service startup so readiness faults with the
        // same cause. Bootstrap intentionally runs before generated host modules exist.
        _descriptors = new(() =>
        {
            if (entries.Length > 0 && !Enabled)
            {
                throw new InvalidOperationException($"Integration hosts require {KnownConfigNames.IntegrationHostsEnabled}=true.");
            }
            if (Bootstrap)
            {
                return [];
            }

            return entries.Select(entry =>
            {
                if (string.IsNullOrEmpty(entry.Language) || string.IsNullOrEmpty(entry.PackageName) || string.IsNullOrEmpty(entry.HostEntryPoint))
                {
                    throw new InvalidOperationException(
                        $"Malformed {entry.Path} entry: Language='{entry.Language}', PackageName='{entry.PackageName}', HostEntryPoint='{entry.HostEntryPoint}'. " +
                        "All three fields are required.");
                }
                if (!File.Exists(entry.HostEntryPoint))
                {
                    throw new FileNotFoundException(
                        $"Integration host entry point for package '{entry.PackageName}' [{entry.Language}] does not exist. Verify the integration path.",
                        entry.HostEntryPoint);
                }

                return new IntegrationHostDescriptor
                {
                    Language = entry.Language,
                    PackageName = entry.PackageName,
                    HostEntryPoint = entry.HostEntryPoint
                };
            }).ToArray();
        });
    }

    public bool Enabled { get; }
    public bool Bootstrap { get; }
    public string? SocketPath { get; }
    public string? Token { get; }
    public TimeSpan RegistrationTimeout { get; }
    public TimeSpan DiscoveryTimeout { get; }
    public TimeSpan InvocationTimeout { get; }
    public TimeSpan CallbackTimeout { get; }
    public TimeSpan ShutdownTimeout { get; }
    public TimeSpan OutputDrainIdleTimeout { get; }
    public TimeSpan StableHostPeriod { get; }
    public TimeSpan RestartDelay { get; }
    public TimeSpan MaxRestartDelay { get; }
    public int MaxRestartAttempts { get; }
    public IReadOnlyList<IntegrationHostDescriptor> Descriptors => _descriptors.Value;

    public TimeSpan GetRestartDelay(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(RestartDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxRestartDelay.TotalMilliseconds));

    private static TimeSpan ReadDuration(IConfiguration configuration, string name, TimeSpan defaultValue)
    {
        var key = $"IntegrationHost:{name}";
        var value = configuration.GetValue(key, defaultValue);
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException(
                $"{key} must be a positive TimeSpan no greater than {TimeSpan.FromMilliseconds(uint.MaxValue - 1)}.");
        }

        return value;
    }
}

/// <summary>
/// Identifies an integration host configured by the AppHost server.
/// </summary>
internal sealed class IntegrationHostDescriptor
{
    public required string Language { get; init; }
    public required string PackageName { get; init; }
    public required string HostEntryPoint { get; init; }
}
