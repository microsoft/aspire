# Verify Docker action

Workflows that need Docker should use `.github/actions/verify-docker` instead of running `docker info` directly. The action checks that the Docker daemon is reachable with `docker info` and always emits `docker version` and `docker buildx version` so logs are useful when the daemon is broken.

The action is a no-op on non-Linux runners, so callers can use it unconditionally even from cross-OS jobs — there's no need to gate the step with `if: runner.os == 'Linux'`.

Extend this action when CI needs a stronger Docker runner contract.
