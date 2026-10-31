# Local application, SSH-managed remote Redis (experimental)

Working design exploration for [discussion #18577](https://github.com/microsoft/aspire/discussions/18577).
This is **remote dependency development, not deployment of the application**.
The AppHost and ASP.NET Core API run locally; Aspire creates Redis in a different
Linux environment through SSH, forwards its endpoint, streams its logs and owns
its lifetime. No global `DOCKER_HOST` and no change to shipped hosting APIs.

The bounded journey is one ephemeral Redis dependency consumed by a **local
project** through `WithReference` and `WaitFor`. This deliberately does not claim
`AddRedis().RunOnHost(...)` support for arbitrary existing container resources.

## Reproduce

Prerequisites: Linux, OpenSSH client/keygen, `ps`/`kill`, Docker supporting privileged
Linux containers, the repository's pinned SDK and .NET 10 runtime. Use only a
disposable development machine for the privileged fixture.

From the repository root:

```sh
./restore.sh --projects "$PWD/playground/RemoteDependencies/RemoteDependencies.AppHost/RemoteDependencies.AppHost.csproj" \
    /p:SkipDashboardProjectReference=true
./.dotnet/dotnet build playground/RemoteDependencies/RemoteDependencies.AppHost \
    -p:SkipDashboardProjectReference=true
./.dotnet/dotnet build playground/RemoteDependencies/RemoteDependencies.Tools
tools=artifacts/bin/RemoteDependencies.Tools/Debug/net10.0/RemoteDependencies.Tools.dll
docker info
./.dotnet/dotnet "$tools" up
. playground/RemoteDependencies/.fixture/env.sh
REMOTE_HEADLESS=1 ./.dotnet/dotnet \
    artifacts/bin/RemoteDependencies.AppHost/Debug/net10.0/RemoteDependencies.AppHost.dll
```

If `docker info` fails, start your normal local Docker daemon first. The fixture
never starts or replaces the workstation daemon. It creates a separate nested
engine with **only a Unix socket inside its container**, not a public TCP engine
listener, and does not mount the workstation Docker socket.

Direct DLL execution avoids the source-tree AppHost package's CLI-bundle run
hook. Build first. The targeted restore above installs the repository's pinned
SDK/runtime toolset and restores this prototype's project graph, without pulling
unrelated integrations into the experiment. The absolute `--projects` path is
required by the repository's restore wrapper.

In this sandbox the configured audit endpoint was unavailable, so the verified
restore/build/test commands additionally used `-p:NuGetAudit=false`. Add that
command-line property if you encounter the same endpoint error; do not edit
repository feeds or disable auditing globally. A full-solution restore is not
required here and encountered unrelated feed download failures.

In another terminal:

```sh
curl --fail http://127.0.0.1:18080/
curl --fail http://127.0.0.1:18080/
```

Responses include incrementing `requests`, `"cache":"127.0.0.1:16379"` and
`"application":"local"`. The cache value is injected by Aspire, not hardcoded in
the API. Ctrl+C the AppHost to cancel its sessions and await owned-container
cleanup. To run the automated manual scenario matrix instead, stop that AppHost:

```sh
./.dotnet/dotnet "$tools" verify
```

`RemoteDependencies.Tools` is a dependency-free .NET console project using the
same SDK/runtime as the playground. Its `up`, `verify` and `down` commands replace
separate scripting-runtime requirements. Run it from inside the repository.
Ctrl+C during verification cancels polling, stops the owned AppHost and still
runs fixture restoration/reconciliation before returning a nonzero exit.
The AppHost also awaits its remote sessions in a `finally` block, because host
startup cancellation can return before hosted-service shutdown is invoked.
The verifier uses the built AppHost, real SSH, real Docker and real Redis; it
does not substitute a fake transport or fake dependency. It reserves local ports
18080 and 16379 for the duration of the run. Do not run parallel copies.

Run proportional unit/model/protocol coverage:

```sh
./.dotnet/dotnet test \
    --project tests/Aspire.Hosting.RemoteDependencies.Tests \
    --no-launch-profile -- \
    --filter-not-trait 'quarantined=true' --filter-not-trait 'outerloop=true'
```

Add `-p:NuGetAudit=false` before `--` only if the audit endpoint is inaccessible.
These tests are normal PR-CI tests; the privileged fixture and tools' `verify` are
explicitly manual, not an implicit Docker requirement for unit CI.

## Topology and access

```text
developer environment
  AppHost + local ASP.NET API :18080
    ConnectionStrings__cache = 127.0.0.1:16379
      OpenSSH local forward (bind 127.0.0.1 only)
        SSH fixture container :22 (published on workstation loopback, random port)
          remote network namespace: 127.0.0.1:26379
            nested Docker Redis container (--network host, bind 127.0.0.1)
```

The fixture generates **new task-owned keys** under ignored `.fixture/` (directory
mode 0700); no existing SSH keys are read. It pins the server public host key via
the local Docker control channel, not unauthenticated `ssh-keyscan`. Password,
keyboard-interactive, agent, X11 and remote forwarding are disabled. Local
forwarding is restricted to remote IPv4 loopback destinations. Use
`.fixture/env.sh` for the generated SSH port and key paths; do not commit it.
The public key is mounted at a staging path and copied to a root-owned mode-0600
server `authorized_keys`, so a non-root developer's bind-mount UID does not fail
OpenSSH `StrictModes`. Known-hosts paths are quoted for OpenSSH's own option
parser, including paths with spaces.

The tools' `up` command sets `REMOTE_HOST_NETWORK_PORT=26379`. This is an explicit
**Linux host-network fallback inside the isolated remote fixture**, not host
networking with the developer workstation. Nested bridge traffic was unavailable
in this sandbox: Docker published a port and Redis started, but the nested
bridge path timed out. With the fallback, local and remote loopbacks really are
different network namespaces and the live requests traverse SSH.

Without `REMOTE_HOST_NETWORK_PORT`, the prototype uses ordinary Docker
`--publish 127.0.0.1::6379`, inspects the allocated remote port and tunnels to it.
That bridge path has parsing/model coverage but **has not passed end-to-end
runtime validation here**. The verified supported reproduction is the fixture's
host-network journey. This is one physical developer environment, **not WAN,
latency, packet-loss, security-isolation or production-host validation**.

For a separately provisioned authorized development host, supply `REMOTE_HOST`,
`REMOTE_SSH_PORT`, `REMOTE_USER`, `REMOTE_IDENTITY`, `REMOTE_KNOWN_HOSTS` and,
for the same tested Linux host-network journey, `REMOTE_HOST_NETWORK_PORT`.
The remote user needs Docker CLI/daemon access and permission to forward TCP.
Select unused remote/local ports (`REMOTE_LOCAL_PORT` defaults to 16379).
No host address, identity or per-developer credential is checked into the model.

## Lifecycle, observability and cleanup

The resource uses repository-standard `OnInitializeResource`,
`BeforeResourceStartedEvent`, allocated endpoint/connection-string events,
`ResourceNotificationService`, `ResourceLoggerService`, relationships and
`WaitFor`. An explicitly owned hosted service awaits session cleanup on shutdown.
The cache explicitly stops waiting on an unavailable machine, including when
dashboard mode would otherwise default to waiting indefinitely for recovery.
The endpoint describes the **local tunnel**, including its local target port,
so DCP does not allocate a conflicting proxy or give local projects a remote
loopback address. Readiness requires accepted SSH forwarding, a protocol-level
Redis PONG and a running owned container, not merely an open TCP socket.

`REMOTE_HEADLESS=1` subscribes to the same notification/log services used by the
dashboard and prints JSON state/log records. Redis startup output, local/remote
addresses, owner/container identities, errors and cleanup commands are observable
there. Without that variable the model uses normal dashboard services; the
dashboard UI itself was not built or visually validated for this experiment.
The machine resource's state is a startup SSH/Docker preflight check, not a
continuous global host-health probe; the cache session is continuously monitored.

| Event | Behavior |
|---|---|
| Bad SSH/host key/engine | Machine and dependent resource fail explicitly; no local dependency fallback. |
| Bad image or occupied local forward port | `FailedToStart`; local API does not pass its wait gate; cleanup runs. |
| Tunnel/log session exits or Redis PING fails | `Unavailable`, inactive endpoint, local API returns HTTP 503; no silent reconnect. |
| SSH restored | Restart AppHost explicitly; a new owner/container/session is created. |
| Graceful AppHost cancellation | Kill only its SSH process; await label-checked removal of its exact container ID. |
| Remote host unreachable during cleanup | Log error and exact reconciliation command; attach `Cleanup required` property instead of claiming success. |
| SSH/cancellation interrupts remote creation | Creation may still settle remotely; even a successful cleanup snapshot is reported unconfirmed and requires reconciliation. |
| AppHost killed without shutdown | No durable lease/remote janitor; use the recorded owner/name to reconcile manually. |

Each run uses a random container name and `aspire.remote.owner` label. Cleanup
looks up that name, verifies the owner and removes the **captured immutable ID**,
not all containers, a global prefix, or a reused name. The integration verifier
also creates an unrelated sentinel container and proves it survives cleanup.
Removal includes only that container's associated anonymous volumes: the Redis
image declares `/data` as a volume even with persistence disabled. The verifier
asserts the nested engine's volume inventory is unchanged after each full run.

After the checks, remove the fixture and its nested workloads, anonymous Docker
data volume, generated image and ephemeral key files:

```sh
./.dotnet/dotnet "$tools" down
```

Only the container ID recorded in `.fixture/state.json` is eligible for fixture
removal, and its task label must match. Scenario logs and `evidence.json` stay in
ignored `.fixture/` for inspection; their generated identity files are removed.
For a real remote host, run the **exact label-checked cleanup command** from the
cache logs/properties after SSH recovers. Do not substitute a broad `docker rm`.

## Evidence and design implications

[verification.json](verification.json) records the completed six-scenario run.
Twenty-two focused tests pass, and the targeted AppHost/API build reports zero
warnings/errors. The scenario runner additionally checks the API's incremented
value directly inside Redis, native state transitions, log delivery, inactive
URLs, actual HTTP 503s, scoped cleanup and explicit restart recovery. It stops
the entire fixture for the remote-outage case, restores its engine, then
reconciles only the exact intentionally orphaned owned container.

A second, clean checkout of the published commit was also tested without
preexisting `.dotnet`, `artifacts` or `.fixture` directories. The targeted restore
downloaded the pinned SDK/runtime toolset; a fresh build, all focused tests and the
complete six-scenario run passed. Its fixture generated new identities and was
removed afterward. No files, keys or build outputs were copied from the original
checkout. This proves the committed-source journey on this Linux environment,
not a second physical host or an independent WAN topology.

Additional review regression checks used a public-key bind mount owned by UID
1000 and a real known-hosts path containing spaces; the complete SSH matrix
passed with `StrictModes` enabled and zero anonymous-volume leaks. An injected
AppHost shutdown reporting failure also confirmed the verifier's nested teardown
still restores the fixture and removes its sentinel/volumes.

The .NET fixture always uses the spaced filename `.fixture/known hosts`. To
repeat the non-root bind-mount check when running the tools as root, use
`up --uid1000` instead of `up` (it changes only the generated public key's UID/GID).
Ordinary non-root developers use `up` without this flag. To test teardown after a
shutdown reporting error, run `verify --inject-shutdown-failure`: it deliberately
exits nonzero after stopping the first AppHost, but its nested `finally` must
still print that the fixture was restored, exact IDs reconciled and volume
inventory unchanged. Run ordinary `verify` afterward for the complete matrix,
then `down`. Failed runs never overwrite successful `evidence.json`.

This builds on the discussion's CLI-based runtime abstraction guidance rather
than introducing another engine API client. The existing `SshRemoteOptions`
detects an AppHost itself running under VS Code Remote SSH; it does not place
selected local-AppHost dependencies remotely. `Aspire.Hosting.RemoteHost` is the
polyglot AppHost RPC host, not SSH container placement.

The experiment suggests per-developer **placement/connection profiles** with
explicit endpoint-network identities, session ownership and lifecycle semantics
are more important than only adding a `RunOnHost` method. A product implementation
should extend the existing DCP runtime/connection abstraction, with equivalent
Docker and Podman behavior and an explicit unsupported-runtime result, rather
than silently bypassing DCP for arbitrary container resources.

Remaining boundaries: Docker/OpenSSH/Linux only; Redis only; one TCP endpoint;
local project consumers only; ephemeral containers/no persistent volumes; no
bind mounts, image builds/context transfer, arbitrary environment/secrets,
registry auth, remote TLS services, automatic reconnect, remote garbage
collection, dashboard restart/stop commands, or cross-machine health guarantees.
SSH host users and Docker access are trusted. Loopback Redis has no password;
other users on the remote host can access it. The privileged fixture and mutable
image tags are development conveniences, not a secure untrusted-image sandbox.
