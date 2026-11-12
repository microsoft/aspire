# Launch groups

Launch groups select a named subset of an application's resources for a local run.
They work with containers, executables, .NET projects, and resources that honor
`WithExplicitStart`. Selection controls automatic startup; it does not pull required
dependencies into the group. Launch groups do not change publishing or deployment.

The public launch-group APIs are experimental and may change or be removed.
Suppress `ASPIRELAUNCHGROUPS001` to opt in when calling them from C#.

```csharp
#pragma warning disable ASPIRELAUNCHGROUPS001

var database = builder.AddPostgres("database").WithLaunchGroups("catalog");
var cache = builder.AddRedis("cache").WithLaunchGroups("basket");
var catalog = builder.AddDotnetProject("catalog", "../Catalog/Catalog.csproj")
    .WithLaunchGroups("catalog")
    .WithReference(database)
    .WaitFor(database);
```

`WithLaunchGroups` accepts one or more names and is additive. Names are case-insensitive.
Valid group names are inferred from resource membership.
To explicitly define the valid names, including groups with no members,
use the `Aspire:LaunchGroups` configuration array. Configuration definitions enable
launch group support automatically, without any launch group API calls.
`WithLaunchGroups` also registers the required support.
Undefined membership and invalid selections fail before resources start. Select
exactly one scalar name, not an array, object, or comma- or semicolon-separated list.
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
with `WithLaunchGroups`. `Aspire:LaunchGroup` selects the active
group. Definitions added to the builder's configuration before
`Build()` are also supported. Configuration changes do not switch a running session.

## Selecting a group

Use `--launch-group` with `aspire run` or `aspire start`. The option works with C#
and polyglot AppHosts, including detached runs and AppHost watch mode.

C# AppHosts require Aspire.Hosting 17.1 or later, including 17.1 previews.
The CLI rejects an explicit selector for older AppHosts before starting them,
rather than silently starting the full application.

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
`https_frontend` profiles. The CLI option is also preserved when an Aspire terminal
delegates run/debug to VS Code.

Omit the selection to preserve the existing full-application run.

## Startup behavior

Selected members remain eligible for automatic startup unless they are already
marked for explicit start. Resources excluded from launch-group selection keep their
existing startup configuration. Other resources stay in the model and are marked
for explicit start, including resources in other groups and resources with no group
membership. Group membership does not override an existing `WithExplicitStart`.
Integrations that initialize independently and do not honor `WithExplicitStart`,
such as cloud provisioning, can still run when their resources are outside the
selected group. Discovery reports the selection decision, not whether an
integration actually initialized.

### Membership, inheritance, and overrides

Structural children (`IResourceWithParent`) without their own groups inherit their
parent's effective memberships, including through multiple levels. A child's own
groups replace that inherited default. `WithParentRelationship` display links,
references, waits, and connection-string redirects do not provide membership or
select dependencies. Use `WithLaunchGroups` for resources shared by multiple groups.

For example, `catalogdb` inherits both groups from its PostgreSQL server, while
`operationsdb` declares its own group and replaces the inherited default:

```csharp
var postgres = builder.AddPostgres("postgres").WithLaunchGroups("catalog", "frontend");
var catalogDb = postgres.AddDatabase("catalogdb");
var operationsDb = postgres.AddDatabase("operationsdb").WithLaunchGroups("operations");
```

The same lookup is recursive. A structural child below `catalogdb` with no direct
membership also resolves to `postgres`. A child below `operationsdb` resolves to the
nearer `operationsdb` declaration instead. This does not start or select `postgres`
if it is omitted: a selected resource that requires either database still requires
its server to be selected and automatically startable.

Membership can describe more than one useful run without making a resource automatic:

```csharp
var messaging = builder.AddRabbitMQ("messaging")
    .WithLaunchGroups("basket", "orders", "frontend");

var diagnostics = builder.AddExecutable("diagnostics", "dotnet", ".")
    .WithLaunchGroups("catalog", "frontend")
    .WithExplicitStart();
```

`messaging` is eligible in any of its three groups. `diagnostics` remains user-started
in both groups. Its memberships are available to discovery tools, but selecting either
group does not start it or its dependencies.

Before startup, Aspire validates required, declaratively recorded local dependencies
of automatically starting resources, including transitive dependencies. These include
reference relationships, `WaitFor`, `WaitForStart`, `WaitForCompletion`, and
connection-string redirects. If a required local dependency would not start
automatically, startup fails with the full dependency path from the automatically
starting resource to the blocked resource. The path identifies reference, wait,
connection-string redirect, and passive-child lifetime-owner hops. This includes
dependencies marked with `WithExplicitStart`, even if they belong to the group.
Parameters, connection-string resources, external services, and standalone resources
without a lifetime are not local execution dependencies. Cloud resources initialized
independently of local processes are not local execution dependencies either, but an
Azure resource running as a local emulator is. A reference to a passive child can
require its local lifetime owner; select the owner, not the child, for automatic
startup. A `WaitForCompletion` on a resource without a lifetime is ignored. `WaitFor`
and `WaitForStart` can still add a separate wait for its parent.

For example, if `frontend` references `database` and waits for its excluded
`server`, the error identifies the full path and the wait:

```text
Resource 'frontend' requires local resource 'server' (the lifetime owner of 'database'), which is not included in launch group 'frontend'. Dependency path: frontend -> database -> server. Edges: frontend --reference--> database; database --lifetime owner--> server. Add 'server' to launch group 'frontend'. The wait from 'frontend' to 'server' requires automatic startup.
```

Add required dependencies to the group; they are not added automatically.
`WithReference` does not imply a readiness wait: use `WaitFor` when the dependent
must wait for readiness. If a dependency is genuinely optional at startup, use
`WithOptionalLaunchGroupReference(dependency)` on the dependent resource. This
marks the reference optional for launch-group validation; it does not remove a
separate `WaitFor` or make the application able to run without the dependency.

Preflight does not execute `WithEnvironment` or `WithArgs` callbacks. Declarative
arguments passed to `AddExecutable` or `WithArgs` record their references. A
dependency used only inside a callback must also be declared with
`WithReferenceRelationship` to be validated. The guarantee covers declared model
dependencies, not arbitrary callback code.

After startup, manual Start and Restart use the ordinary resource lifecycle. They
do not enforce the group or automatically start dependencies. Start those
dependencies yourself if the resource needs them.

### Startup summary

For a valid selection, the AppHost logs the finalized startup plan before resources
start. The summary separates visible local execution resources eligible for automatic
startup, excluded by the selected group, or already configured for explicit start.
A second summary lists coordinated .NET builds planned initially and builds deferred
until a later cold Start.

For example, with three executable resources and no coordinated .NET builds:

```text
Launch group 'frontend' (visible resources): eligible for automatic startup: frontend; excluded by group: backend, manual; explicit start: manual.
Coordinated builds: initial: (none); eager for persistent resources: (none); shared with initial builds: (none); deferred: (none). AppHost project-reference builds may also run eagerly.
```

The summary reports decisions, not dependency activation. A required dependency that
is missing from the group still fails preflight instead of moving into the eligible
list. Effective-persistent projects and compatible registrations that share one
physical project identity can appear in the initial build list even when one resource
is excluded. Legacy AppHost project references can also build eagerly outside the
coordinated plan.

Resources marked with `ExcludeFromLaunchGroupSelectionAnnotation` keep their
existing startup configuration regardless of group membership. The annotation
does not remove an existing `WithExplicitStart` setting. It is currently applied
to the internal .NET coordinated build resources, the framework dashboard, and
per-replica terminal support hosts. Initial builds and the dashboard retain their
automatic startup; deferred builds start on demand. Terminal hosts remain
available even for unselected workloads, so those workloads can still be started
manually later without starting hidden dependencies separately. Other hidden
resources do not automatically opt out.

The selection lasts for the AppHost session. Watching, rebuilding, or restarting a
resource retains the selection. Restarting the AppHost process recomputes it;
in-process hot reload does not rerun startup. Editing the configuration does not
dynamically switch groups in an already running AppHost.

## Inspecting a running AppHost

Use `aspire describe --launch-groups` while an AppHost is running to inspect the
finalized plan:

```bash
aspire describe --launch-groups
```

The output includes the selected group, known groups, each resource's effective
memberships and inheritance source, pre-existing explicit-start and group-exclusion
reasons, and its coordinated build type. It also includes hidden framework
helpers; some, such as project rebuilders, have their own explicit-start setting.
`Default startup` means there is no direct launch-group or explicit-start
restriction; passive structural children still follow their lifetime owner.
Use the existing `--format json` convention for machine-readable output:

```bash
aspire describe --launch-groups --format json
```

An older AppHost that does not expose launch-group metadata is reported as unsupported;
the CLI does not infer a plan from resource state. Both forms are read-only. They do
not evaluate environment or argument callbacks, start resources, trigger deferred
builds, switch the selected group, or mutate the application model.

The Dashboard Resources page also shows effective launch-group memberships and can
filter the current resource list by group. Inherited groups are shown the same as
direct memberships, and a resource with multiple memberships can match each group.
Filtering only changes the visible rows. It does not change automatic startup,
lifecycle controls, or the AppHost's selected group.

Resource details show **Launch groups** (the resource's effective memberships)
in both Status and Properties, without repeating the AppHost's active group.
Hover over or focus a group to see its effective members, including
resources that inherit membership. Hidden members appear when **Show hidden resources**
is enabled. Details also show the inheritance source,
and explicit-start setting when relevant. For a resource excluded from initial
startup, **State details** explains **Not in active launch group 'catalog'**.
If its build is deferred, the explanation adds **, deferred build**.
Hover over or focus the active group's name in the explanation to see its members.
After a manual Start, state details describe the current lifecycle state instead,
including any startup failure, rather than continuing to show the initial exclusion.

The JSON metadata uses one `exclusionReason` enum: `None`, `NotInSelectedGroup`,
or `ExemptFromSelection`. Exempt resources retain their ordinary startup behavior.
`explicitStart` remains independent, and `buildType` reports the coordinated build
classification.

## Project builds

When a group is selected, the AppHost's .NET build coordinator initially builds
automatically starting coordinator-owned project roots and their MSBuild
`ProjectReference` dependencies. An unrelated, excluded coordinator-owned project
root is not built initially. Compatible registrations of the same physical project
share one coordinated build, including when that build is deferred until cold Start.
A shared project identity is built initially when any compatible registration needs
it initially, even if another registration is excluded from automatic startup.
A compile-time reference can still build a project whose resource is excluded from
runtime startup; building it does not start that resource.

An ordinary Start of an excluded, never-built non-persistent .NET project builds
it before launch. A separate Rebuild is not required. Build-completion consumers,
such as EF migration commands, trigger or join the needed build without waiting
for the application to be ready. While a deferred build runs, the starting resource
shows Building and its console receives the build output. A resource waiting on a
dependency remains Waiting until that dependency is ready.

Internal deferred build resources are hidden from default resource lists before
they start and after a successful build. They remain visible while building or
after a failure. The excluded application resources remain visible and startable.

Coordinator-owned output-writing builds are conservatively serialized. This includes
initial builds, deferred cold-Start builds, and Rebuild commands, so separate build
processes do not race while writing shared `bin` and `obj` directories.

There are eager cases that do not imply automatic runtime startup:

- Legacy AppHost `ProjectReference` projects build as part of the AppHost build.
- .NET resources with an effective persistent lifetime, including an inherited
  lifetime, build eagerly because DCP registration needs their build-produced run
  properties. They do not automatically start just because they were built.
- Compatible registrations that resolve to one physical project share its build.
  An initially required registration can therefore make that shared build eager for
  an excluded registration too.

Build ownership remains capability-based. An extension host that owns project builds
does not also use AppHost-owned coordinated builds. Without a selected group, normal
full-run build behavior applies. Publishing is unchanged. Fully lazy persistent and
legacy builds are not included.

### Try selective builds and cold Start

In an AppHost with `Aspire.Hosting.Dotnet`, use two independent .NET projects and
no AppHost `ProjectReference` to either project. Replace the paths with your
projects. Run the commands from the AppHost directory.

```csharp
#pragma warning disable ASPIRELAUNCHGROUPS001

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDotnetProject("alpha", "../Alpha/Alpha.csproj").WithLaunchGroups("alpha");
builder.AddDotnetProject("beta", "../Beta/Beta.csproj").WithLaunchGroups("beta");
builder.Build().Run();
```

```bash
aspire run --launch-group alpha
```

With AppHost-owned builds, the initial coordinated build includes `alpha`, not
the unrelated `beta` root. Check the build output; previously existing artifacts
are not evidence that a project built in this run. Start `beta` from the dashboard,
or in another terminal:

```bash
aspire resource beta start
```

The cold Start builds `beta` before launching it. To try the failure path, introduce
a compile error in `Beta`, start it, fix the error, and select Start again. The
new attempt retries the build before launching.

### Compile references and runtime dependencies

If `Alpha.csproj` has a `ProjectReference` to `Shared.csproj`, MSBuild builds
`Shared` with `alpha`, even if a separate resource for `Shared` is outside the
selected group. That compile-time reference does not start the resource or create
a runtime readiness wait.

For a runtime dependency, declare membership and readiness separately. In these
snippets, `Backend` is a web project with an `http` launch-profile endpoint.
Use either snippet in place of the resources in the earlier AppHost:

```csharp
var backend = builder.AddDotnetProject("backend", "../Backend/Backend.csproj")
    .WithLaunchGroups("frontend");
builder.AddDotnetProject("frontend", "../Frontend/Frontend.csproj")
    .WithLaunchGroups("frontend")
    .WithReference(backend)
    .WaitFor(backend);
```

```bash
aspire run --launch-group frontend
```

If the frontend only uses the backend inside a callback, use this alternative and
declare the relationship explicitly so preflight can see it:

```csharp
var backend = builder.AddDotnetProject("backend", "../Backend/Backend.csproj")
    .WithLaunchGroups("frontend");
builder.AddDotnetProject("frontend", "../Frontend/Frontend.csproj")
    .WithLaunchGroups("frontend")
    .WithReferenceRelationship(backend.Resource)
    .WithEnvironment(context => context.EnvironmentVariables["BACKEND_URL"] = backend.GetEndpoint("http"));
```

This relationship does not wait for readiness. Add `.WaitFor(backend)` if needed.
If `backend` is instead assigned only to another group, selecting `frontend`
fails preflight rather than starting it implicitly.

### Try the eager exceptions

With the independent `alpha` and `beta` example above, a legacy AppHost project
reference to `Beta.csproj` makes Beta part of the AppHost build even when `alpha`
is selected. Add this to an `ItemGroup` in the AppHost project:

```xml
<ProjectReference Include="../Beta/Beta.csproj" />
```

Alternatively, remove that AppHost reference and replace the earlier `beta`
registration with:

```csharp
#pragma warning disable ASPIREPERSISTENCE001

builder.AddDotnetProject("beta", "../Beta/Beta.csproj")
    .WithLaunchGroups("beta")
    .WithPersistentLifetime();
```

```bash
aspire run --launch-group alpha
```

Beta builds eagerly in either case, but remains explicitly started when it is
outside the selected group.

## TestShop

TestShop uses its PostgreSQL resources to show the difference between structural
inheritance and UI relationships. The `postgres` server declares `catalog` and
`frontend`; its structural `catalogdb` child has no direct declaration and inherits
both. pgAdmin has a management relationship to PostgreSQL rather than a structural
parent, so it declares only `catalog` directly.

The `catalog` group contains PostgreSQL, pgAdmin, catalog initialization, and the
replicated catalog service. Redis, messaging, basket, orders, frontend, and the
gateway are marked for explicit start.

The `basket` group contains Redis and its admin resources, messaging, and the basket
service. The `frontend` group includes the frontend and gateway, both services,
and their database/cache/messaging dependencies, but not the order processor.
The `orders` group contains messaging and the order processor.

`messaging` demonstrates direct membership in `basket`, `orders`, and `frontend`.
`activity-generator` declares `frontend` membership but also uses
`WithExplicitStart`, so it remains user-started. Redis Commander and Redis Insight
also declare `basket` directly; their management relationships do not inherit the
Redis server's `frontend` membership.

TestShop's AppHost has legacy `ProjectReference` entries for its projects, so it
is not a clean demonstration of deferred initial builds. Its `catalogservice`
and `basketservice` both reference `TestShop.ServiceDefaults` at compile time;
that does not make either service a runtime dependency of the other.

While TestShop is running, inspect the effective memberships and startup/build plan:

```bash
aspire describe --launch-groups
aspire describe --launch-groups --format json
```

In the Dashboard, filter the Resources page by `catalog`, `basket`, `frontend`, or
`orders`. The filter is useful for reviewing the current plan, but it does not switch
the running group or start filtered resources.

To start `basketservice` after a catalog-only run, manually start its dependencies
(`basketcache` and `messaging`) as well. Start does not automatically start them:

```bash
aspire run --apphost playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj --launch-group catalog
# In another terminal:
aspire resource basketcache start --apphost playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
aspire resource messaging start --apphost playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
aspire resource basketservice start --apphost playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
```

Stop and clear `Aspire:LaunchGroup` (or choose the ordinary `https` launch profile)
for a full run.
