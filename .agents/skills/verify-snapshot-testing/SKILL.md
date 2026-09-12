---
name: verify-snapshot-testing
description: Handle Verify snapshot test failures. Use this when a Verify snapshot test fails, when asked to accept or update snapshots, or when asked to add a new Verify test.
---

You are a specialized agent for handling [Verify](https://github.com/VerifyTests/Verify) snapshot test failures in this repository.

## Key concepts

- **`.verified.*` files** are the approved snapshots. They are committed to source control.
- **`.received.*` files** are the actual output from the latest test run. They are generated when a test fails (actual != expected) and are git-ignored.

## Handling a test failure

When a Verify snapshot test fails, follow this process:

### Step 1: Read the exception message

The exception message is machine-parsable:

```
Directory: /path/to/test/project
New:
  - Received: TestClass.Method.received.txt
    Verified: TestClass.Method.verified.txt
NotEqual:
  - Received: TestClass.Method.received.txt
    Verified: TestClass.Method.verified.txt

FileContent:

Received: TestClass.Method.received.txt
<received content>
Verified: TestClass.Method.verified.txt
<verified content>
```

- `Directory:` gives the base path for all file references.
- `New` means no `.verified.` file exists yet (first run or new test).
- `NotEqual` means the `.received.` and `.verified.` files differ.
- `Delete` means a `.verified.` file is no longer produced by any test.
- `FileContent:` contains the actual content for comparison.

### Step 2: Read the files

This repo runs with `VerifierSettings.AutoVerify(includeBuildServer: false, throwException: true)` (see `tests/Shared/TestModuleInitializer.cs`), so a local run behaves differently from CI:

- **Locally**: the `.verified.*` file has *already been overwritten* with the new output, and the test still fails. There is usually no `.received.*` file to read. Inspect the change with `git diff` on the `.verified.*` file — the "before" side is the old approved snapshot, the "after" side is the actual output.
- **On the build server**: auto-accept is off, so a `.received.*` file is written alongside the `.verified.*` file. Read both and compare.

The `FileContent:` section of the exception message also contains both sides, and is available either way.

### Step 3: Determine the action

- **If the change is expected** (due to an intentional code change): keep the rewritten `.verified.*` file and commit it. If any `.received.*` files are still pending, run `dotnet verify accept -y` to accept them.
- **If it is a new test** (no `.verified.*` file existed): the newly created `.verified.*` file is the snapshot — review it line by line before committing, since nothing was there to diff against.
- **If the change is a bug**: fix the code, not the snapshot, and `git checkout` the `.verified.*` file to restore the approved content. Re-run the test to confirm it passes against the original snapshot.

Never commit a `.verified.*` change you have not read. Auto-accept means a wrong snapshot lands silently in the working tree, so `git diff` before every commit that touches one.

## Rules

- **Never hand-edit `.verified.*` files** to make tests pass. Always let Verify generate the correct output by running the test.
- Do not infer the snapshot location from the test source file path. This repo customizes `DerivePathInfo`, so snapshots live in a `Snapshots` directory at the root of the test project (e.g. `tests/Aspire.Hosting.Tests/Snapshots/MyTests.MethodName.verified.txt`), not beside the `.cs` file. Always use the `Directory:` value from the exception message as the base path for the reported file names.

## Scrubbed values

Verify replaces non-deterministic values with stable placeholders. These are intentional:

- GUIDs become `Guid_1`, `Guid_2`, etc.
- DateTimes become `DateTime_1`, `DateTime_2`, etc.
- File paths become `{SolutionDirectory}`, `{ProjectDirectory}`, `{TempPath}`.

Do not treat these placeholders as errors.

## Verified file conventions

- Encoding: UTF-8 with BOM
- Line endings: LF (not CRLF)
- No trailing newline

These are enforced for text based snapshots by the root `.editorconfig` and `.gitattributes`. When a snapshot with a new text extension is added, add that extension to both files. Do not reformat `.verified.*` files — leave them exactly as Verify wrote them.
