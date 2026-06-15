// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

internal sealed class DcpOptions
{
    /// <summary>
    /// The path to the DCP executable used for Aspire orchestration
    /// </summary>
    /// <example>
    /// C:\Program Files\dotnet\packs\Aspire.Hosting.Orchestration.win-x64\8.0.0-preview.1.23518.6\tools\dcp.exe
    /// </example>
    public string? CliPath { get; set; }

    /// <summary>
    /// Optional path to a folder containing the DCP extension assemblies.
    /// </summary>
    /// <example>
    /// C:\Program Files\dotnet\packs\Aspire.Hosting.Orchestration.win-x64\8.0.0-preview.1.23518.6\tools\ext\
    /// </example>
    public string? ExtensionsPath { get; set; }

    /// <summary>
    /// Optional path to a folder containing the Aspire Dashboard binaries.
    /// </summary>
    /// <example>
    /// When running the playground applications in this repo: <c>..\..\..\artifacts\bin\Aspire.Dashboard\Debug\net8.0\Aspire.Dashboard.dll</c>
    /// </example>
    public string? DashboardPath { get; set; }

    /// <summary>
    /// Optional path to the Aspire Terminal Host binary.
    /// </summary>
    public string? TerminalHostPath { get; set; }

    /// <summary>
    /// Optional invocation args that must be prepended when launching <see cref="TerminalHostPath"/>.
    /// In the CLI bundle case the path is the multi-mode <c>aspire-managed</c> exe and this is set
    /// to <c>"terminalhost"</c> so the dispatcher routes to <c>TerminalHostApp.RunAsync</c>.
    /// Empty for the standalone per-RID NuGet package and inner-loop cases.
    /// </summary>
    public string? TerminalHostInvocationArgs { get; set; }

    /// <summary>
    /// Optional container runtime to override default runtime for DCP containers.
    /// </summary>
    /// <example>
    /// podman
    /// </example>
    public string? ContainerRuntime { get; set; }

    /// <summary>
    /// How long the dependency check will wait (in seconds) for a response before timing out.
    /// Timeout is disabled if set to zero or a negative value.
    /// </summary>
    public int DependencyCheckTimeout { get; set; } = 25;

    /// <summary>
    /// The suffix to use for resource names when creating resources in DCP.
    /// </summary>
    public string? ResourceNameSuffix { get; set; }

    /// <summary>
    /// Whether to randomize ports used by resources during orchestration.
    /// </summary>
    public bool RandomizePorts { get; set; }

    /// <summary>
    /// The first port in the range used to allocate unspecified public ports for proxyless endpoints.
    /// </summary>
    public int ProxylessEndpointPortRangeStart { get; set; } = 10000;

    /// <summary>
    /// The last port in the range used to allocate unspecified public ports for proxyless endpoints.
    /// </summary>
    /// <remarks>
    /// The default leaves room for Aspire to persist stable allocated ports in the future while staying
    /// compatible across supported OSes. Linux's default ephemeral range starts at 32768, which is the
    /// most restrictive default among those OSes, so default allocations stop one port lower.
    /// </remarks>
    public int ProxylessEndpointPortRangeEnd { get; set; } = 32767;

    public int KubernetesConfigReadRetryCount { get; set; } = 300;

    public int KubernetesConfigReadRetryIntervalMilliseconds { get; set; } = 100;

    /// <summary>
    /// The duration to wait for the container runtime to become healthy before aborting startup.
    /// </summary>
    /// <remarks>
    /// A value of zero, which is the default value, indicates that the application will not wait for the container
    /// runtime to become healthy.
    /// If this property has a value greater than zero, the application will abort startup if the container runtime
    /// does not become healthy within the specified timeout.
    /// </remarks>
    public TimeSpan ContainerRuntimeInitializationTimeout { get; set; }

    public TimeSpan ServiceStartupWatchTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to wait for resource cleanup to end when stopping DcpExecutor.
    /// This guarantees that application resources (programs, transient containers etc.) are stopped
    /// before DcpExecutor.StopAsync() returns. Default is false (resources are cleaned up asynchronously).
    /// </summary>
    public bool WaitForResourceCleanup { get; set; }

    /// <summary>
    /// Gets or sets the suffix to use for DCP log file names (applicable when verbose DCP logging is enabled).
    /// By default log file name suffix defaults to the current process ID.
    /// </summary>
    public string? LogFileNameSuffix { get; set; }

    /// <summary>
    /// Gets or sets the folder path where DCP diagnostics logs are written.
    /// If set, overrides the DCP_DIAGNOSTICS_LOG_FOLDER environment variable.
    /// </summary>
    public string? DiagnosticsLogFolder { get; set; }

    /// <summary>
    /// Gets or sets the DCP diagnostics log level.
    /// If set, overrides the DCP_DIAGNOSTICS_LOG_LEVEL environment variable.
    /// </summary>
    public string? DiagnosticsLogLevel { get; set; }

    /// <summary>
    /// Gets or sets whether DCP should preserve executable logs.
    /// If set to true, overrides the DCP_PRESERVE_EXECUTABLE_LOGS environment variable.
    /// </summary>
    public bool? PreserveExecutableLogs { get; set; }

    /// <summary>
    /// Enables Aspire container tunnel for container-to-host connectivity across all container orchestrators.
    /// </summary>
    public bool EnableAspireContainerTunnel { get; set; } = true;
}

internal class ValidateDcpOptions : IValidateOptions<DcpOptions>
{
    public ValidateOptionsResult Validate(string? name, DcpOptions options)
    {
        var builder = new ValidateOptionsResultBuilder();

        if (string.IsNullOrWhiteSpace(options.CliPath))
        {
            builder.AddError("The path to the DCP executable used for Aspire orchestration is required.", "CliPath");
        }

        if (string.IsNullOrWhiteSpace(options.DashboardPath))
        {
            builder.AddError("The path to the Aspire Dashboard binaries is missing.", "DashboardPath");
        }

        if (!PortRange.IsValidPort(options.ProxylessEndpointPortRangeStart))
        {
            builder.AddError($"The proxyless endpoint port range start must be between {PortRange.MinPort} and {PortRange.MaxPort}.", nameof(options.ProxylessEndpointPortRangeStart));
        }

        if (!PortRange.IsValidPort(options.ProxylessEndpointPortRangeEnd))
        {
            builder.AddError($"The proxyless endpoint port range end must be between {PortRange.MinPort} and {PortRange.MaxPort}.", nameof(options.ProxylessEndpointPortRangeEnd));
        }

        if (options.ProxylessEndpointPortRangeStart > options.ProxylessEndpointPortRangeEnd)
        {
            builder.AddError("The proxyless endpoint port range start must be less than or equal to the range end.", nameof(options.ProxylessEndpointPortRangeStart));
        }

        return builder.Build();
    }
}

internal class ConfigureDefaultDcpOptions(
    DistributedApplicationOptions appOptions,
    IConfiguration configuration) : IConfigureOptions<DcpOptions>
{
    private const string DcpCliPathMetadataKey = "DcpCliPath";
    private const string DcpExtensionsPathMetadataKey = "DcpExtensionsPath";
    private const string DashboardPathMetadataKey = "aspiredashboardpath";
    private const string TerminalHostPathMetadataKey = "aspireterminalhostpath";
    private const string TerminalHostInvocationArgsMetadataKey = "aspireterminalhostinvocationargs";

    public static string DcpPublisher = nameof(DcpPublisher);

    public void Configure(DcpOptions options)
    {
        var dcpPublisherConfiguration = configuration.GetSection(DcpPublisher);
        var assemblyMetadata = appOptions.Assembly?.GetCustomAttributes<AssemblyMetadataAttribute>();

        // Priority 1: Check explicit DcpPublisher configuration first (env vars are automatically bound via IConfiguration)
        // Priority 2: BundleDiscovery env vars: ASPIRE_DCP_PATH, ASPIRE_DASHBOARD_PATH
        var configDcpPath = configuration[BundleDiscovery.DcpPathEnvVar];
        var configDashboardPath = configuration[BundleDiscovery.DashboardPathEnvVar];

        if (!string.IsNullOrWhiteSpace(dcpPublisherConfiguration[nameof(options.CliPath)]))
        {
            // If an explicit path to DCP was provided from configuration
            options.CliPath = dcpPublisherConfiguration[nameof(options.CliPath)];
            if (Path.GetDirectoryName(options.CliPath) is string dcpDir && !string.IsNullOrEmpty(dcpDir))
            {
                options.ExtensionsPath = Path.Combine(dcpDir, "ext");
            }
        }
        else if (!string.IsNullOrWhiteSpace(configDcpPath))
        {
            // Configuration/environment variable override - set DCP paths from bundle
            options.CliPath = BundleDiscovery.GetDcpExecutablePath(configDcpPath);
            options.ExtensionsPath = Path.Combine(configDcpPath, "ext");
        }
        else
        {
            // Resolve via assembly metadata attributes (NuGet packages)
            options.CliPath = GetMetadataValue(assemblyMetadata, DcpCliPathMetadataKey);
            options.ExtensionsPath = GetMetadataValue(assemblyMetadata, DcpExtensionsPathMetadataKey);
        }

        if (!string.IsNullOrWhiteSpace(dcpPublisherConfiguration[nameof(options.DashboardPath)]))
        {
            // If an explicit path to Dashboard was provided from configuration
            options.DashboardPath = dcpPublisherConfiguration[nameof(options.DashboardPath)];
        }
        else if (!string.IsNullOrWhiteSpace(configDashboardPath))
        {
            // Configuration/environment variable override - set Dashboard path from bundle
            options.DashboardPath = configDashboardPath;
        }
        else
        {
            // Resolve via assembly metadata attributes (NuGet packages)
            options.DashboardPath = GetMetadataValue(assemblyMetadata, DashboardPathMetadataKey);
        }

        // Terminal Host path resolution (same pattern as Dashboard)
        var configTerminalHostPath = configuration[BundleDiscovery.TerminalHostPathEnvVar];
        if (!string.IsNullOrEmpty(configTerminalHostPath))
        {
            options.TerminalHostPath = configTerminalHostPath;
        }
        else if (!string.IsNullOrEmpty(dcpPublisherConfiguration[nameof(options.TerminalHostPath)]))
        {
            options.TerminalHostPath = dcpPublisherConfiguration[nameof(options.TerminalHostPath)];
        }
        else
        {
            options.TerminalHostPath = GetMetadataValue(assemblyMetadata, TerminalHostPathMetadataKey);
        }

        // Terminal Host invocation args (used when the binary is the multi-mode aspire-managed exe in the bundle).
        var configTerminalHostInvocationArgs = configuration[BundleDiscovery.TerminalHostInvocationArgsEnvVar];
        if (!string.IsNullOrEmpty(configTerminalHostInvocationArgs))
        {
            options.TerminalHostInvocationArgs = configTerminalHostInvocationArgs;
        }
        else if (!string.IsNullOrEmpty(dcpPublisherConfiguration[nameof(options.TerminalHostInvocationArgs)]))
        {
            options.TerminalHostInvocationArgs = dcpPublisherConfiguration[nameof(options.TerminalHostInvocationArgs)];
        }
        else
        {
            options.TerminalHostInvocationArgs = GetMetadataValue(assemblyMetadata, TerminalHostInvocationArgsMetadataKey);
        }

        // Discovery order for the terminal host binary:
        //
        // 1. ASPIRE_TERMINAL_HOST_PATH environment variable (manual override, e.g.
        //    side-loading a custom build for development).
        // 2. dcpPublisherConfiguration[TerminalHostPath] (programmatic override, e.g.
        //    tests or in-proc hosts.)
        // 3. Assembly metadata "aspireterminalhostpath" baked into the AppHost at
        //    build time by ResolveAspireCliBundle (the SetTerminalHostDiscoveryAttributes
        //    MSBuild target). This is the **primary** path in the normal `dotnet build`
        //    → `dotnet run` case — by the time the AppHost runs, the metadata already
        //    points at the bundled aspire-managed binary.
        // 4. Runtime inference from DashboardPath (below): only fires when none of the
        //    above produced a value, which happens when the AppHost was built on a
        //    machine where ResolveAspireCliBundle could not locate the bundle, but at
        //    runtime the launching CLI did set ASPIRE_DASHBOARD_PATH. Since 13.4 the
        //    bundle ships a single multi-mode aspire-managed exe that dispatches to
        //    dashboard / terminalhost via a leading subcommand arg, so reusing
        //    DashboardPath as the terminal host (with "terminalhost" as the dispatch
        //    arg) is correct.
        //
        // Note: if both the dashboard and terminal host paths end up empty, .WithTerminal()
        // resources will fail at start time; see TerminalHostFailureDiagnosticService for
        // the user-facing recovery (unhide the failed host, inject an actionable log line).
        if (string.IsNullOrEmpty(options.TerminalHostPath) &&
            !string.IsNullOrEmpty(options.DashboardPath) &&
            BundleDiscovery.IsAspireManagedBinary(options.DashboardPath))
        {
            options.TerminalHostPath = options.DashboardPath;

            if (string.IsNullOrEmpty(options.TerminalHostInvocationArgs))
            {
                options.TerminalHostInvocationArgs = "terminalhost";
            }
        }

        if (!string.IsNullOrEmpty(dcpPublisherConfiguration[nameof(options.ContainerRuntime)]))
        {
            options.ContainerRuntime = dcpPublisherConfiguration[nameof(options.ContainerRuntime)];
        }
        else
        {
            options.ContainerRuntime = configuration.GetString(KnownConfigNames.ContainerRuntime, KnownConfigNames.Legacy.ContainerRuntime);
        }

        if (!string.IsNullOrEmpty(dcpPublisherConfiguration[nameof(options.DependencyCheckTimeout)]))
        {
            if (int.TryParse(dcpPublisherConfiguration[nameof(options.DependencyCheckTimeout)], out var timeout))
            {
                options.DependencyCheckTimeout = timeout;
            }
            else
            {
                throw new InvalidOperationException($"Invalid value \"{dcpPublisherConfiguration[nameof(options.DependencyCheckTimeout)]}\" for \"--dcp-dependency-check-timeout\". Expected an integer value.");
            }
        }
        else
        {
            options.DependencyCheckTimeout = configuration.GetValue(KnownConfigNames.DependencyCheckTimeout, KnownConfigNames.Legacy.DependencyCheckTimeout, options.DependencyCheckTimeout);
        }

        options.KubernetesConfigReadRetryCount = dcpPublisherConfiguration.GetValue(nameof(options.KubernetesConfigReadRetryCount), options.KubernetesConfigReadRetryCount);
        options.KubernetesConfigReadRetryIntervalMilliseconds = dcpPublisherConfiguration.GetValue(nameof(options.KubernetesConfigReadRetryIntervalMilliseconds), options.KubernetesConfigReadRetryIntervalMilliseconds);

        if (!string.IsNullOrEmpty(dcpPublisherConfiguration[nameof(options.ResourceNameSuffix)]))
        {
            options.ResourceNameSuffix = dcpPublisherConfiguration[nameof(options.ResourceNameSuffix)];
        }

        options.RandomizePorts = dcpPublisherConfiguration.GetValue(nameof(options.RandomizePorts), options.RandomizePorts);
        options.ProxylessEndpointPortRangeStart = dcpPublisherConfiguration.GetValue(nameof(options.ProxylessEndpointPortRangeStart), options.ProxylessEndpointPortRangeStart);
        options.ProxylessEndpointPortRangeEnd = dcpPublisherConfiguration.GetValue(nameof(options.ProxylessEndpointPortRangeEnd), options.ProxylessEndpointPortRangeEnd);
        ApplyProxylessEndpointPortRangeOverride(options, configuration);
        options.WaitForResourceCleanup = dcpPublisherConfiguration.GetValue(nameof(options.WaitForResourceCleanup), options.WaitForResourceCleanup);
        options.ServiceStartupWatchTimeout = configuration.GetValue(KnownConfigNames.ServiceStartupWatchTimeout, KnownConfigNames.Legacy.ServiceStartupWatchTimeout, options.ServiceStartupWatchTimeout);
        options.ContainerRuntimeInitializationTimeout = dcpPublisherConfiguration.GetValue(nameof(options.ContainerRuntimeInitializationTimeout), options.ContainerRuntimeInitializationTimeout);
        options.LogFileNameSuffix = dcpPublisherConfiguration[nameof(options.LogFileNameSuffix)];
        options.DiagnosticsLogFolder = dcpPublisherConfiguration[nameof(options.DiagnosticsLogFolder)];
        options.DiagnosticsLogLevel = dcpPublisherConfiguration[nameof(options.DiagnosticsLogLevel)];
        options.PreserveExecutableLogs = dcpPublisherConfiguration.GetValue<bool?>(nameof(options.PreserveExecutableLogs), options.PreserveExecutableLogs);
        options.EnableAspireContainerTunnel = configuration.GetValue(KnownConfigNames.EnableContainerTunnel, options.EnableAspireContainerTunnel);
    }

    private static void ApplyProxylessEndpointPortRangeOverride(DcpOptions options, IConfiguration configuration)
    {
        if (configuration[KnownConfigNames.ProxylessEndpointPortRange] is not { Length: > 0 } configuredRange)
        {
            return;
        }

        var separatorIndex = configuredRange.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex != configuredRange.LastIndexOf('-'))
        {
            ThrowInvalidProxylessEndpointPortRange(configuredRange);
        }

        var startText = configuredRange[..separatorIndex].Trim();
        var endText = configuredRange[(separatorIndex + 1)..].Trim();
        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            ThrowInvalidProxylessEndpointPortRange(configuredRange);
        }

        if (!int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
        {
            ThrowInvalidProxylessEndpointPortRange(configuredRange);
        }

        options.ProxylessEndpointPortRangeStart = start;
        options.ProxylessEndpointPortRangeEnd = end;
    }

    [DoesNotReturn]
    private static void ThrowInvalidProxylessEndpointPortRange(string configuredRange)
    {
        throw new InvalidOperationException(
            $"Invalid value \"{configuredRange}\" for \"{KnownConfigNames.ProxylessEndpointPortRange}\". Expected a port range formatted as \"start-end\", for example \"10000-32767\".");
    }

    private static string? GetMetadataValue(IEnumerable<AssemblyMetadataAttribute>? assemblyMetadata, string key)
    {
        return assemblyMetadata?.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
