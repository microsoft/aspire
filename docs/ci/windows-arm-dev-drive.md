# Windows ARM native archive storage

The Windows ARM64 native CLI archive job uses the unused local NVMe disk as a
ReFS Dev Drive on `D:`.

The existing workspace path is a junction to `D:\aspire-build\workspace`.
`TEMP` and `TMP` use `D:\aspire-build\tmp`; `RUNNER_TEMP` stays unchanged.
After checkout, the job verifies these inherited build paths before compilation.

Setup fails if the disk or workspace is unsafe; existing filesystems are never
reformatted.
