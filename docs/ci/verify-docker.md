# Verify Docker action

Workflows that need Docker should use `.github/actions/verify-docker` instead of running `docker info` directly. The action checks that the Docker daemon is reachable with `docker info`; pass `diagnostics: true` when a workflow benefits from extra `docker version`, `docker buildx version`, and root disk-space output.

Extend this action when CI needs a stronger Docker runner contract, such as adding buildx or disk-space sanity checks before Docker-heavy jobs run.
