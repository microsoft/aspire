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
4. Run `build` (and `start` for `PublishAsPackageScript`) using the package manager's native workspace filter syntax:
   - `npm run <script> --workspace=<app-package-name>`
   - `yarn workspace <app-package-name> run <script>`
   - `pnpm --filter <app-package-name> run <script>`
   - `bun run --filter <app-package-name> <script>`
5. Set the runtime stage `WORKDIR` to `/app/<app-relative-path>` so existing entrypoint paths keep working.

> **pnpm + `PublishAsPackageScript`**: pnpm's symlinked `node_modules` layout cannot be flattened via the standard production-deps overlay used for npm/yarn/bun. For pnpm workspace apps that use `PublishAsPackageScript`, the generated Dockerfile uses [`pnpm deploy`](https://pnpm.io/cli/deploy) to materialize the target package and its production dependencies (workspace deps copied as content) into a self-contained directory. The runtime stage runs `pnpm run <start-script>` against this deployed bundle. If the workspace root declares `packageManager: "pnpm@10..."`, Aspire uses pnpm 10's non-legacy deploy path and validates that `pnpm-workspace.yaml` contains `injectWorkspacePackages: true`. For pnpm 8/9 or missing/unparseable pnpm versions, Aspire keeps the legacy deploy compatibility path instead of silently overriding the project's declared package-manager major version.

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
