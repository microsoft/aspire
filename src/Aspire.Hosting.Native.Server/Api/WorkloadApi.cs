// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

internal sealed partial class ResourceExecution
{
    /// <summary>Starts a container after server-side symbolic environment and argument resolution.</summary>
    /// <param name="revision">The current desired resource configuration revision.</param>
    /// <param name="networkContext">Must be container; endpoint references fail until container-network addressing exists.</param>
    /// <param name="launch">Literal workload metadata and unbound launch slots.</param>
    /// <param name="bindings">Mappings from environment/argument slots to this resource's symbolic configuration keys.</param>
    /// <returns>The actual server-owned workload allocation, without resolved credentials.</returns>
    [AspireExport]
    public async Task<WorkloadAllocation> StartContainerWithValues(long revision, string networkContext,
        ContainerWorkloadLaunch launch, SymbolicLaunchBindings bindings) =>
        CopyAllocation(await owner.StartSymbolicWorkload(revision, CreateContainerPlan(launch),
            ParseNetworkContext(networkContext), CopyBindings(bindings)).ConfigureAwait(false));

    /// <summary>Starts a host executable with references resolved only after its dependencies are ready.</summary>
    /// <param name="revision">The current desired resource configuration revision.</param>
    /// <param name="networkContext">Must be host for an executable workload.</param>
    /// <param name="launch">Literal workload metadata and unbound launch slots.</param>
    /// <param name="bindings">Mappings to this resource's server-owned symbolic configuration.</param>
    /// <returns>The actual server-owned workload allocation, without resolved credentials.</returns>
    [AspireExport]
    public async Task<WorkloadAllocation> StartExecutableWithValues(long revision, string networkContext,
        ExecutableWorkloadLaunch launch, SymbolicLaunchBindings bindings) =>
        CopyAllocation(await owner.StartSymbolicWorkload(revision, CreateExecutablePlan(launch),
            ParseNetworkContext(networkContext), CopyBindings(bindings)).ConfigureAwait(false));

    /// <summary>Replaces a container using its current symbolic configuration after previous workload cleanup.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> RestartContainerWithValues(long revision, string networkContext,
        ContainerWorkloadLaunch launch, SymbolicLaunchBindings bindings) =>
        CopyAllocation(await owner.ReplaceSymbolicWorkloadAsync(revision, CreateContainerPlan(launch),
            ParseNetworkContext(networkContext), CopyBindings(bindings)).ConfigureAwait(false));

    /// <summary>Replaces a host executable using late-bound values and current dependency allocations.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> RestartExecutableWithValues(long revision, string networkContext,
        ExecutableWorkloadLaunch launch, SymbolicLaunchBindings bindings) =>
        CopyAllocation(await owner.ReplaceSymbolicWorkloadAsync(revision, CreateExecutablePlan(launch),
            ParseNetworkContext(networkContext), CopyBindings(bindings)).ConfigureAwait(false));

    /// <summary>Reads metadata for this execution's current symbolic fields, never their resolved contents.</summary>
    [AspireExport]
    public SymbolicConfigurationSnapshot InspectExecutionSymbolicConfiguration() =>
        DeclaredResource.CopySymbolic(owner.ReadSymbolicConfiguration());

    private static ValueNetworkContext ParseNetworkContext(string context) => context switch
    {
        "host" => ValueNetworkContext.Host,
        "container" => ValueNetworkContext.Container,
        _ => throw new SymbolicValueException(SymbolicValueFailure.UnsupportedNetworkContext)
    };

    internal static SymbolicWorkloadBindings CopyBindings(SymbolicLaunchBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.Environment);
        ArgumentNullException.ThrowIfNull(bindings.Arguments);
        if (bindings.Environment.Any(binding => binding is null) || bindings.Arguments.Any(binding => binding is null))
        {
            throw new ArgumentException("Symbolic launch bindings cannot contain null.");
        }

        return new(bindings.Environment.Select(binding => new SymbolicEnvironmentBinding(binding.Name, binding.ConfigurationKey)).ToImmutableArray(),
            bindings.Arguments.Select(binding => new SymbolicArgumentBinding(binding.Index, binding.ConfigurationKey)).ToImmutableArray());
    }
    /// <summary>Starts an owned container with named endpoints, an entrypoint and retained mounts.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> StartContainerWorkload(ContainerWorkloadLaunch launch) =>
        CopyAllocation(await owner.StartWorkload(CreateContainerPlan(launch)).ConfigureAwait(false));

    /// <summary>Replaces an owned container after its previous execution has been removed.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> RestartContainerWorkload(long revision, ContainerWorkloadLaunch launch) =>
        CopyAllocation(await owner.ReplaceWorkloadAsync(revision, CreateContainerPlan(launch)).ConfigureAwait(false));

    /// <summary>Starts an owned executable with independently named port allocations.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> StartExecutableWorkload(ExecutableWorkloadLaunch launch) =>
        CopyAllocation(await owner.StartWorkload(CreateExecutablePlan(launch)).ConfigureAwait(false));

    /// <summary>Replaces an owned executable after its previous execution has been removed.</summary>
    [AspireExport]
    public async Task<WorkloadAllocation> RestartExecutableWorkload(long revision, ExecutableWorkloadLaunch launch) =>
        CopyAllocation(await owner.ReplaceWorkloadAsync(revision, CreateExecutablePlan(launch)).ConfigureAwait(false));

    private static ContainerWorkload CreateContainerPlan(ContainerWorkloadLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(launch.Entrypoint);
        ArgumentNullException.ThrowIfNull(launch.Mounts);
        if (launch.Mounts.Any(mount => mount is null))
        {
            throw new ArgumentException("Container mounts cannot contain null.", nameof(launch));
        }

        var plan = new ContainerWorkload(launch.Image, launch.Entrypoint.Length == 0 ? null : launch.Entrypoint,
            launch.Mounts.Select(mount => new WorkloadMount(RuntimeStates.ParseMountKind(mount.Type), mount.Source, mount.Target, mount.ReadOnly)).ToImmutableArray(),
            CopyEnvironment(launch.Environment), CopyArguments(launch.Arguments), CopyEndpoints(launch.Endpoints));
        WorkloadDefinition.Validate(plan);

        return plan;
    }

    internal static ExecutableWorkload CreateExecutablePlan(ExecutableWorkloadLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var plan = new ExecutableWorkload(launch.ExecutablePath, launch.WorkingDirectory,
            CopyEnvironment(launch.Environment), CopyArguments(launch.Arguments), CopyEndpoints(launch.Endpoints));
        WorkloadDefinition.Validate(plan);

        return plan;
    }

    private static ImmutableArray<WorkloadEnvironment> CopyEnvironment(LaunchEnvironment[] environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Any(variable => variable is null))
        {
            throw new ArgumentException("Environment variables cannot contain null.", nameof(environment));
        }

        return environment.Select(variable => new WorkloadEnvironment(variable.Name, variable.Value)).ToImmutableArray();
    }

    private static ImmutableArray<string> CopyArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return arguments.ToImmutableArray();
    }

    private static ImmutableArray<WorkloadPort> CopyEndpoints(WorkloadEndpointDeclaration[] endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Any(endpoint => endpoint is null))
        {
            throw new ArgumentException("Workload endpoints cannot contain null.", nameof(endpoints));
        }

        return endpoints.Select(endpoint => new WorkloadPort(endpoint.Name, RuntimeStates.ParseProtocol(endpoint.Protocol), endpoint.UriScheme,
            endpoint.TargetPort, endpoint.HostPort, endpoint.EnvironmentVariable)).ToImmutableArray();
    }

    private static WorkloadAllocation CopyAllocation(WorkloadEndpoint allocation) => new()
    {
        InstanceId = allocation.InstanceId,
        Endpoints = allocation.Endpoints.Select(endpoint => new WorkloadEndpointAllocation
        {
            Name = endpoint.Name,
            Protocol = endpoint.Protocol.ToWire(),
            UriScheme = endpoint.UriScheme,
            Host = endpoint.Host,
            Port = endpoint.Port
        }).ToArray()
    };
}

/// <summary>References resource-owned symbolic fields by configuration key; contains no capability handles.</summary>
[AspireDto]
internal sealed class SymbolicLaunchBindings
{
    public required SymbolicLaunchEnvironment[] Environment { get; init; }
    public required SymbolicLaunchArgument[] Arguments { get; init; }
}

/// <summary>Maps one environment variable to a server-owned symbolic configuration field.</summary>
[AspireDto]
internal sealed class SymbolicLaunchEnvironment
{
    public required string Name { get; init; }
    public required string ConfigurationKey { get; init; }
}

/// <summary>Replaces one argument slot with a server-resolved symbolic field.</summary>
[AspireDto]
internal sealed class SymbolicLaunchArgument
{
    public required int Index { get; init; }
    public required string ConfigurationKey { get; init; }
}

/// <summary>Declares a container launch without integration-specific state or implicit endpoints.</summary>
[AspireDto]
internal sealed class ContainerWorkloadLaunch
{
    public required string Image { get; init; }
    public required string Entrypoint { get; init; }
    public required ContainerWorkloadMount[] Mounts { get; init; }
    public required LaunchEnvironment[] Environment { get; init; }
    public required string[] Arguments { get; init; }
    public required WorkloadEndpointDeclaration[] Endpoints { get; init; }
}

/// <summary>Declares an executable launch with named fixed or environment-injected ports.</summary>
[AspireDto]
internal sealed class ExecutableWorkloadLaunch
{
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required LaunchEnvironment[] Environment { get; init; }
    public required string[] Arguments { get; init; }
    public required WorkloadEndpointDeclaration[] Endpoints { get; init; }
}

/// <summary>Declares a host-facing endpoint; zero host port requests allocation.</summary>
[AspireDto]
internal sealed class WorkloadEndpointDeclaration
{
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public required string UriScheme { get; init; }
    public required int TargetPort { get; init; }
    public required int HostPort { get; init; }
    public required string EnvironmentVariable { get; init; }
}

/// <summary>Declares a bind mount or an externally retained named volume.</summary>
[AspireDto]
internal sealed class ContainerWorkloadMount
{
    public required string Type { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required bool ReadOnly { get; init; }
}

/// <summary>Contains a workload identity and all named host-facing allocations.</summary>
[AspireDto]
internal sealed class WorkloadAllocation
{
    public required string InstanceId { get; init; }
    public required WorkloadEndpointAllocation[] Endpoints { get; init; }
}

/// <summary>Contains an allocated host-facing endpoint without implying container-network routing.</summary>
[AspireDto]
internal sealed class WorkloadEndpointAllocation
{
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public required string UriScheme { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
}
