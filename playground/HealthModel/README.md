# Health model playground

A container-free sample for iterating on the dashboard's resource graph and health model.
The resources are simulated; the database servers, APIs, and gateways do not start real services.

From the repository root, run:

```powershell
dotnet run --project playground\HealthModel\HealthModelSandbox.AppHost\HealthModelSandbox.AppHost.csproj
```

Open the login URL printed by the AppHost, then select **Health**.
The AppHost uses the repository's dashboard so local UI changes are included.
Keep the terminal open while using the playground; stop it with **Ctrl+C**.

## Topology and health

Each resource has one incoming relationship. APIs reference their branch's database server;
the database declares that server as its parent. This avoids a database appearing under both
the API and a separate top-level server.

```text
AppHost
  storefront
    checkout-api
      orders-db-server
        orders-db
      payments-gateway       Degraded
    catalog-api
      catalog-db-server
        catalog-db
      search-index           Unhealthy
    identity-api
      identity-cache
```

All other resources report Healthy. Once startup completes, the graph should show:

| Entity | Aggregate health | Cause |
|---|---|---|
| checkout-api | Degraded | payments-gateway |
| catalog-api | Unhealthy | search-index |
| identity-api | Healthy | Healthy dependencies |
| storefront / AppHost | Unhealthy | catalog-api |

To try a different scenario, change a leaf's `HealthStatus` in `HealthModelScenario.cs` and restart.
Both **Health** and **Resources > Graph** derive relationships from the AppHost. Health no
longer invents Services/Infrastructure groups or automatically limits the impact of containers.

## Health model lite

The Health page follows the
[Azure Monitor graph and entity inspection workflow](https://learn.microsoft.com/azure/azure-monitor/health-models/analyze-health)
and a restricted version of its
[designer](https://learn.microsoft.com/azure/azure-monitor/health-models/designer).

- **Graph** shows live, aggregated health on rectangular entity cards. Select a card to see
  its own signals, dependencies, parents and the health it propagates.
- **Entities** presents the same entities as a searchable list. State-count buttons filter
  the list or dim nonmatching cards without removing relationship context.
- **Designer** lets you move cards, edit their display names, impact, health objectives and
  dependency rollup rules. Apply entity edits to the draft, then use **Save changes** to persist.
- **Arrange** restores a deterministic hierarchical layout. **Undo** and **Discard changes**
  operate on the draft; live health updates do not reset the layout.
- Drag cards only in Designer, or focus a card and use arrow keys to move it (Shift moves
  further). Positions are canvas coordinates, not screen pixels or zoom transforms.

Saved settings are scoped to the application in this browser's local storage. They are
not automatically written into the AppHost project and are not shared across browsers.
Export the saved model to keep a project-owned copy.

### Portable model definition

**Export model** downloads `aspire-healthmodel.json`. Add that file to your AppHost project
when you want to retain the design alongside your code. **Import model** restores its
positions and propagation settings as an unsaved draft. Imports must match the current
AppHost's application name, resource bindings, entities, relationships and local signals.
Changing topology remains an AppHost operation, not a browser-only edit.

The version 1 document contains:

| Field | Purpose |
|---|---|
| `name` | Model identifier; also identifies the root entity |
| `applicationName` | Prevents applying another application's settings |
| `entities[].name` | Stable, Azure-compatible identity, independent of runtime suffixes |
| `aspireResourceName`, `replicaIndex` | Binding back to the corresponding AppHost resource |
| `canvasPosition` | Saved X/Y coordinates, preserved by export and import |
| `impact`, `dependencies`, `healthObjective` | Declarative health propagation settings |
| `localSignals` | Local signal names and kinds, without readings or exception details |
| `relationships` | Parent and child entity identities |

No environment variables, credentials, endpoint addresses, observed health values, or
exception text are exported. Root identity, topology and positions are deliberately separate
from the live signal readings.

### Publish the model

The AppHost opts into the experimental `Aspire.Hosting.Azure.HealthModels` integration in publish
mode. Local startup remains container-free and never provisions Azure.

The checked-in `HealthModelSandbox.AppHost\aspire-healthmodel.json` contains the default 12-entity,
11-relationship design with the same stable IDs and positions as the local dashboard. To publish
your edits, **Save changes**, **Export model**, and replace that project-owned file. Browser storage
is not read by the publisher. After changing relationships or check registrations, re-export the
definition; stale bindings fail publishing rather than silently dropping checks.

From the repository root, generate the assets without Azure credentials or cloud mutations:

```powershell
aspire publish --apphost .\playground\HealthModel\HealthModelSandbox.AppHost\HealthModelSandbox.AppHost.csproj --output-path .\artifacts\health-model-publish --non-interactive
```

For testing the CLI built from this checkout, replace `aspire` with
`dotnet .\artifacts\bin\Aspire.Cli\Debug\net10.0\aspire.dll`.

| Output | Purpose |
|---|---|
| `main.bicep` | Infrastructure entrypoint using Aspire's existing Azure publisher |
| `health\health.bicep` | AHM, entities, relationships, 22 signal definitions, AMW, DCE, DCR and RBAC |
| `health-metrics\health-metrics.bicep` | Internally accessible metrics producer, kept at one replica |
| `health-collector\health-collector.bicep` | Prometheus collector with managed-identity remote write |
| `azure\`, `azure-acr\` | Container Apps environment and registry managed by Aspire |

The compute modules are separate from `main.bicep`, as in the existing Azure publishing pipeline.
Their image and infrastructure parameters remain deferred. `aspire deploy` is the operation that
builds/pushes the producer image, provisions infrastructure, and applies the compute modules; running
only `main.bicep` will not deploy the producer or collector.

The AHM module targets **`Microsoft.CloudHealth/healthmodels@2026-09-01-preview`**. The health
model is a separate Azure resource, not a workspace child. It preserves entity identities, directed
edges, fractional coordinates, impact, objectives and dependency aggregation. The identities get
no broad owner/contributor permissions: the model has **Monitoring Reader** on the AMW and the
collector has **Monitoring Metrics Publisher** on the DCR.

### Health signals in Azure

`HealthModelScenario.cs` owns both the simulated topology and its validators. The AppHost and
`HealthModel.Metrics` register the same checks with `HealthCheckService`. `/metrics` executes
those checks and reports each resource's own status, not its already-aggregated parent status:

```text
aspire_health_status{resource_name="payments-gateway",health_check="payments-gateway_check",replica_index="1"} 1
aspire_health_status{resource_name="search-index",health_check="search-index_check",replica_index="1"} 0
```

The producer emits 22 series: 11 check results plus 11 lifecycle results. Values are **2 Healthy,
1 Degraded, 0 Unhealthy**. Simulated lifecycle states are Healthy while the producer is serving;
they do not claim to monitor real databases. `/alive` reports process liveness independently of
the deliberately unhealthy sample entities.

Prometheus scrapes every 30 seconds and remote-writes through the DCE/DCR into the AMW using a
dedicated user-assigned identity. The model evaluates one `PrometheusMetricsQuery` signal per
binding, once per minute: `< 1` is Unhealthy and `< 2` is Degraded. AHM performs parent rollup using
the saved dependency policies. No credentials or health-report exception details appear in the metrics.

To inspect the producer alone:

```powershell
dotnet run --project .\playground\HealthModel\HealthModel.Metrics\HealthModel.Metrics.csproj --no-launch-profile -- --urls http://localhost:5088
```

### Deployment boundary

Deployment requires an Azure subscription/region supporting the current CloudHealth API and managed
Prometheus, Container Apps, a working container-image build path, and permission to create resources
and role assignments. The generated model uses authenticated public-network ingestion; private-link
configuration and existing-workspace reuse are outside this first version.

This remains a simulation. Arbitrary AppHost delegates are not automatically copied into deployed
workloads. For a real application, instrument the workload or supply an equivalent probe that emits
the documented resource/check/replica labels. The sample's logical database entities deliberately do
not pretend to have deployed database ARM IDs.

Missing, invalid or older-than-three-minute measurements produce an empty PromQL result instead of
manufactured Healthy values. AHM's empty-result/Unknown transitions, RBAC propagation and Azure portal
coordinate anchoring still require service-level validation. Saved numeric coordinates are preserved,
but identical pixels and full local/cloud evaluation parity are not yet claimed.

Relationship endpoints use entity names; rewiring creates a replacement relationship. The first
version uses incremental deployment, so removing entities, relationships or definitions from the
file does not automatically delete their old Azure resources. Use a fresh model for topology-removal
experiments or explicitly remove obsolete resources before comparing graphs. Deletion reconciliation
is not implemented.

The generated Azure resource group owns the sample infrastructure. Use the application's normal
Azure teardown/resource-group cleanup workflow when finished; publishing itself creates no Azure
resources and needs no cloud cleanup.

The local dashboard does not include historical timelines, alert delivery, Azure discovery,
remote Azure health reads or arbitrary browser-defined entities and relationships.
Cyclic AppHost references remain inspectable in **Resources > Graph**, but the Health
designer reports them as unsupported rather than silently dropping edges. Requiring an acyclic,
root-connected topology is a local lite-product restriction, not a claim that Azure prohibits
every other topology.

## Resource graph controls

- Drag a node to position and pin it. Physics pauses while dragging, allowing overlap;
  after release, neighbours separate. Dropping onto an older pinned node releases that older pin.
- Double-click a pinned node to release it.
- Zoom with the wheel or the zoom buttons; drag the background to pan.
- **Reset** clears pins, restores the hierarchical layout, and fits all nodes and labels into view.
- Focus a resource's action cog and press **Enter** to use its menu with the keyboard;
  **Escape** closes the menu and returns focus to the cog.

Known health states keep their colour on hover and selection: solid relationship lines,
full-colour node outlines, and a 30% background tint.
