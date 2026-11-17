---
applyTo: "tests/Infrastructure.Tests/**"
---

# Infrastructure Test Guidance

Use these rules when authoring or reviewing tests in `tests/Infrastructure.Tests`.
The goal is to catch incorrect infrastructure behavior, not to preserve its
current implementation.

## Choose the right test owner and entry point

- When a regression belongs to a repository-internal tool, prefer coverage in
  that tool's own test project over adding it to `Infrastructure.Tests`.
- Do not add infrastructure tests for internal MSBuild target or project
  implementation details, whether exercised through private targets or a public
  build entry point. Product and artifact correctness checks belong in the
  producing build or CI stage, as described below.
- An exceptional MSBuild regression test needs a critical failure that existing
  build or CI validation would not catch. Explain that gap and why a check in
  the producing build is unsuitable; do not add a test just because the target
  or project file changed.

## Test behavior, not source text

- Start with the regression the test should catch. Exercise the real tool,
  script, or build entry point with representative inputs, then assert its
  observable results: outputs, artifacts, side effects, exit codes, and failures.
  A callable production function or exported module is also a valid entry point;
  testing it does not require launching its entire host.
  Copying a complete, unmodified production file into a fixture is valid when
  it runs through its normal entry point.
- Assert behavior rather than searching YAML, Markdown, project files, or
  scripts for expected strings. Parsing metadata to independently discover
  consumers and compare them with production results is valid; checking a fixed
  command, property, step name, or comment is not evidence that it works.
  Snapshots, regular expressions, and parsed-value assertions do not change
  that distinction.
- Dependency and pinning policy checks belong to dedicated validation tooling
  or build validation, not assertions in infrastructure tests. This includes
  checks of GitHub Actions references.
- Do not extract script fragments from workflows or other files to execute in
  tests. Running a reconstructed fragment does not exercise the real entry point
  or its surrounding execution context. Sourcing a shell script, dot-sourcing a
  PowerShell script, or importing a module and calling its actual functions is
  valid unit testing. Load the production file through the language's normal
  mechanisms rather than copying or extracting function bodies into the test.
- Assertions on runtime results, such as a diagnostic emitted by a tool or the
  contents of a produced artifact, are behavioral assertions; they are not
  assertions on the source text that implements the behavior. Copying, printing,
  or serializing source/configuration does not make implementation-restating
  assertions behavioral.
- Checked-in configuration is valid production input. When testing selection,
  read `.github/instructions/test-trigger-map.instructions.md` for the routing
  contract, discovery-based checks, and representative regression coverage.
- Configuration validity belongs to the consuming tool: malformed rules and
  unresolved explicit references should produce actionable diagnostics. Test
  that production validation with invalid inputs rather than duplicating it in
  a test-owned model. Distinguish required references from optional convention
  matches that can legitimately be absent.
- Cover relevant negative and error cases when testing validation, fallback,
  retry, or failure behavior.
- If meaningful behavioral coverage is not practical, do not add a test merely
  to have coverage. State the limitation rather than substituting source-text
  checks or extracted-script tests. Existing tests using those patterns are not
  precedents to copy.
- When removing or replacing an existing test, identify its intended regression
  and whether another check exercises the same production code path. Where
  practical, move valuable inline logic into a production script or module and
  test that entry point. Record any behavior left unverified; testing a separate
  copy or test-owned reimplementation does not preserve coverage of the original.
  Do not require a production refactor solely to preserve a test count.
- Review mixed test classes method by method, not by filename or use of file I/O.
  Preserve real entry-point tests when removing neighboring source assertions.
  Report each removed test's regression, replacement or existing coverage, and
  remaining gap; distinguish related coverage from equivalent coverage.
- Do not use a test-owned implementation of the production algorithm as the
  system under test. Invoke production code; fixture setup, dependency stubs,
  and independent expected results belong in the test, not a substitute for the
  implementation whose behavior needs verification.

## Justify expensive build and publish tests

Do not add infrastructure tests that repeat product builds or publishes to
check artifact correctness. Put those checks in the producing build or CI stage.
For an exceptional critical regression described above, or a test of an internal
tool's behavior that genuinely requires a build, identify the failure and why
existing validation or a cheaper behavioral test cannot catch it. Use the
smallest project and operation that exercise the behavior.

## Put essential production checks in the build

Validation required to produce a correct product belongs in the build, packaging,
or CI stage that produces the artifact, not in `Infrastructure.Tests`. Run the
check as part of each applicable build or publish so correctness does not depend
on a separate test run. Validate the actual result at the appropriate point and
fail with an actionable diagnostic when it is incorrect.

For example, check a release artifact's required contents or behavior against the
produced artifact, not against the YAML, project properties, or script intended
to create it. Invoking MSBuild from a test does not make that test the right
place for production validation.

## Validate the product in the appropriate CI lane

Use GitHub CI for product and artifact checks that can run there. Prefer checking
the outputs of existing build steps over adding infrastructure tests that repeat
those builds.

The official Azure Pipelines build adds signing-specific behavior and does not
run the general repository test suite, but does run dedicated validations,
including basic template tests against produced packages. Reuse those lanes
where applicable; an `Infrastructure.Tests` assertion cannot protect a build
that does not run that suite. Checks of unsigned GitHub CI outputs cannot establish
signing-dependent correctness. Focused smoke checks of the actual post-signing
outputs can add value in the official pipeline, for example by verifying that a
produced executable starts or that a signed package can be consumed. Choose checks
that catch a concrete product failure, not checks that merely confirm pipeline
configuration.

## Reuse shared test infrastructure

- Search for existing helpers before adding new utilities. Reuse or extend
  `RepoRoot`, `TemporaryWorkspace`, `NodeCommand`, `PowerShellCommand`, and
  applicable shared test utilities.
- For test-selection scenarios, reuse the production selector and existing
  real-map helpers.
- Put repeated repository-root, process, workspace, parsing, and harness logic
  in a shared location within `Infrastructure.Tests` instead of adding another
  file-local copy. Sharing within this project is sufficient; do not create a
  repository-wide abstraction without an actual need from other projects. Reuse
  execution harnesses only when they exercise the real entry point, not extracted
  script fragments.
- Keep timeouts, output capture, cleanup, and process-tree termination in shared
  process helpers that infrastructure tests can reuse rather than adding
  different ways to launch processes in individual tests.
