# Experimental DCP V2 execution

The AppHost can opt into the logical DCP V2 execution pipeline for an entire run.
V1 remains the default. Set `DcpPublisher:UseV2` in AppHost configuration:

```json
{
  "DcpPublisher": {
    "UseV2": true
  }
}
```

The equivalent environment variable is `DcpPublisher__UseV2=true`. To use a development
DCP binary, set `DcpPublisher:CliPath` (or `DcpPublisher__CliPath`) to its executable path.
The inactive execution pipeline and its watchers are not constructed.

## DCP requirements

This implementation follows the logical service and typed-port APIs in
[DCP commit c5edc40a](https://github.com/microsoft/dcp/tree/c5edc40a38f023c1cb24a8a46c7eed1932c15798/api/v2).
There is no established minimum released DCP version yet. Startup probes the required
V2 resource APIs and reports a configuration error if they are missing, before creating
workloads. These probes are not a guarantee of compatibility with arbitrary future
changes to the V2 wire contracts. The older DCP bundled with Aspire does not establish
end-to-end readiness for this feature.

## Resources and lifecycle

Executables and projects run as plain `LogicalProcess` resources. Project process
launch commands, arguments, environment callbacks, endpoint allocation, dependencies,
and replica naming use the shared app-model orchestration and launch recipes.
V1 IDE launch settings do not turn these processes into debug sessions.

Containers run as `LogicalContainer` resources with typed image/build configuration,
volume mounts, create-files, labels, and logical network connections. Named physical
container volumes are retained at shutdown. Anonymous session volumes are removed.
Container executables use `LogicalContainerExec` after their target container is running.
Stopping uses the monotonic `spec.stop` request; starting again deletes and recreates
the logical object rather than resetting `stop`. Container executions are deleted to stop them.

Each AppHost run starts its own DCP API server with a fresh session directory and
kubeconfig. Session-only applications use a random namespace. Applications containing
persistent processes use a stable namespace derived from the AppHost path identity and
resource-name suffix, together with stable persistent process names. This preserves
DCP's namespace/name process lease key across API-server runs without a second
Aspire process-identity store. Different AppHosts and isolation suffixes have different
identities. DCP's shared persistent-resource leases coordinate concurrent API servers;
they do not merge their API objects.

An existing namespace on the same API server is never adopted. Cleanup deletes the
owned namespace with a UID precondition, allowing DCP's namespace finalizer to cascade
through its children while retaining persistent runtime workloads. Replaced namespaces
and workloads are not deleted. Persistent container reuse is based on the stable runtime
container name and DCP's calculated lifecycle key, not on namespace identity. DCP excludes
network attachments and normalizes volume references when computing that key.

Explicit-start session resources are not submitted until requested. Explicit-start
persistent containers can observe an existing runtime container without evaluating
start-time callbacks; a missing container is not created until start is requested.
Explicit-start persistent processes are not submitted until requested because V2
does not provide V1's delayed-start registration operation.

## Endpoints and logs

Logical services are created before workloads. Aspire waits for proxy listener
addresses, not service `Ready`: readiness additionally requires a workload endpoint.
Every endpoint in the model is allocated before endpoint callbacks run. Typed workload
ports reference logical services; DCP controllers publish the workload's `ServiceEndpoint`
resources. Public addresses and serving addresses/ports remain separate. Serving-port
references use V2 `portForServing` with logical port names.

Container-to-host connections use an all-interface logical-service proxy and the
container runtime's host bridge, rather than a V2 tunnel. `HostUrl` references to modeled
host endpoints use the same projected address. This is the established non-tunnel
connectivity route and retains its runtime/host-network limitations. In particular,
the runtime must make its host bridge reachable from containers. These proxies expose
listeners on host network interfaces; opt in only where that exposure is appropriate.

The V2 watcher is namespace-scoped. It projects phases, conditions, exit codes, resource
relationships, and controller-published URL activity into dashboard snapshots. Unknown
exit codes remain unknown. Runtime and startup log streams are opened only when their
`LogSource` is available. Source identities and delivery cursors distinguish captures,
subscriber reconnects, and replaced objects. Shutdown cancels and drains owned streams.
V2 does not report V1's complete effective argument/environment arrays; snapshots show
the declared values, including DCP expressions when applicable.

## Current boundaries

- V2 Project resources, debug adapters, IDE opt-in, terminal execution, and container
  tunnels are follow-up work. Plain process launching of projects is supported.
- Arbitrary container runtime CLI arguments have no V2 equivalent. `--label` arguments
  map to typed labels; other runtime arguments fail with an actionable diagnostic.
- Process custom certificate trust files are materialized by Aspire. Non-Windows
  OpenSSL certificate directories require the local `openssl x509 -hash` command.
- DCP V1 remains unchanged. This opt-in path is not a transparent replacement for all
  V1 settings, and must be validated against a compatible DCP build before release.
