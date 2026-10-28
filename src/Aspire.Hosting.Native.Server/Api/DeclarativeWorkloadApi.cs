// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

internal sealed partial class Composition
{
    /// <summary>Declares a container without launching it or acquiring execution ownership.</summary>
    [AspireExport]
    public DeclaredResource AddContainer(string name, string image)
    {
        var plan = DeclaredWorkload.Create(new ContainerWorkload(image, null, [], [], [], []));
        var resource = AddResource(name, "hosting/container");
        Model.SetWorkload(resource.Handle, plan);

        return resource;
    }

    /// <summary>Declares a host executable without launching it.</summary>
    [AspireExport]
    public DeclaredResource AddExecutable(string name, string executablePath, string workingDirectory)
    {
        var plan = DeclaredWorkload.Create(new ExecutableWorkload(executablePath, workingDirectory, [], [], []));
        var resource = AddResource(name, "hosting/executable");
        Model.SetWorkload(resource.Handle, plan);

        return resource;
    }

    /// <summary>Seals the desired graph and starts declared workloads after allocation and explicit readiness prerequisites.</summary>
    [AspireExport]
    public ApplicationExecution StartApplication()
    {
        lock (_runtimeGate)
        {
            var execution = _execution ?? CreateExecution();
            execution.Runtime.StartDeclaredApplication();

            return execution;
        }
    }
}

internal sealed partial class DeclaredResource
{
    /// <summary>Assigns a late-bound environment value without adding an implicit readiness wait.</summary>
    [AspireExport]
    public DeclaredResource WithEnvironment(string name, DeclaredValue value)
    {
        if (!ReferenceEquals(Composition, value.Composition))
        {
            throw new ArgumentException("The value belongs to another composition.", nameof(value));
        }
        Composition.Model.MutateWorkload(Handle, current =>
        {
            var key = $"workload.env.{name}";
            var next = current with
            {
                Bindings = current.Bindings with
                {
                    Environment = current.Bindings.Environment.Where(binding => binding.Name != name)
                        .Append(new SymbolicEnvironmentBinding(name, key)).ToImmutableArray()
                }
            };

            return (next, new(key, value.Value));
        });

        return this;
    }

    /// <summary>References a symbolic connection value through an environment slot without resolving it.</summary>
    [AspireExport]
    public DeclaredResource WithReference(string environmentName, DeclaredValue value) => WithEnvironment(environmentName, value);

    /// <summary>Appends a late-bound argument to the declared workload.</summary>
    [AspireExport]
    public DeclaredResource WithArgument(DeclaredValue value)
    {
        if (!ReferenceEquals(Composition, value.Composition))
        {
            throw new ArgumentException("The value belongs to another composition.", nameof(value));
        }
        Composition.Model.MutateWorkload(Handle, current =>
        {
            var index = current.Plan.Arguments.Length;
            var key = $"workload.arg.{index}";
            var plan = SymbolicWorkloadBindings.Copy(current.Plan, current.Plan.Environment, current.Plan.Arguments.Add(""));
            var next = current with
            {
                Plan = plan,
                Bindings = current.Bindings with { Arguments = current.Bindings.Arguments.Add(new(index, key)) }
            };

            return (next, new(key, value.Value));
        });

        return this;
    }

    /// <summary>Declares a named endpoint; host ports are allocated when the application starts.</summary>
    [AspireExport]
    public DeclaredResource WithEndpoint(WorkloadEndpointDeclaration endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Composition.Model.MutateWorkload(Handle, current =>
        {
            var ports = current.Plan.Endpoints.Where(port => port.Name != endpoint.Name).Append(
                new WorkloadPort(endpoint.Name, RuntimeStates.ParseProtocol(endpoint.Protocol), endpoint.UriScheme,
                    endpoint.TargetPort, endpoint.HostPort, endpoint.EnvironmentVariable)).ToImmutableArray();
            var plan = current.Plan switch
            {
                ContainerWorkload container => (WorkloadPlan)(container with { Ports = ports }),
                ExecutableWorkload executable => executable with { Ports = ports },
                _ => throw new NotSupportedException("The declared workload primitive is unsupported.")
            };

            return (current with { Plan = plan }, null);
        });

        return this;
    }

    /// <summary>Marks the workload's HTTP endpoints as externally exposed deployment intent.</summary>
    [AspireExport]
    public DeclaredResource WithExternalHttpEndpoints()
    {
        Composition.Model.MutateWorkload(Handle, current =>
        {
            var ports = current.Plan.Endpoints.Select(endpoint =>
                endpoint.UriScheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                endpoint.UriScheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                    ? endpoint with { IsExternal = true } : endpoint).ToImmutableArray();
            var plan = current.Plan switch
            {
                ContainerWorkload container => (WorkloadPlan)(container with { Ports = ports }),
                ExecutableWorkload executable => executable with { Ports = ports },
                _ => throw new NotSupportedException("The declared workload primitive is unsupported.")
            };

            return (current with { Plan = plan }, null);
        });

        return this;
    }

    /// <summary>Configures the container entrypoint without interpreting shell syntax.</summary>
    [AspireExport]
    public DeclaredResource WithEntrypoint(string entrypoint)
    {
        Composition.Model.MutateWorkload(Handle, current =>
        {
            if (current.Plan is not ContainerWorkload container)
            {
                throw new InvalidOperationException("Entrypoints require a container declaration.");
            }

            return (current with { Plan = container with { Command = entrypoint } }, null);
        });

        return this;
    }

    /// <summary>Mounts a named retained data volume; application retirement removes the container, not the volume.</summary>
    [AspireExport]
    public DeclaredResource WithVolume(string name, string target, bool readOnly)
    {
        Composition.Model.MutateWorkload(Handle, current =>
        {
            if (current.Plan is not ContainerWorkload container)
            {
                throw new InvalidOperationException("Volumes require a container declaration.");
            }
            var mounts = container.Mounts.Where(mount => mount.Target != target)
                .Append(new WorkloadMount(WorkloadMountKind.Volume, name, target, readOnly)).ToImmutableArray();

            return (current with { Plan = container with { Mounts = mounts } }, null);
        });

        return this;
    }

    /// <summary>Declares a bind mount; the source is checked on the local machine only when executing locally.</summary>
    [AspireExport]
    public DeclaredResource WithBindMount(string source, string target, bool readOnly)
    {
        Composition.Model.MutateWorkload(Handle, current =>
        {
            if (current.Plan is not ContainerWorkload container)
            {
                throw new InvalidOperationException("Bind mounts require a container declaration.");
            }
            var mounts = container.Mounts.Where(mount => mount.Target != target)
                .Append(new WorkloadMount(WorkloadMountKind.Bind, source, target, readOnly)).ToImmutableArray();

            return (current with { Plan = container with { Mounts = mounts } }, null);
        });

        return this;
    }

    /// <summary>Declares an integration-owned executable hook for initialize, health, ready, or retire.</summary>
    /// <remarks>Health commands must be side-effect-free and return zero only after authentic protocol readiness.
    /// Inputs reference native symbolic fields; no resolved credentials are returned to integration authors.</remarks>
    [AspireExport]
    public DeclaredResource WithLifecycleExecutable(string phase, ExecutableWorkloadLaunch launch, SymbolicLaunchBindings bindings)
    {
        if (phase is not (LifecycleCommandPhases.Initialize or LifecycleCommandPhases.Health or LifecycleCommandPhases.Ready or LifecycleCommandPhases.Retire))
        {
            throw new ArgumentException("Unsupported declared lifecycle phase.", nameof(phase));
        }
        var command = ResourceExecution.CreateExecutablePlan(launch);
        var slots = ResourceExecution.CopyBindings(bindings);
        slots.Validate(command);
        Composition.Model.MutateWorkload(Handle, current => (current with
        {
            Lifecycle = current.Lifecycle.Where(hook => hook.Phase != phase)
                .Append(new(phase, command, slots)).ToImmutableArray()
        }, null));

        return this;
    }
}
