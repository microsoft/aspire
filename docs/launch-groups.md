# Launch groups

Launch groups select a named subset of an application's resources for a local run.
They work with containers, executables, .NET projects, and resources that honor
`WithExplicitStart`. They do not change publishing or deployment.

```csharp
builder.AddLaunchGroups("catalog", "basket", "frontend");

var database = builder.AddPostgres("database").WithLaunchGroup("catalog");
var cache = builder.AddRedis("cache").WithLaunchGroup("basket");
var catalog = builder.AddDotnetProject("catalog", "../Catalog/Catalog.csproj")
    .WithLaunchGroups("catalog", "frontend")
    .WithReference(database)
    .WaitFor(database);
```

`WithLaunchGroup` and `WithLaunchGroups` are additive. Names are case-insensitive.
Declare valid names with `AddLaunchGroups`, the root `LaunchGroups` configuration
array, or both. Without an explicit list, membership defines the valid groups.
Undefined membership and invalid selections fail before resources start. Select
exactly one name, not a comma- or semicolon-separated list.

## Selecting a group

Use `--launch-group` with `aspire run` or `aspire start`. The option works with C#
and polyglot AppHosts, including detached runs and AppHost watch/reload.

```powershell
aspire start --apphost playground\TestShop\TestShop.AppHost\TestShop.AppHost.csproj --launch-group catalog --isolated
aspire run --apphost .\apphost.mts --launch-group basket
```

The option overrides the AppHost's root `LaunchGroup` configuration value from
environment variables, JSON, and launch profiles. Without the option, standard
AppHost configuration still applies:

```powershell
$env:LaunchGroup = "catalog"
aspire start --apphost playground\TestShop\TestShop.AppHost\TestShop.AppHost.csproj --isolated
```

For IDE run/debug, put `"LaunchGroup": "catalog"` in the AppHost launch profile's
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
and messaging and a deferred .NET build. Stop and clear `LaunchGroup` (or choose
the ordinary `https` launch profile) for a full run.
