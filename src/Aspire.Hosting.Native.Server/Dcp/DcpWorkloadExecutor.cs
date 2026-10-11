// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Dcp;

/// <summary>Projects standard native execution primitives to DCP-owned workloads.</summary>
internal sealed class DcpWorkloadExecutor(DcpApiClient client, Task startup, NativeServerOptions options) : IWorkloadExecutor
{
    public DcpWorkloadExecutor(DcpApiClient client) : this(client, Task.CompletedTask, new())
    {
    }

    public DcpWorkloadExecutor(DcpApiClient client, Task startup) : this(client, startup, new())
    {
    }

    public async Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken)
    {
        await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        var name = "native-" + identity.ResourceId.ToString("N");
        var collection = plan is ContainerWorkload ? "containers" : "executables";
        var service = name + "-tcp";
        var exposesPort = plan is ContainerWorkload { TargetPort: > 0 } ||
            plan is ExecutableWorkload { PortEnvironmentVariable.Length: > 0 };
        var lease = new Lease(client, collection, name, exposesPort ? service : null, options);
        try
        {
            var spec = new DcpWorkloadSpec
            {
                Env = plan.Environment.Select(variable => new DcpEnvironment(variable.Name, variable.Value)).ToArray(),
                Args = plan.Arguments.ToArray()
            };
            Dictionary<string, string>? annotations = null;
            if (exposesPort)
            {
                await client.CreateAsync("services", service, JsonSerializer.SerializeToElement(
                    new DcpServiceSpec("TCP", plan is ContainerWorkload ? "Proxyless" : "Localhost"),
                    DcpJsonContext.Default.DcpServiceSpec), null, cancellationToken).ConfigureAwait(false);
                annotations = new Dictionary<string, string>
                {
                    ["service-producer"] = JsonSerializer.Serialize(new[]
                    {
                        new DcpServiceProducer(service, System.Net.IPAddress.Loopback.ToString(),
                            plan is ContainerWorkload containerPort ? containerPort.TargetPort : null)
                    }, DcpJsonContext.Default.DcpServiceProducerArray)
                };
            }
            if (plan is ContainerWorkload container)
            {
                spec = spec with
                {
                    Image = container.Image,
                    Ports = exposesPort
                        ? [new DcpContainerPort(container.TargetPort, System.Net.IPAddress.Loopback.ToString(), "TCP")]
                        : null
                };
            }
            else if (plan is ExecutableWorkload executable)
            {
                spec = spec with
                {
                    ExecutablePath = executable.ExecutablePath,
                    WorkingDirectory = executable.WorkingDirectory,
                    ExecutionType = "Process"
                };
                if (executable.PortEnvironmentVariable.Length > 0)
                {
                    // DCP substitutes the allocation before launch, avoiding a
                    // reserve/release port race in the executable integration.
                    spec = spec with
                    {
                        Env = [.. spec.Env, new DcpEnvironment(executable.PortEnvironmentVariable,
                            "{{- portForServing \"" + service + "\" -}}")]
                    };
                }
            }
            else
            {
                throw new NotSupportedException("The workload primitive is not implemented.");
            }
            await client.CreateAsync(collection, name, JsonSerializer.SerializeToElement(spec, DcpJsonContext.Default.DcpWorkloadSpec),
                annotations, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var resource = await client.GetAsync(collection, name, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("DCP removed the workload during startup.");
                var state = resource.Status?.State;
                if (state is "Exited" or "Finished" or "FailedToStart")
                {
                    throw new WorkloadStartupException("DCP could not start the workload.",
                        await lease.ReadLogsAsync(cancellationToken).ConfigureAwait(false));
                }
                if (state == "Running")
                {
                    var instanceId = plan is ContainerWorkload
                        ? resource.Status?.ContainerId
                        : resource.Status?.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(instanceId))
                    {
                        throw new InvalidOperationException("DCP did not report its running workload identity.");
                    }
                    if (!exposesPort)
                    {
                        lease.Endpoint = new WorkloadEndpoint(System.Net.IPAddress.Loopback.ToString(), 0, instanceId);
                        return lease;
                    }
                    var endpoint = await client.GetAsync("services", service, cancellationToken).ConfigureAwait(false);
                    if (endpoint?.Status is { EffectivePort: > 0 and <= 65535 } status)
                    {
                        var address = status.EffectiveAddress
                            ?? throw new InvalidDataException("DCP did not report an allocated endpoint address.");
                        if (!address.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                            (!System.Net.IPAddress.TryParse(address, out var host) || !System.Net.IPAddress.IsLoopback(host)))
                        {
                            throw new InvalidOperationException("DCP must allocate a loopback workload endpoint.");
                        }
                        lease.Endpoint = new WorkloadEndpoint(address, status.EffectivePort.Value, instanceId);

                        return lease;
                    }
                }
                await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A partially submitted workload still has a cleanup obligation.
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(DcpApiClient client, string collection, string name, string? service,
        NativeServerOptions options) : IWorkloadLease
    {
        private long _stdout;
        private long _stderr;
        private int _disposed;
        public WorkloadEndpoint Endpoint { get; set; } = new(System.Net.IPAddress.Loopback.ToString(), 0, "");

        public async Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken)
        {
            var resource = await client.GetAsync(collection, name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The DCP workload disappeared before cleanup.");
            var state = resource.Status?.State;
            var exitCode = resource.Status?.ExitCode;

            return new WorkloadStatus(state == "Running", exitCode);
        }

        public async Task<ImmutableArray<WorkloadLog>> ReadLogsAsync(CancellationToken cancellationToken)
        {
            var stdout = await client.ReadLogsAsync(collection, name, "stdout", _stdout, cancellationToken).ConfigureAwait(false);
            var stderr = await client.ReadLogsAsync(collection, name, "stderr", _stderr, cancellationToken).ConfigureAwait(false);
            _stdout += stdout.Length;
            _stderr += stderr.Length;

            return stdout.Select(message => new WorkloadLog("stdout", message.TrimEnd('\r')))
                .Concat(stderr.Select(message => new WorkloadLog("stderr", message.TrimEnd('\r')))).ToImmutableArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
            await client.DeleteAsync(collection, name, cleanup.Token).ConfigureAwait(false);
            if (service is not null)
            {
                await client.DeleteAsync("services", service, cleanup.Token).ConfigureAwait(false);
            }
        }
    }
}
