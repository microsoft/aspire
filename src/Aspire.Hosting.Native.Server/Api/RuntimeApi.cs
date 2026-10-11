// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Security.Cryptography;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

/// <summary>Provides application creation and explicitly delegated connection entry points.</summary>
[AspireExport]
internal sealed class NativeApplicationServer
{
    private readonly RuntimeInvitations _invitations;

    public NativeApplicationServer() : this(UnavailableWorkloadExecutor.Instance)
    {
    }

    public NativeApplicationServer(IWorkloadExecutor executor)
    {
        _invitations = new(executor);
    }

    /// <summary>Opens an application session owned by this connection.</summary>
    [AspireExport]
    public CompositionSession OpenApplication() => new(_invitations);

    /// <summary>Creates a server-owned workspace whose runtime survives AppHost disconnection.</summary>
    [AspireExport]
    public ApplicationWorkspace CreateApplicationWorkspace() => _invitations.CreateWorkspace();

    /// <summary>Resumes a workspace through a one-use invitation, never a caller-supplied resource name.</summary>
    [AspireExport]
    public ApplicationWorkspace JoinApplicationWorkspace(string invitation) => _invitations.Claim<ApplicationWorkspace>(invitation);

    /// <summary>Redeems a one-use invitation to own one resource execution.</summary>
    [AspireExport]
    public ResourceExecution JoinResourceExecution(string invitation) => _invitations.Claim<ResourceExecution>(invitation);

    /// <summary>Redeems a one-use invitation to observe and interact with one application execution.</summary>
    [AspireExport]
    public ApplicationObserver JoinApplicationObserver(string invitation) => _invitations.Claim<ApplicationObserver>(invitation);

    internal void Close() => _invitations.Close();
    internal Task DrainAsync() => _invitations.DrainAsync();
    internal ApplicationObserver? GetApplicationObserver() => _invitations.GetApplicationObserver();
}

/// <summary>Controls delegation within one sealed application generation.</summary>
[AspireExport]
internal sealed class ApplicationExecution : ICapabilityLifetime
{
    private readonly Composition _composition;
    private readonly RuntimeInvitations _invitations;
    internal RuntimeGeneration Runtime { get; }
    public bool IsRevoked => _composition.IsRetired || Runtime.IsRetired;

    internal ApplicationExecution(Composition composition, RuntimeInvitations invitations)
    {
        _composition = composition;
        _invitations = invitations;
        Runtime = new RuntimeGeneration(composition.Model.Seal(), invitations.Executor);
        invitations.Track(Runtime);
    }

    internal ApplicationExecution(Composition composition, RuntimeInvitations invitations, RuntimeGeneration runtime)
    {
        _composition = composition;
        _invitations = invitations;
        Runtime = runtime;
    }

    /// <summary>Delegates exclusive execution authority for this declaration to an integration connection.</summary>
    [AspireExport]
    public string InviteResourceExecution(DeclaredResource resource)
    {
        if (!ReferenceEquals(_composition, resource.Composition))
        {
            throw new ArgumentException("The resource belongs to another composition.", nameof(resource));
        }

        ObjectDisposedException.ThrowIf(IsRevoked, this);
        var declaration = resource.Composition.Model.ReadResource(resource.Handle);
        var handle = Runtime.ResolveResource(declaration.Name, declaration.TypeId);

        return _invitations.Issue(this, () => new ResourceExecution(Runtime.ClaimResource(handle)));
    }

    /// <summary>Delegates observation, command invocation, and interaction responses for this execution.</summary>
    [AspireExport]
    public string InviteApplicationObserver() => _invitations.Issue(this, () => new ApplicationObserver(Runtime));

    internal void Retire() => Runtime.Dispose();
}

/// <summary>Supplies resource-scoped runtime capabilities directly to an integration.</summary>
[AspireExport]
internal sealed class ResourceExecution(RuntimeResourceOwner owner) : ICapabilityLifetime, ICapabilityOwner
{
    public bool IsRevoked => owner.IsRevoked;

    /// <summary>Starts a standard container through the server-owned workload executor.</summary>
    [AspireExport]
    public async Task<StartedWorkload> StartContainer(ContainerLaunch container)
    {
        var endpoint = await owner.StartWorkload(ContainerPlan(container)).ConfigureAwait(false);

        return new StartedWorkload { Host = endpoint.Host, Port = endpoint.Port, InstanceId = endpoint.InstanceId };
    }

    /// <summary>Replaces this resource's container only after its previous workload has been removed.</summary>
    [AspireExport]
    public async Task<StartedWorkload> RestartContainer(long revision, ContainerLaunch container)
    {
        var endpoint = await owner.ReplaceWorkloadAsync(revision, ContainerPlan(container)).ConfigureAwait(false);

        return new StartedWorkload { Host = endpoint.Host, Port = endpoint.Port, InstanceId = endpoint.InstanceId };
    }

    private static ContainerWorkload ContainerPlan(ContainerLaunch container)
    {
        ArgumentNullException.ThrowIfNull(container);
        ValidateLaunch(container.Environment, container.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(container.Image);
        if (container.Image.Length > 2048 || container.TargetPort is < 1 or > 65535)
        {
            throw new ArgumentException("Invalid container launch.");
        }
        return new ContainerWorkload(container.Image, container.TargetPort,
            container.Environment.Select(variable => new WorkloadEnvironment(variable.Name, variable.Value)).ToImmutableArray(),
            container.Arguments.ToImmutableArray());
    }

    /// <summary>Starts a standard executable through the server-owned workload executor.</summary>
    [AspireExport]
    public async Task<StartedWorkload> StartExecutable(ExecutableLaunch executable)
    {
        var endpoint = await owner.StartWorkload(ExecutablePlan(executable)).ConfigureAwait(false);

        return new StartedWorkload { Host = endpoint.Host, Port = endpoint.Port, InstanceId = endpoint.InstanceId };
    }

    /// <summary>Replaces this resource's executable only after its previous workload has been removed.</summary>
    [AspireExport]
    public async Task<StartedWorkload> RestartExecutable(long revision, ExecutableLaunch executable)
    {
        var endpoint = await owner.ReplaceWorkloadAsync(revision, ExecutablePlan(executable)).ConfigureAwait(false);

        return new StartedWorkload { Host = endpoint.Host, Port = endpoint.Port, InstanceId = endpoint.InstanceId };
    }

    private static ExecutableWorkload ExecutablePlan(ExecutableLaunch executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ValidateLaunch(executable.Environment, executable.Arguments);
        if (!Path.IsPathFullyQualified(executable.ExecutablePath) || !File.Exists(executable.ExecutablePath) ||
            !Path.IsPathFullyQualified(executable.WorkingDirectory) || !Directory.Exists(executable.WorkingDirectory) ||
            executable.PortEnvironmentVariable is null || executable.PortEnvironmentVariable.Length > 128 ||
            executable.PortEnvironmentVariable.Contains('=') ||
            executable.Environment.Any(variable => variable.Name == executable.PortEnvironmentVariable))
        {
            throw new ArgumentException("Executables require an existing absolute executable path and working directory.");
        }
        return new ExecutableWorkload(executable.ExecutablePath, executable.WorkingDirectory, executable.PortEnvironmentVariable,
            executable.Environment.Select(variable => new WorkloadEnvironment(variable.Name, variable.Value)).ToImmutableArray(),
            executable.Arguments.ToImmutableArray());
    }

    private static void ValidateLaunch(LaunchEnvironment[] environment, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(arguments);
        if (environment.Length > 64 || arguments.Length > 128 ||
            arguments.Any(argument => argument is null || argument.Length > 8192) ||
            environment.Any(variable => variable is null || string.IsNullOrEmpty(variable.Name) ||
                variable.Name.Length > 128 || variable.Name.Contains('=') ||
                variable.Value is null || variable.Value.Length > 8192) ||
            environment.Select(variable => variable.Name).Distinct(StringComparer.Ordinal).Count() != environment.Length)
        {
            throw new ArgumentException("Invalid workload environment or arguments.");
        }
    }

    /// <summary>Publishes the observed lifecycle, health, and URLs for this resource execution.</summary>
    [AspireExport]
    public void PublishObservation(ResourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.State);
        ArgumentNullException.ThrowIfNull(observation.Urls);
        owner.Publish(observation.State, observation.Healthy, observation.Urls.ToImmutableArray());
    }

    /// <summary>Appends an ordered resource console log entry.</summary>
    [AspireExport]
    public void AppendResourceLog(string stream, string message) => owner.AppendLog(stream, message);

    /// <summary>Reads the desired configuration revision, dependencies, and application status for this execution.</summary>
    [AspireExport]
    public ResourceConfigurationRevision ReadResourceConfiguration() => CopyConfiguration(owner.ReadConfiguration());

    /// <summary>Reads observations only for this resource's declared readiness dependencies.</summary>
    [AspireExport]
    public ApplicationObservations ReadResourceDependencies() => ApplicationObserver.Copy(owner.ReadDependencies());

    /// <summary>Waits for a newer desired configuration or a bounded timeout without blocking other RPC work.</summary>
    [AspireExport]
    public async Task<ResourceConfigurationRevision> WaitResourceConfiguration(long revision, int timeoutMilliseconds) =>
        CopyConfiguration(await owner.WaitConfigurationAsync(revision, timeoutMilliseconds).ConfigureAwait(false));

    /// <summary>Acknowledges the current desired configuration or records an explicit integration failure.</summary>
    [AspireExport]
    public void CompleteResourceConfiguration(long revision, RuntimeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        owner.CompleteConfiguration(revision, result.Status, result.Message);
    }

    private static ResourceConfigurationRevision CopyConfiguration(RuntimeConfigurationRevision configuration) => new()
    {
        Revision = configuration.Revision,
        AppliedRevision = configuration.AppliedRevision,
        Status = configuration.Status,
        Message = configuration.Message,
        Properties = configuration.Properties.Select(entry =>
            new ResourceConfigurationProperty { Name = entry.Name, Value = entry.Value }).ToArray(),
        Dependencies = configuration.Dependencies.Select(handle => handle.ResourceId.ToString()).ToArray()
    };

    /// <summary>Registers a command that this integration can execute.</summary>
    [AspireExport]
    public void DefineResourceCommand(string name, string displayName) => owner.DefineCommand(name, displayName);

    /// <summary>Reads outstanding command invocations authorized for this resource.</summary>
    [AspireExport]
    public PendingResourceCommands ReadResourceCommands() => new()
    {
        Commands = owner.ReadCommands().Select(command => new ResourceCommandRequest
        {
            RequestId = command.Id.ToString(),
            Name = command.Name
        }).ToArray()
    };

    /// <summary>Waits for command work or a bounded timeout without blocking resource observations.</summary>
    [AspireExport]
    public async Task<PendingResourceCommands> WaitResourceCommands(int timeoutMilliseconds) => new()
    {
        Commands = (await owner.WaitCommandsAsync(timeoutMilliseconds).ConfigureAwait(false)).Select(command => new ResourceCommandRequest
        {
            RequestId = command.Id.ToString(),
            Name = command.Name
        }).ToArray()
    };

    /// <summary>Completes a command owned by this resource execution; request IDs alone confer no authority.</summary>
    [AspireExport]
    public void CompleteResourceCommand(string requestId, RuntimeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Status);
        ArgumentNullException.ThrowIfNull(result.Message);
        owner.CompleteCommand(Guid.Parse(requestId), result.Status, result.Message);
    }

    /// <summary>Requests user confirmation without holding the resource execution or RPC dispatch gate.</summary>
    [AspireExport]
    public RuntimeOperation RequestConfirmation(string message) => new(owner.RequestInteraction(message), this);

    public void Close() => owner.Dispose();
}

/// <summary>Projects execution observations and routes user actions without granting integration writer authority.</summary>
[AspireExport]
internal sealed class ApplicationObserver(RuntimeGeneration runtime) : ICapabilityLifetime
{
    public bool IsRevoked => runtime.IsRetired;
    internal DateTimeOffset CreatedAt => runtime.CreatedAt;

    /// <summary>Reads the current immutable resource observation view.</summary>
    [AspireExport]
    public ApplicationObservations ReadResourceObservations() => Copy(runtime.Read());

    /// <summary>Waits for a newer observation version or a bounded timeout.</summary>
    [AspireExport]
    public async Task<ApplicationObservations> WaitResourceObservations(long version, int timeoutMilliseconds) =>
        Copy(await runtime.WaitForChangeAsync(version, timeoutMilliseconds).ConfigureAwait(false));

    /// <summary>Reads ordered console logs with an explicit indication when retained history has been truncated.</summary>
    [AspireExport]
    public ResourceLogs ReadResourceLogs(string resourceId, long afterSequence)
    {
        var batch = runtime.ReadLogs(Guid.Parse(resourceId), afterSequence);

        return new ResourceLogs
        {
            LastSequence = batch.LastSequence,
            Truncated = batch.Truncated,
            Entries = batch.Entries.Select(line => new ResourceLog
            {
                Sequence = line.Sequence,
                Timestamp = line.Timestamp.ToString("O"),
                Stream = line.Stream,
                Message = line.Message
            }).ToArray()
        };
    }

    /// <summary>Requests execution of a registered resource command.</summary>
    [AspireExport]
    public RuntimeOperation InvokeResourceCommand(string resourceName, string commandName) =>
        new(runtime.RequestCommand(resourceName, commandName), this);

    /// <summary>Reads pending user-confirmation requests for this application execution.</summary>
    [AspireExport]
    public PendingConfirmations ReadConfirmations() => new()
    {
        Requests = runtime.ReadInteractions().Select(prompt => new ConfirmationRequest
        {
            RequestId = prompt.Id.ToString(),
            ResourceName = prompt.ResourceName,
            Message = prompt.Message
        }).ToArray()
    };

    /// <summary>Responds once to a pending confirmation within this application execution.</summary>
    [AspireExport]
    public void RespondConfirmation(string requestId, bool accepted) => runtime.RespondInteraction(Guid.Parse(requestId), accepted);

    internal static ApplicationObservations Copy(RuntimeSnapshot snapshot) => new()
    {
        GenerationId = snapshot.GenerationId.ToString(),
        Version = snapshot.Version,
        Resources = snapshot.Resources.Select(resource => new ResourceObservationSnapshot
        {
            ResourceId = resource.ResourceId.ToString(),
            Name = resource.Name,
            TypeId = resource.TypeId,
            State = resource.State,
            Healthy = resource.Healthy,
            Urls = resource.Urls.ToArray(),
            ConfigurationRevision = resource.ConfigurationRevision,
            AppliedConfigurationRevision = resource.AppliedConfigurationRevision,
            ConfigurationStatus = resource.ConfigurationStatus,
            Commands = resource.Commands.Select(command => new ResourceCommandDefinition
            {
                Name = command.Name,
                DisplayName = command.DisplayName
            }).ToArray()
        }).ToArray()
    };
}

/// <summary>Provides completion of a command or interaction without exposing framework tasks as handles.</summary>
[AspireExport]
internal sealed class RuntimeOperation(RuntimeRequest request, ICapabilityLifetime owner) : ICapabilityLifetime
{
    public bool IsRevoked => owner.IsRevoked;
    /// <summary>Awaits command or interaction completion, including cancellation on execution retirement.</summary>
    [AspireExport]
    public async Task<RuntimeOperationResult> AwaitRuntimeOperation()
    {
        var result = await request.Completion.ConfigureAwait(false);

        return new RuntimeOperationResult { Status = result.Status, Message = result.Message };
    }
}

/// <summary>Bounds short-lived, one-use delegation grants within a server instance.</summary>
internal sealed class RuntimeInvitations
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Invitation> _pending = new(StringComparer.Ordinal);
    private bool _closed;
    private readonly List<RuntimeGeneration> _executions = [];
    private readonly List<ApplicationWorkspace> _workspaces = [];
    private ApplicationObserver? _observer;
    public IWorkloadExecutor Executor { get; }

    public RuntimeInvitations() : this(UnavailableWorkloadExecutor.Instance)
    {
    }

    public RuntimeInvitations(IWorkloadExecutor executor)
    {
        Executor = executor;
    }

    public ApplicationWorkspace CreateWorkspace()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _workspaces.RemoveAll(workspace => workspace.IsRevoked);
            if (_workspaces.Count >= 1024)
            {
                throw new InvalidOperationException("The workspace capacity was reached.");
            }
            var workspace = new ApplicationWorkspace(this);
            _workspaces.Add(workspace);

            return workspace;
        }
    }

    public void Track(RuntimeGeneration execution)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _executions.RemoveAll(previous => previous.IsRetired && previous.Completion.IsCompletedSuccessfully);
            if (_executions.Count >= 1024)
            {
                execution.Dispose();
                throw new InvalidOperationException("The execution capacity was reached.");
            }
            _executions.Add(execution);
            _observer = new ApplicationObserver(execution);
        }
    }

    public ApplicationObserver? GetApplicationObserver()
    {
        lock (_gate)
        {
            return _observer is { IsRevoked: false } ? _observer : null;
        }
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_executions.Select(execution => execution.Completion));
        }
    }

    public string Issue<T>(ICapabilityLifetime execution, Func<T> create) where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed || execution.IsRevoked, this);
            RemoveExpired();
            if (_pending.Count >= 1024)
            {
                throw new InvalidOperationException("The invitation capacity was reached.");
            }
            var token = RandomNumberGenerator.GetHexString(64, lowercase: true);
            _pending.Add(token, new Invitation(execution, typeof(T), () => create(), DateTimeOffset.UtcNow.AddMinutes(1)));

            return token;
        }
    }

    public T Claim<T>(string token) where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            RemoveExpired();
            if (!_pending.TryGetValue(token, out var invitation) || invitation.Type != typeof(T))
            {
                throw new ArgumentException("The invitation is unknown, expired, or not applicable.", nameof(token));
            }
            _pending.Remove(token);

            return (T)invitation.Create();
        }
    }

    public void Close()
    {
        ApplicationWorkspace[] workspaces;
        lock (_gate)
        {
            _closed = true;
            _pending.Clear();
            workspaces = _workspaces.ToArray();
        }
        // Workspace commits acquire the invitation gate while holding their own
        // gate. Close them outside it to avoid lock-order inversion during shutdown.
        foreach (var workspace in workspaces)
        {
            workspace.Close();
        }
        lock (_gate)
        {
            foreach (var execution in _executions)
            {
                execution.Dispose();
            }
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, invitation) in _pending.ToArray())
        {
            if (invitation.Execution.IsRevoked || invitation.Expires <= now)
            {
                _pending.Remove(token);
            }
        }
    }

    private sealed record Invitation(ICapabilityLifetime Execution, Type Type, Func<object> Create, DateTimeOffset Expires);
}

[AspireDto]
internal sealed class ResourceConfigurationRevision
{
    public required long Revision { get; init; }
    public required long AppliedRevision { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
    public required ResourceConfigurationProperty[] Properties { get; init; }
    public required string[] Dependencies { get; init; }
}

[AspireDto]
internal sealed class ResourceObservation
{
    public required string State { get; init; }
    public required bool Healthy { get; init; }
    public required string[] Urls { get; init; }
}

[AspireDto]
internal sealed class ApplicationObservations
{
    public required long Version { get; init; }
    public required string GenerationId { get; init; }
    public required ResourceObservationSnapshot[] Resources { get; init; }
}

[AspireDto]
internal sealed class ResourceObservationSnapshot
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required string TypeId { get; init; }
    public required string State { get; init; }
    public required bool Healthy { get; init; }
    public required string[] Urls { get; init; }
    public required ResourceCommandDefinition[] Commands { get; init; }
    public required long ConfigurationRevision { get; init; }
    public required long AppliedConfigurationRevision { get; init; }
    public required string ConfigurationStatus { get; init; }
}

[AspireDto]
internal sealed class ResourceCommandDefinition
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
}

[AspireDto]
internal sealed class ResourceLogs
{
    public required long LastSequence { get; init; }
    public required bool Truncated { get; init; }
    public required ResourceLog[] Entries { get; init; }
}

[AspireDto]
internal sealed class ResourceLog
{
    public required long Sequence { get; init; }
    public required string Timestamp { get; init; }
    public required string Stream { get; init; }
    public required string Message { get; init; }
}

[AspireDto]
internal sealed class PendingResourceCommands
{
    public required ResourceCommandRequest[] Commands { get; init; }
}

[AspireDto]
internal sealed class ResourceCommandRequest
{
    public required string RequestId { get; init; }
    public required string Name { get; init; }
}

[AspireDto]
internal sealed class PendingConfirmations
{
    public required ConfirmationRequest[] Requests { get; init; }
}

[AspireDto]
internal sealed class ConfirmationRequest
{
    public required string RequestId { get; init; }
    public required string ResourceName { get; init; }
    public required string Message { get; init; }
}

[AspireDto]
internal sealed class RuntimeOperationResult
{
    public required string Status { get; init; }
    public required string Message { get; init; }
}

[AspireDto]
internal sealed class ContainerLaunch
{
    public required string Image { get; init; }
    public required int TargetPort { get; init; }
    public required LaunchEnvironment[] Environment { get; init; }
    public required string[] Arguments { get; init; }
}

[AspireDto]
internal sealed class ExecutableLaunch
{
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string PortEnvironmentVariable { get; init; }
    public required LaunchEnvironment[] Environment { get; init; }
    public required string[] Arguments { get; init; }
}

[AspireDto]
internal sealed class LaunchEnvironment
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

[AspireDto]
internal sealed class StartedWorkload
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string InstanceId { get; init; }
}
