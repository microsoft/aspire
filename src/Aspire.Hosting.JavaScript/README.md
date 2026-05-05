# Aspire.Hosting.JavaScript library

Provides extension methods and resource definitions for an Aspire AppHost to configure JavaScript projects.

## Getting started

### Install the package

In your AppHost project, install the Aspire JavaScript library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.JavaScript
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Or resource and consume the connection using the following methods:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaScriptApp("frontend", "../frontend", "app.js");

builder.Build().Run();
```

## Monorepo / workspace support

When the JavaScript app lives inside an npm / yarn / pnpm / bun workspace (a monorepo), the auto-generated Dockerfile must use the workspace **root** as its build context so the root `package.json`, lockfile, and sibling workspace member manifests are visible to `docker build`. Use `WithWorkspaceRoot` to opt in:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddViteApp("web", "../monorepo/packages/web")
       .WithWorkspaceRoot("../monorepo")
       .WithPnpm();

builder.Build().Run();
```

`WithWorkspaceRoot` accepts a path relative to the AppHost directory (or an absolute path) pointing at the workspace root. The root must contain:

- A `package.json` with a `workspaces` field (npm / yarn / bun), **or** a `pnpm-workspace.yaml` listing `packages`.
- A recognized lockfile: `package-lock.json`, `npm-shrinkwrap.json`, `yarn.lock`, `pnpm-lock.yaml`, `bun.lock`, or `bun.lockb`.

The application's directory must be a declared workspace member, and its `package.json` must have a non-empty `name` field (the name is used by the package manager's workspace filter to build and run only this app).

The generated Dockerfile will:

1. Use the workspace root as the build context.
2. Copy the workspace root's `package.json`, lockfile, optional manifest files (`pnpm-workspace.yaml`, `.yarnrc.yml`, `.npmrc`, `bunfig.toml`), the `.yarn/` directory if present, and every workspace member's `package.json` (cache-friendly).
3. Run `install` at the workspace root so transitive deps and `workspace:*` dependencies resolve correctly.
4. Run `build` (and `start` for `PublishAsNpmScript`) using the package manager's native workspace filter syntax:
   - `npm run <script> --workspace=<app-package-name>`
   - `yarn workspace <app-package-name> run <script>`
   - `pnpm --filter <app-package-name> run <script>`
   - `bun --filter <app-package-name> run <script>`
5. Set the runtime stage `WORKDIR` to `/app/<app-relative-path>` so existing entrypoint paths keep working.

> **Note**: `PublishAsNpmScript` is **not** supported with pnpm in workspace mode. pnpm uses symlinked `node_modules` rather than hoisting, so the production-deps stage cannot be flattened into a runnable image. Use `PublishAsNodeServer` with a bundler (tsup, esbuild) instead.

### Required `.dockerignore`

The build context becomes the entire workspace root, so a `.dockerignore` at the workspace root is strongly recommended to keep the build context small and avoid leaking sensitive files. A reasonable starting point:

```text
**/node_modules
**/.git
**/.next
**/dist
**/build
**/coverage
**/.turbo
**/.cache
**/.env*
!**/.env.example
**/*.log
```

Aspire does not generate or modify `.dockerignore`; you must commit one yourself.

## Additional documentation
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node

## Feedback & contributing

https://github.com/microsoft/aspire
