# Chaos YARP-per-edge pilot (reference only)

> **Not for merge.** This is a snapshot of an internal pilot, shared as a reference for the proxy/fault-injection work tracked in #19160. It isn't wired into the Aspire build.

## What it is

A reverse-proxy container (YARP-based) that Aspire inserts on each dependency edge between resources. Each edge shows up as its own resource in the app model, so traffic and logs are visible per edge, and faults can be injected deterministically on a specific edge.

## Layout

| Path | What's there |
|---|---|
| `src/Aspire.Hosting.Chaos/` | Hosting integration: `ChaosProxyResource`, builder extensions, and the mesh that discovers edges from service discovery and connection strings (`Mesh/`) |
| `src/Aspire.Hosting.Chaos/container/` | The proxy itself: fault middleware (error, latency, drop, partial response, rate limit, replay, and so on), the policy store and remote-control endpoints (`ChaosEndpoints.cs`), fault profiles, and telemetry |

## How it maps to the split discussed on 8/19

- **Aspire core candidates:** the proxy resource and annotation, edge discovery, and the proxy remote-control protocol.
- **Azure package candidates:** the Azure fault profiles (`container/Policy/Profiles/azure.*.json`). The Azure-specific hosting extensions aren't included here.
- **Chaos layer on top:** policies, scenarios, and the verification experience.

## Caveats

- It targets Aspire 13.3 and was built for our own services, so treat it as a reference for the shape rather than a design.
- The container image registry is a placeholder. The proxy builds locally from its Dockerfile.
- Tests aren't included.
