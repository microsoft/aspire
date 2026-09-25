# Cross-platform tooling

Hosting integrations should work on Windows, macOS, Linux, and containers unless a platform limitation is explicit.

## Paths and working directories

DO:

- Resolve user-supplied app paths relative to `builder.AppHostDirectory`.
- Normalize paths for the current platform.
- Use `Path.Combine`, `Path.GetFullPath`, and existing path-normalization helpers.
- Keep working directories explicit on executable resources.

DON'T:

- Don't rely on the current process working directory.
- Don't call `Directory.SetCurrentDirectory`.
- Don't hardcode path separators.
- Don't assume filesystem case sensitivity.

## Required tools

DO:

- Use required-command/toolchain checks for language runtimes and external CLIs needed in run mode.
- Include installation/help URLs in required-command guidance.
- Make publish-only tool requirements fail during publish/build validation, not during local run.
- Distinguish Docker-required features from toolchain-only features.
- For deployment targets, check external CLI authentication/context separately from CLI installation, and include the selected project/subscription/cluster/region in diagnostics.
- Check container registry authentication and push/pull access before building large images when the target requires a registry.

DON'T:

- Don't fail app model construction because an optional run-mode tool is missing before the user uses that feature.
- Don't assume `docker`, `go`, `python`, `node`, package managers, `helm`, or cloud CLIs are available.
- Don't rely on ambient cloud CLI state when the AppHost model provides an explicit target context.

## Shell and arguments

DO:

- Prefer structured argument APIs over shell command strings.
- Preserve argument boundaries.
- Quote only at the shell boundary.
- Document command argument ordering when the underlying tool requires it.

DON'T:

- Don't concatenate user input into shell commands.
- Don't rely on Bash-specific syntax for Windows paths or PowerShell-specific syntax for Unix paths.
- Don't pass secrets as command-line args when environment variables or secret files work.

## Platform-specific behavior

DO:

- Use `OperatingSystem.IsWindows()` or similar guards for platform-specific environment variables, encodings, or commands.
- Handle Windows path length, file locking, and executable permission differences.
- Use UTF-8 mode or environment settings when a language runtime needs it on Windows.
- Test path-heavy features on at least one non-Unix platform when practical.

DON'T:

- Don't assume file deletion succeeds immediately on Windows after a process exits.
- Don't assume executable permission bits are meaningful on Windows.
- Don't assume localhost binding behavior is identical across runtimes and platforms.

## Containers and host networking

DO:

- Use Aspire endpoint abstractions instead of hardcoded hostnames.
- Use internal container endpoints for container-to-container traffic when the service requires different advertised addresses.
- Keep host-process and container-process connection strings distinct when needed.

DON'T:

- Don't assume `localhost` from inside a container points at the host.
- Don't publish host-only endpoint values into container environments.
