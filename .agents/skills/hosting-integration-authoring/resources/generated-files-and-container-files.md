# Generated files and container files

Many integrations generate config files, mount init files, or move files between resources and generated container images. Treat these files as part of the app model contract.

## Generated config files

DO:

- Generate files from callbacks at the lifecycle point where all required model data is available.
- Keep generated content deterministic.
- Include comments in code that explain non-obvious file formats and examples of generated shape.
- Redact or parameterize secrets.
- Use stable filenames and paths.

DON'T:

- Don't generate files in constructors.
- Don't write generated files into source directories unless that is the explicit user-facing feature.
- Don't include host-specific absolute paths in generated deployment artifacts.

## `WithContainerFiles`

Use container file APIs when a resource needs files from another resource or generated content in a container image/runtime.

DO:

- Use `WithContainerFiles` for generated files copied into containers.
- Add build pipeline dependencies when one resource's container files come from another resource.
- Keep destination paths explicit and aligned with the image's expected paths.
- Set file permissions when the target runtime requires executable or restricted files.

DON'T:

- Don't assume build order automatically follows file dependencies.
- Don't mount writable files as read-only unless the service supports it.
- Don't copy secrets into images when secret mounts or parameters are available.

## Init files

DO:

- Prefer `WithInitFiles` for database or service initialization files.
- Document accepted file types, execution order, and target paths.
- Support read-only mounts for init content when possible.

DON'T:

- Don't keep adding obsolete init bind-mount APIs for new integrations.
- Don't assume file ordering unless the service defines it or the integration enforces it.

## Temporary and persistent generated files

DO:

- Use Aspire store/temp abstractions when available for files created during AppHost execution.
- Otherwise use securely created temporary directories.
- Clean up temporary files when their lifetime ends.
- Keep generated persistent files under user-expected locations.

DON'T:

- Don't use ad hoc `Path.GetTempPath()` plus random names when a secure temp directory abstraction is available.
- Don't leak temporary files containing credentials.

## Cross-platform file behavior

DO:

- Normalize paths for the current platform.
- Avoid hardcoded `/` or `\` in paths.
- Consider line endings and executable permissions for generated scripts.
- Use UTF-8 for generated text unless the target tool requires another encoding.

DON'T:

- Don't assume case-sensitive filesystems.
- Don't assume Linux file modes work on Windows.
