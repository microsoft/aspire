# CLI E2E Docker images

The CLI E2E tests run in Docker containers through Hex1b. GitHub Actions splits those tests across many isolated jobs, so the CI workflows prebuild shared images once per workflow run and pass them to test jobs as short-lived artifacts.

## Image contract

The reusable `.github/workflows/build-cli-e2e-image.yml` workflow produces these artifacts:

| Variant | Dockerfile | Artifact | Image tag | Image env var | Require env var |
| --- | --- | --- | --- | --- | --- |
| DotNet | `tests/Shared/Docker/Dockerfile.e2e` | `cli-e2e-dotnet-image` | `aspire-cli-e2e-dotnet:prebuilt` | `ASPIRE_E2E_DOTNET_IMAGE` | `ASPIRE_E2E_REQUIRE_DOTNET_IMAGE` |
| Polyglot | `tests/Shared/Docker/Dockerfile.e2e-polyglot-base` | `cli-e2e-polyglot-image` | `aspire-cli-e2e-polyglot:prebuilt` | `ASPIRE_E2E_POLYGLOT_IMAGE` | `ASPIRE_E2E_REQUIRE_POLYGLOT_IMAGE` |
| Polyglot Java | `tests/Shared/Docker/Dockerfile.e2e-polyglot-java` | `cli-e2e-polyglot-java-image` | `aspire-cli-e2e-polyglot-java:prebuilt` | `ASPIRE_E2E_POLYGLOT_JAVA_IMAGE` | `ASPIRE_E2E_REQUIRE_POLYGLOT_JAVA_IMAGE` |

`Dockerfile.e2e-podman` is not part of this contract because it uses a separate privileged Podman-in-Docker runtime path.

## Fail-fast semantics

`CliE2ETestHelpers` uses the image environment variable for the selected Dockerfile variant when it is set. If the matching `ASPIRE_E2E_REQUIRE_*_IMAGE` variable is `true` or `1`, the helper throws when the image variable is missing. If the require variable is not set, the helper falls back to building the variant Dockerfile locally.

This preserves the local development path while making opted-in CI jobs fail early when their prebuilt image artifact was not loaded correctly.

## Workflow behavior

The image build workflow has an `includePolyglotImages` input. It defaults to `true` and builds all three shared images. Daily CLI smoke tests set it to `false` because they only need the DotNet image.

Consumer workflows download image artifacts into `${{ github.workspace }}/cli-e2e-image` and call `eng/scripts/load-cli-e2e-images.sh` to load the images and export the matching environment variables. Regular split CLI E2E jobs always require DotNet and Polyglot images. Java image download and loading is conditional on Java test jobs to avoid transferring the larger Java tarball to every split job.

The reusable image build workflow also has an `uploadArtifacts` input. It defaults to `true` for test workflows because each isolated test job needs the image tarballs. Cache-warming workflows set it to `false` so they only build the images and export BuildKit cache layers.

## Cross-PR BuildKit cache

The image build workflow exports BuildKit layers to the GitHub Actions cache with stable scopes:

| Variant | Cache scope |
| --- | --- |
| DotNet | `cli-e2e-dotnet` |
| Polyglot | `cli-e2e-polyglot-base` |

GitHub Actions does not let one pull request restore cache entries written by a sibling pull request. Pull request runs can restore cache entries from the current PR ref and from the base/default branch. The `.github/workflows/warm-cli-e2e-image-cache.yml` workflow runs on `main` by schedule, manual dispatch, and relevant `main` pushes so new PRs can restore layers seeded from `main`.

The Java image is built from the local polyglot base image and does not use a separate shared BuildKit cache scope.

## Adding another variant

When adding a new shared CLI E2E Dockerfile variant:

1. Add a stable artifact name, image tag, image environment variable, and require environment variable.
2. Update `.github/workflows/build-cli-e2e-image.yml` to build, save, and upload the new image.
3. Update `eng/scripts/load-cli-e2e-images.sh` to load the tarball and export the environment variables.
4. Update workflow consumers to download the artifact where needed.
5. Update `CliE2ETestHelpers.GetDockerfilePath`, `CliE2ETestHelpers.GetPrebuiltImageName`, and the helper tests.

## Local usage

To run a CLI E2E test against a prebuilt image locally, load the tarball and set the matching image variable:

```bash
docker load -i /path/to/aspire-cli-e2e-dotnet.tar.gz
ASPIRE_E2E_DOTNET_IMAGE=aspire-cli-e2e-dotnet:prebuilt \
  dotnet test tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj \
  -- --filter-class "*.SmokeTests"
```

Set the matching `ASPIRE_E2E_REQUIRE_*_IMAGE=true` variable when you want local execution to fail instead of falling back to a Dockerfile build.
