# Launch groups

Launch groups select a named subset of an application's resources for a local run.
They work with containers, executables, .NET projects, and resources that honor
`WithExplicitStart`. They do not change publishing or deployment.

The public launch-group APIs are experimental and may change or be removed.
Suppress `ASPIRELAUNCHGROUPS001` to opt in when calling them from C#.

```csharp
#pragma warning disable ASPIRELAUNCHGROUPS001

var database = builder.AddPostgres("database").WithLaunchGroup("catalog");
var cache = builder.AddRedis("cache").WithLaunchGroup("basket");
var catalog = builder.AddDotnetProject("catalog", "../Catalog/Catalog.csproj")
    .WithLaunchGroup("catalog")
    .WithReference(database)
    .WaitFor(database);
```

`WithLaunchGroup` and `WithLaunchGroups` are additive. Names are case-insensitive.
Valid group names are inferred from `WithLaunchGroup` and `WithLaunchGroups`
membership. To explicitly define the valid names, including groups with no members,
use the `Aspire:LaunchGroups` configuration array. Configuration definitions enable
launch group support automatically, without any launch group API calls.
`WithLaunchGroup` and `WithLaunchGroups` also register the required support.
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

Omit the selection to preserve the existing full-application run.

## Startup behavior

Selecting a launch group only mutates the application model before startup.
Unless opted out as described below, every resource without explicit membership
in the selected group is marked with `WithExplicitStart`, including resources in
other groups and resources with no group membership. Existing explicit-start
settings are preserved, even for members of the selected group.

Membership is not inherited from parents or inferred from dependencies,
references, or connection strings. Assign every resource that should start to the
group explicitly. Use `WithLaunchGroups` for resources shared by multiple groups.
Selection does not evaluate environment or argument callbacks.

After selection, normal explicit-start behavior applies. Existing `WaitFor`,
`WaitForStart`, and `WaitForCompletion` relationships still control readiness and
ordering, but do not select or automatically start dependencies. A selected
resource can therefore wait for an unselected dependency until that dependency is
started manually.

Start, stop, restart, health monitoring, persistent-resource reuse, and project
builds behave exactly as they do with ordinary `WithExplicitStart` resources.
Starting an excluded resource does not start its dependencies or other group
members. Launch groups do not introduce build deferral or build serialization.

Resources marked with `ExcludeFromLaunchGroupSelectionAnnotation` keep their
existing startup configuration regardless of group membership. The annotation
does not remove an existing `WithExplicitStart` setting. It is currently applied
only to the internal .NET coordinated build resources, so builds still run
automatically before selected projects launch. The existing shared build plan may
also build projects that are not selected to run; launch groups do not change
which projects the build coordinator builds.

The selection lasts for the AppHost session. Watch/rebuild of a running resource
retains the selection; restarting/reloading the AppHost recomputes it. Editing the
configuration does not dynamically switch groups in an already running AppHost.

## TestShop

The `catalog` group contains PostgreSQL, its admin resource, catalog initialization,
and the replicated catalog service. Redis, messaging, basket, orders, frontend,
and the gateway are marked for explicit start.

The `basket` group contains Redis and its admin resources, messaging, and the basket
service. The `frontend` group includes the frontend and gateway, both services,
and their database/cache/messaging dependencies, but not the order processor.
The `orders` group contains messaging and the order processor.

The shared .NET build runs automatically before the selected projects launch.

To start `basketservice` after a catalog-only run, manually start its dependencies
(`basketcache` and `messaging`) as well. Stop and clear `Aspire:LaunchGroup` (or choose
the ordinary `https` launch profile) for a full run.
