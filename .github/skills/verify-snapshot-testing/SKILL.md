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

1. Read the `.received.*` file to see the actual output.
2. Read the `.verified.*` file (if it exists) to see the expected output.
3. Compare the two to understand the difference.

### Step 3: Determine the action

- **If the change is expected** (due to an intentional code change): copy the `.received.*` file over the `.verified.*` file to accept the new snapshot.
- **If it is a new test** (no `.verified.*` file): accept the `.received.*` file as the new snapshot by renaming it to `.verified.*`.
- **If the change is a bug**: fix the code, not the snapshot. Re-run the test to confirm the fix.

## Rules

- **Never hand-edit `.verified.*` files** to make tests pass. Always let Verify generate the correct output by running the test.
- Snapshot files live next to the test source file. For a test in `Tests/MyTests.cs`, look for `Tests/MyTests.MethodName.verified.txt`.

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
