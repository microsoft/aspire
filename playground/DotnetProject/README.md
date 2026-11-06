# .NET project watch playground

This playground has C# and TypeScript AppHosts, two project services consuming
`DotnetProject.SharedLibrary`, and a file-based worker. It exercises ordinary V2 builds and the
explicit **experimental** `aspire run --watch` path.

Use a freshly built CLI and matching packages, not a released CLI without the preview option.
Check the SDK selected from the AppHost directory with `dotnet --version`. The default watcher
is 10.0.403 and cannot load .NET 11 MSBuild runtime dependencies. This source checkout pins a
.NET 11 SDK, so use a verified compatible exact `ASPIRE_DOTNET_WATCH_TOOL_VERSION` or a complete
`ASPIRE_DOTNET_WATCH_TOOL_PATH` installation when exercising it. Do not change `global.json`
to hide an incompatibility; ordinary coverage with a warning is the expected fallback.

For the mixed project/file watch exercise, add this directive locally to `worker/worker.cs`:

```csharp
#:property PublishAot=false
```

File apps otherwise default to AOT settings that disable the managed agent. The worker shares
`Playground.ServiceDefaults` with the project services, so leaving it incompatible can keep the
whole shared-output component ordinary. The default sample deliberately does not change AOT
configuration for non-watch runs.

Start with TypeScript:

```bash
cd playground/DotnetProject/TypeScriptAppHost
aspire run --watch
```

The existing managed AppHost server is required for `Aspire.Hosting.Dotnet`; do not enable
`features.appHostServerV2` for this managed-integration scenario. Inspect each resource's
`watch.mode` and `watch.reason` before treating an edit as a watch test.

Edit a method body in `DotnetProject.ApiService/Program.cs` and call its HTTP endpoint. Then edit
the body of `Greeter.Greet` in `DotnetProject.SharedLibrary/Greeter.cs` and call the API and worker
endpoints: both project consumers should observe the change. Method-body edits avoid conflating
hot reload with an intentional rude-edit restart. Check compiler diagnostics by introducing an
invalid expression, then correcting it. The previous application can remain running while an
edit cannot be applied.

Stop that run, then repeat with C# from the repository root:

```bash
cd playground/DotnetProject/DotnetProject.AppHost
aspire run --watch
```

Change AppHost model construction, such as a resource environment value or a resource addition
or removal. A new C# AppHost generation must reconstruct the model; merely hot-reloading the
method body is not enough. Verify that old servers, proxies, and services terminate.

Manual Rebuild is disabled only on watched resources. Restart is a process restart rather than
a build. After manually stopping a watched resource, restart the entire watch run to start it
again safely. Successful exits and real application PIDs are not supplied by the selected
resource protocol; `watch.transport.*` metadata is explicitly about the proxy, not the application.
See the [integration README](../../src/Aspire.Hosting.Dotnet/README.md#experimental-watch)
for the complete preview boundaries.
