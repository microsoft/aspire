---
applyTo: "**/*.cs"
---

# Temporary directory guidance

When reviewing or generating C# code in this repository:

* Prefer the repository temp directory abstractions first (for example `IFileSystemService.TempDirectory` / `ITempFileSystemService`) when they are available.
* Otherwise, use `Directory.CreateTempSubdirectory()` for temporary directories instead of `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())`.
* If code needs a temporary file path, create it under a securely created temporary directory rather than composing a path directly under `Path.GetTempPath()`.
* Preserve the caller's behavior when migrating existing code. If the final path must stay absent or needs broader permissions for bind mounts, use a secure temporary parent directory and derive the child path from it instead of pre-creating the final path.

When reviewing pull requests, flag new uses of `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())` for temporary directories and suggest the secure alternatives above.
