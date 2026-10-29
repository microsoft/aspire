# Launch groups

Launch groups select a named subset of an application's resources for a local run.
They work with containers, executables, .NET projects, and resources that honor
`WithExplicitStart`. They do not change publishing or deployment.

```csharp
builder.DefineLaunchGroups("catalog", "basket", "frontend");

var database = builder.AddPostgres("database").WithLaunchGroup("catalog");
var cache = builder.AddRedis("cache").WithLaunchGroup("basket");
var catalog = builder.AddDotnetProject("catalog", "../Catalog/Catalog.csproj")
    .WithLaunchGroups("catalog", "frontend")
    .WithReference(database)
    .WaitFor(database);
```

`WithLaunchGroup` and `WithLaunchGroups` are additive. Names are case-insensitive.
Define valid names with `DefineLaunchGroups`, the `Aspire:LaunchGroups` configuration
array, or both. `DefineLaunchGroups` requires at least one name. Configuration
definitions enable launch group support automatically, without any launch group API
calls. `WithLaunchGroup` and `WithLaunchGroups` also register the required support.
Without explicit definitions, membership defines the valid groups.
Undefined membership and invalid selections fail before resources start. Select
exactly one name, not a comma- or semicolon-separated list.
Selecting a group when no names or resource memberships are defined also fails
before resources start; it does not fall back to running the full application.

For example, define groups in the AppHost's `appsettings.json`:

```json
{
  "Aspire": {
    "LaunchGroups": ["catalog", "basket", "frontend"],
    "LaunchGroup": "catalog"
  }
}
```

`Aspire:LaunchGroups` defines the valid names; resource membership is still configured
with `WithLaunchGroup` or `WithLaunchGroups`. `Aspire:LaunchGroup` selects the active
group. Definitions added to the builder's configuration before
`Build()` are also supported. Configuration changes do not switch a running session.

## Selecting a group

Use `--launch-group` with `aspire run` or `aspire start`. The option works with C#
and polyglot AppHosts, including detached runs and AppHost watch/reload.

```powershell
aspire start --apphost playground\TestShop\TestShop.AppHost\TestShop.AppHost.csproj --launch-group catalog --isolated
aspire run --apphost .\apphost.mts --launch-group basket
```

The option overrides the AppHost's `Aspire:LaunchGroup` configuration value from
environment variables, JSON, and launch profiles. Without the option, standard
AppHost configuration still applies:

```powershell
$env:Aspire__LaunchGroup = "catalog"
aspire start --apphost playground\TestShop\TestShop.AppHost\TestShop.AppHost.csproj --isolated
```

For IDE run/debug, put `"Aspire__LaunchGroup": "catalog"` in the AppHost launch profile's
`environmentVariables`. TestShop provides `https_catalog`, `https_basket`, and
`https_frontend` profiles. The same configuration contract applies regardless of
whether the CLI, Visual Studio, or VS Code launches the AppHost. The CLI option is
also preserved when an Aspire terminal delegates run/debug to VS Code.

Omit the selection to preserve the existing full-application run. Ungrouped
resources always participate. Ungrouped children inherit their parent's membership;
explicit child membership overrides that inheritance.

## Dependencies and additional starts

Selection includes the transitive closure of wait dependencies, reference
relationships, parents, and connection-string redirects/expressions. This can
include resources assigned to other groups. Existing `WaitFor`, `WaitForStart`,
and `WaitForCompletion` relationships continue to control readiness and ordering;
references alone do not add a health wait.

Runtime environment/argument callbacks are not evaluated during selection, since
they can wait on values that only exist after startup. If a callback introduces
resource references, declare them with `WithReferenceRelationship` or a wait
relationship as well.

Unselected resources remain visible as **Not started**, with their normal lifecycle
commands. Starting an additional resource from the dashboard or CLI starts it and
its not-started/stopped dependencies, without restarting running dependencies or
replaying successful completed tasks. Starts are scheduled together, with existing
wait relationships controlling readiness; references alone do not impose startup
ordering. It does not select the entire resource's group. Intentional `WithExplicitStart`
settings remain in effect at initial startup, even for group members or dependencies;
explicit late starts include their dependencies.

`AddDotnetProject` uses Project Resource v2's coordinated build plan. Only initially
selected projects are included in the startup batch, with their MSBuild project
references. Excluded projects have dormant build resources and build when started.
Initial builds, late builds, and project rebuilds are serialized during partial runs
to avoid racing on shared outputs. Launch groups do not provide file isolation:
projects intentionally sharing incompatible output paths remain unsupported.

The selection lasts for the AppHost session. Watch/rebuild of a running resource
retains the selection; restarting/reloading the AppHost recomputes it. Editing the
configuration does not dynamically switch groups in an already running AppHost.

## TestShop

The `catalog` group starts PostgreSQL, its admin resource, catalog initialization,
and the replicated catalog service. Redis, messaging, basket, orders, frontend,
and the gateway remain unstarted.

The `basket` group starts Redis and its admin resources, messaging, and the basket
service. The `frontend` group includes the frontend and gateway, both services,
and their database/cache/messaging dependencies, but not the order processor.
The `orders` group starts messaging and the order processor.

Start `basketservice` after a catalog-only run to exercise expansion into Redis
and messaging and a deferred .NET build. Stop and clear `Aspire:LaunchGroup` (or choose
the ordinary `https` launch profile) for a full run.
