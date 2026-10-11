// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Dcp;

/// <summary>Projects standard native execution primitives to DCP-owned workloads.</summary>
internal sealed class DcpWorkloadExecutor(DcpApiClient client, Task startup) : IWorkloadExecutor
{
    public DcpWorkloadExecutor(DcpApiClient client) : this(client, Task.CompletedTask)
    {
    }

    public async Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken)
    {
        await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        var name = "native-" + identity.ResourceId.ToString("N");
        var collection = plan is ContainerWorkload ? "containers" : "executables";
        var service = name + "-tcp";
        var exposesPort = plan is ContainerWorkload || plan is ExecutableWorkload { PortEnvironmentVariable.Length: > 0 };
        var lease = new Lease(client, collection, name, exposesPort ? service : null);
        try
        {
            var spec = new JsonObject
            {
                ["env"] = new JsonArray(plan.Environment.Select(variable => (JsonNode)new JsonObject
                {
                    ["name"] = variable.Name, ["value"] = variable.Value
                }).ToArray()),
                ["args"] = new JsonArray(plan.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray())
            };
            JsonObject? annotations = null;
            if (exposesPort)
            {
                await client.CreateAsync("services", service, new JsonObject
                {
                    ["protocol"] = "TCP", ["addressAllocationMode"] = plan is ContainerWorkload ? "Proxyless" : "Localhost"
                }, null, cancellationToken).ConfigureAwait(false);
                annotations = new JsonObject
                {
                    ["service-producer"] = new JsonArray(new JsonObject
                    {
                        ["serviceName"] = service, ["address"] = "127.0.0.1",
                        ["port"] = plan is ContainerWorkload containerPort ? JsonValue.Create(containerPort.TargetPort) : null
                    }).ToJsonString()
                };
            }
            if (plan is ContainerWorkload container)
            {
                spec["image"] = container.Image;
                spec["ports"] = new JsonArray(new JsonObject
                {
                    ["containerPort"] = container.TargetPort, ["hostIP"] = "127.0.0.1", ["protocol"] = "TCP"
                });
            }
            else if (plan is ExecutableWorkload executable)
            {
                spec["executablePath"] = executable.ExecutablePath;
                spec["workingDirectory"] = executable.WorkingDirectory;
                spec["executionType"] = "Process";
                if (executable.PortEnvironmentVariable.Length > 0)
                {
                    // DCP substitutes the allocation before launch, avoiding a
                    // reserve/release port race in the executable integration.
                    spec["env"]!.AsArray().Add((JsonNode)new JsonObject
                    {
                        ["name"] = executable.PortEnvironmentVariable,
                        ["value"] = "{{- portForServing \"" + service + "\" -}}"
                    });
                }
            }
            else
            {
                throw new NotSupportedException("The workload primitive is not implemented.");
            }
            await client.CreateAsync(collection, name, spec, annotations, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var resource = await client.GetAsync(collection, name, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("DCP removed the workload during startup.");
                var state = resource["status"]?["state"]?.GetValue<string>();
                if (state is "Exited" or "Finished" or "FailedToStart")
                {
                    throw new WorkloadStartupException("DCP could not start the workload.",
                        await lease.ReadLogsAsync(cancellationToken).ConfigureAwait(false));
                }
                if (state == "Running")
                {
                    var instanceId = plan is ContainerWorkload
                        ? resource["status"]?["containerId"]?.GetValue<string>()
                        : resource["status"]?["pid"]?.ToJsonString();
                    if (string.IsNullOrEmpty(instanceId))
                    {
                        throw new InvalidOperationException("DCP did not report its running workload identity.");
                    }
                    if (!exposesPort)
                    {
                        lease.Endpoint = new WorkloadEndpoint("127.0.0.1", 0, instanceId);
                        return lease;
                    }
                    var endpoint = await client.GetAsync("services", service, cancellationToken).ConfigureAwait(false);
                    if (endpoint?["status"]?["effectivePort"] is JsonValue port && port.TryGetValue<int>(out var value) && value > 0)
                    {
                        var address = endpoint["status"]!["effectiveAddress"]!.GetValue<string>();
                        if (!address.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                            (!System.Net.IPAddress.TryParse(address, out var host) || !System.Net.IPAddress.IsLoopback(host)))
                        {
                            throw new InvalidOperationException("DCP must allocate a loopback workload endpoint.");
                        }
                        lease.Endpoint = new WorkloadEndpoint(address, value, instanceId);

                        return lease;
                    }
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A partially submitted workload still has a cleanup obligation.
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Lease(DcpApiClient client, string collection, string name, string? service) : IWorkloadLease
    {
        private long _stdout;
        private long _stderr;
        private int _disposed;
        public WorkloadEndpoint Endpoint { get; set; } = new("127.0.0.1", 0, "");

        public async Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken)
        {
            var resource = await client.GetAsync(collection, name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The DCP workload disappeared before cleanup.");
            var state = resource["status"]?["state"]?.GetValue<string>();
            var exitCode = resource["status"]?["exitCode"]?.GetValue<int>();

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
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await client.DeleteAsync(collection, name, cleanup.Token).ConfigureAwait(false);
            if (service is not null)
            {
                await client.DeleteAsync("services", service, cleanup.Token).ConfigureAwait(false);
            }
        }
    }
}
