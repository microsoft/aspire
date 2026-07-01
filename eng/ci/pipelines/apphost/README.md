# CI Pipeline AppHost

This TypeScript AppHost is a side-by-side place to model CI jobs as Aspire pipeline steps. The GitHub Actions workflow installs an Aspire CLI build from the configured LKG channel and runs named steps from this AppHost with `aspire do`.

## Layout

```text
eng/ci/pipelines/apphost/
  apphost.mts
  lib/
    docker.mts
    dotnet.mts
    env.mts
    github.mts
    process.mts
    repo.mts
  workflows/
    cli-e2e-helpers.mts
    cli-e2e-image.mts
    daily-smoke.mts
    docs.mts
    fast.mts
    packaging.mts
```

`apphost.mts` is the composition root. It creates the builder, resolves the repository root, composes workflow modules, and runs the AppHost.

`lib/process.mts` contains shared process execution with Aspire pipeline task reporting. `lib/repo.mts` contains repository root resolution and repository-level commands such as restore. `lib/docker.mts` and `lib/dotnet.mts` contain shared CI helpers for Docker and dotnet command shapes. Docker retry and mirror behavior belongs in the shared Docker helper, but workflow modules should declare the ordered attempts they want to run. `lib/env.mts` centralizes environment variable parsing. `lib/github.mts` models the GitHub Actions runtime context. Keep this layer small and dependency-free; use Node.js built-ins instead of adding npm packages for CI plumbing.

`workflows/*.mts` files own related pipeline steps. Each file should export an `add<Area>Workflow(pipeline, repoRoot)` function that registers aggregate and leaf steps against the shared pipeline graph. Workflow-specific helpers, such as CLI E2E image artifact handling, should stay next to the workflows that use them.

## Current workflows

`workflows/cli-e2e-image.mts` defines:

- `ci-cli-e2e-image`: aggregate step used by CI.
- `cli-e2e-verify-docker`: verifies Docker is available.
- `cli-e2e-build-images`: builds the CLI E2E Docker images, retrying Ubuntu package installation with the default apt sources if the Azure mirror fails.
- `cli-e2e-save-image-tarballs`: saves Docker image tarballs under `artifacts/cli-e2e-image` for GitHub Actions to upload.

`workflows/daily-smoke.mts` defines:

- `ci-daily-smoke`: aggregate step used by CI.
- `daily-smoke-restore`: restores repository tools and SDKs.
- `daily-smoke-verify-docker`: verifies Docker is available.
- `daily-smoke-load-cli-e2e-image`: loads a prebuilt CLI E2E image artifact.
- `daily-smoke-build-tests`: builds the CLI E2E test project.
- `daily-smoke-tests`: runs the `SmokeTests` class against the configured Aspire CLI quality.

`workflows/docs.mts` defines:

- `ci-docs`: aggregate step used by CI.
- `markdownlint`: runs Markdownlint over repository Markdown files.

`workflows/fast.mts` defines:

- `ci-fast`: aggregate step used by CI.
- `typescript-sdk-tests`: runs the TypeScript SDK unit tests.
- `verify-skills-bundle`: verifies the embedded Aspire skills bundle archive and attestation.

`workflows/packaging.mts` defines:

- `ci-pack`: aggregate step used by CI.
- `pack`: runs the existing package build script.

The `ci-*` steps are stable entry points for GitHub Actions. Leaf steps remain available for local debugging.

For the first PR validating this approach, the AppHost shadow workflow is the only direct PR CI workflow left enabled. The existing direct PR workflows it covers are temporarily manual/scheduled-only so the PR checks show the AppHost pipeline shape clearly.

## Shadow parity

Each workflow module starts with a comment naming the GitHub Actions workflow it shadows. When changing one of those source workflows, update the corresponding AppHost workflow and this shadow workflow in the same PR.

| AppHost workflow | Source workflow |
| --- | --- |
| `workflows/cli-e2e-image.mts` | `.github/workflows/build-cli-e2e-image.yml` |
| `workflows/daily-smoke.mts` | `.github/workflows/tests-daily-smoke.yml` |
| `workflows/docs.mts` | `.github/workflows/markdownlint.yml` |
| `workflows/fast.mts` | `.github/workflows/typescript-sdk-tests.yml`, `.github/workflows/verify-aspire-skills-bundle.yml` |
| `workflows/packaging.mts` | `.github/workflows/build-packages.yml` |

## GitHub Actions compute boundaries

This AppHost is the single pipeline model, but GitHub Actions still owns the compute boundary. When steps need different runners, operating systems, permissions, or resource sizes, split them across GitHub Actions matrix entries or jobs and invoke different AppHost aggregate steps.

```yaml
matrix:
  include:
    - step: ci-fast
      runner: ubuntu-latest
    - step: ci-pack
      runner: 8-core-ubuntu-latest
    - step: ci-docs
      runner: ubuntu-latest
```

Each job still runs the same AppHost entry point:

```bash
aspire do ${{ matrix.step }} --apphost eng/ci/pipelines/apphost/apphost.mts --non-interactive
```

Before invoking `aspire do`, each clean runner runs `aspire restore --apphost apphost.mts --non-interactive` from `eng/ci/pipelines/apphost` to regenerate `.aspire/modules` before the pipeline step is imported.

This keeps one source of truth for the pipeline graph while preserving GitHub Actions scheduling, isolation, permissions, and OS-specific execution.

## Local usage

Install the Aspire CLI from the desired LKG channel:

```bash
./eng/scripts/get-aspire-cli.sh --quality dev
```

Restore generated TypeScript AppHost modules:

```bash
cd eng/ci/pipelines/apphost
aspire restore --apphost apphost.mts --non-interactive
cd -
```

List a pipeline graph:

```bash
aspire do ci-fast --apphost eng/ci/pipelines/apphost/apphost.mts --list-steps --non-interactive
```

Run a leaf step locally:

```bash
aspire do typescript-sdk-tests --apphost eng/ci/pipelines/apphost/apphost.mts --non-interactive
```

Some steps require local prerequisites. For example, `verify-skills-bundle` requires `pwsh`, `gh`, and GitHub authentication.

Docker-backed steps also require Docker to be running. `ci-daily-smoke` expects a prebuilt .NET CLI E2E image tarball in `cli-e2e-image/aspire-cli-e2e-dotnet.tar.gz` unless `CLI_E2E_IMAGE_DIR` points elsewhere. `ci-cli-e2e-image` can produce that tarball locally:

```bash
CLI_E2E_INCLUDE_POLYGLOT_IMAGES=false \
  aspire do ci-cli-e2e-image --apphost eng/ci/pipelines/apphost/apphost.mts --non-interactive
```

## Adding a workflow area

1. Add `workflows/<area>.mts`.
2. Export `add<Area>Workflow(pipeline, repoRoot)`.
3. Register one or more `ci-*` aggregate steps for CI-facing entry points.
4. Register explicit leaf steps for the actual commands.
5. Use the shared tool helpers in `lib/` for common Docker, dotnet, and process command shapes.
6. Add one composition line in `apphost.mts`.
7. Add the aggregate step to `.github/workflows/ci-apphost-shadow.yml` if it should run in the shadow workflow.

Avoid hiding CI behavior behind high-level step factories. Workflow modules should clearly show the commands they run; shared helpers should only cover common plumbing such as process execution, paths, environment handling, and reporting.
