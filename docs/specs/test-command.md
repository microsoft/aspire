# Aspire test

> **Status:** Draft

## Why

Integration tests should exercise an application as it runs, regardless of the
language used for its AppHost or tests. A CLI-driven polyglot end-to-end testing
model also works better for agents: they can update tests connected through the
application model and invoke one entry point, `aspire test`, to orchestrate
dependencies and aggregate results across frameworks. Agents do not need a
separate orchestration workflow for each test framework.

`DistributedApplicationTestingBuilder` primarily serves C# developers. Its deep
inspection of AppHost internals relies on the tests and AppHost sharing a process;
that approach does not work with polyglot AppHosts. As .NET AppHosts move closer to
the polyglot execution model, an out-of-process testing approach provides a common
foundation without requiring every language to reproduce those internals.

`aspire test` evolves Aspire's end-to-end integration testing approach by
inverting framework-owned orchestration: Aspire starts the application, real
Azure resources or local emulators, and test drivers described in the application
model. References and dependencies give those drivers the application's
endpoints, connection information, and readiness relationships. The drivers test
the running system; Aspire owns their combined lifetime and verdict.

One run can combine frontend Vitest tests, backend Go tests, and a finite
validation script with no test framework. Helpers such as `AddMsTest` and
`AddPytest` illustrate how framework integrations can make that model convenient;
they belong to separate integration layers, not this foundation. Vitest is an example of a possible
driver, not a framework helper implemented here.

This approach complements existing in-process testing rather than recreating
its internal inspection capabilities. Dashboard-specific testing features are
outside this spec.

## Primitives

`TestingAnnotation` marks any resource as a participant. `WithTesting()` adds the
annotation without requiring a framework or individual-result collection.
Execution passes when every replica finishes with exit code zero. Reported
`FailedToStart` or `RuntimeUnhealthy` states fail without requiring an exit code;
otherwise, a terminal `Finished` or `Exited` state needs exit code zero to pass.
These are resource notifications, not failures inferred from elapsed startup
time. A terminal resource with an unknown exit code gets five seconds to publish
it, then fails explicitly. Custom resources publish their state and exit code
through `ResourceNotificationService`.

There are no test-specific per-resource startup or completion deadlines.
A resource waiting on dependencies is not considered failed merely because it
remains `Waiting`; neither is one that remains `Starting` or `Running`. If a
dependency never becomes ready, testing can continue waiting indefinitely unless
the user cancels or supplies the overall `--timeout-seconds` deadline. The
separate CLI AppHost startup timeout bounds connection and readiness negotiation,
not each test resource's dependency wait or execution.

`WithTesting(Func<ResourceTestingContext, Task>)` additionally supplies an
asynchronous runtime collector. The collector can observe and publish results
while execution is underway, or await `WaitForCompletionAsync()` before reading
final reports. That wait reports resource execution success, not the collector's
verdict. Collection is optional: a finite program with only an exit code remains
a complete test participant.

`TestingAnnotation.Timeout` optionally bounds the collector callback. It defaults
to `Timeout.InfiniteTimeSpan`; a finite deadline begins when the collector is
scheduled and includes any resource-completion wait inside it. Expiry cancels the
collector context, rejects late publication, and fails collection while other
resources and collectors continue. It does not stop the test process or impose
per-resource startup or execution deadlines. Overall `--timeout-seconds` and user
cancellation still apply, even with an infinite collector timeout.
`WithTestingTimeout(TimeSpan)` configures it fluently after `WithTesting` or a
framework testing helper, without replacing the collector. It requires an existing
testing annotation so it cannot accidentally turn a supporting service into a test.

`ResourceTestingContext` supplies the logical resource name, resource artifact
path, cancellation token, and containing `TestingContext`. `TestingContext`
identifies the invocation's output path. `PublishResult(TestResult)` associates an
observed name, outcome, optional duration, and optional diagnostic with that
resource. Only `Passed` and `Skipped` are non-failing individual outcomes;
`Failed`, `Inconclusive`, `Error`, `TimedOut`, and `Cancelled` fail the aggregate.
Process exits never manufacture individual test results.

Output destinations are established during model construction in testing mode,
not by invoking collectors. Discovery waits until user before-start events,
lifecycle hooks, and before-start pipeline steps finish changing the model.
The coordinator then schedules collectors before resource execution begins:
collection accompanies execution, rather than being restricted to a post-exit
callback. This is eager run-level coordination, not a callback triggered by each
resource's start event. A collector is not a prelaunch configuration hook.
Framework integrations configure runner report locations independently and stage
artifacts into the supplied destination.

Repeated `WithTesting` calls replace the collector while preserving its timeout,
context and destination; a marker-only call removes collection. Outside testing, the
annotation does not invoke collectors or allocate testing output paths.

### Low-level examples

The following C# fragments assume an AppHost `builder`, the hosting namespaces,
and suppression of the experimental `ASPIRETESTING001` diagnostic. The public
annotation can be attached directly:

```csharp
var tests = builder.AddExecutable("checks", "check-service", ".");
tests.WithAnnotation(new TestingAnnotation { Timeout = TimeSpan.FromMinutes(2) });
```

This marks execution; the timeout only applies if a collector is attached.
`TestingAnnotation` has no public collector setter. Use `WithTesting` to attach
one, preserving the timeout above. A simplified callback can wait for its driver
to finish and explicitly publish that driver's status:

```csharp
tests.WithTesting(async context =>
{
    var passed = await context.WaitForCompletionAsync();
    context.PublishResult(new TestResult
    {
        Name = "driver execution",
        Outcome = passed ? TestOutcome.Passed : TestOutcome.Failed
    });
}).WithTestingTimeout(TimeSpan.FromMinutes(2));
```

Here the result intentionally describes the driver, not an inferred framework
assertion. Real framework collectors translate observed report entries; the wait's
boolean still describes execution, independently of those results.

Alternatively, a collector can publish completed cases incrementally.
`ReadCompletedResultsAsync` below is a hypothetical application-provided report
reader yielding `TestResult` values, not an Aspire API:

```csharp
tests.WithTesting(async context =>
{
    await foreach (var result in ReadCompletedResultsAsync(context.CancellationToken))
    {
        context.PublishResult(result);
    }
});
```

Each publication is an observed completed result, not a running-state update or
replacement of an earlier result. Actual streaming depends on the driver and
report reader.

### Framework-level examples

The intended higher-level API surface combines implemented and proposed helpers.
Given an existing `api` resource with an HTTP endpoint, one AppHost can declare
multiple drivers:

```csharp
builder.AddMsTest("dotnet-tests", "../Api.Tests/Api.Tests.csproj")
    .WithReference(api).WaitFor(api);

builder.AddPytest("python-tests", "../python-tests")
    .WithArgs("tests", "-q").WithReference(api).WaitFor(api)
    .WithTestingTimeout(TimeSpan.FromMinutes(2));

builder.AddGoTest("go-tests", "../go-tests")
    .WithEnvironment("API_URL", api.GetEndpoint("http")).WaitFor(api);

builder.AddCargoTest("rust-tests", "../rust-tests")
    .WithEnvironment("API_URL", api.GetEndpoint("http")).WaitFor(api);

builder.AddJavaApp("java-tests", "../java-tests")
    .WithMavenGoal("test", "--batch-mode", "--no-transfer-progress")
    .WithJUnitTesting()
    .WithEnvironment("SERVICE_URL", api.GetEndpoint("http")).WaitFor(api);
```

These helpers configure their runners and collection, not a separate orchestration
workflow. Projects still supply their framework dependencies: for example,
executable MSTest/MTP with TRX support, pytest in the Python environment, and
JUnit/Surefire in the Maven project.
The framework drivers contribute to one run and aggregate verdict.

## Services

The new Aspire.Hosting service is `TestRunCoordinator`. It discovers annotated
resources, observes execution and replicas, invokes collectors concurrently,
records published results, and produces aggregate completion. It waits for all
execution and collection work even when another participant fails; an
early-returning collector cannot end its resource.

The coordinator reuses the existing `ResourceNotificationService` for
authoritative state tracking and the existing AppHost backchannel for CLI
communication. It does not stop the application itself. The CLI's `TestCommand`
owns orchestration and shutdown; `TestRunReporter` maintains the report and
renders text or JSON.

## Protocol

Testing extends the existing CLI-to-AppHost backchannel with completion waiting
and a test-run subscription. The stream carries resource observations,
individual results, collection status, and a final aggregate verdict.

The coordinator records events before the CLI connects. Subscription replays
those events and joins live delivery under one synchronization boundary, so
rapid completion or early publication is not lost. Resource completion waits
also observe already-completed resources. Aggregate completion follows both
execution and collection, with producers drained before the stream closes.

Run cancellation, AppHost shutdown, and backchannel shutdown bound collection.
Testing callbacks use the run's cancellation lifetime rather than the ordinary
remote callback deadline, via the callback-timeout attribute. Other callbacks
keep their normal timeout policy. Cancellation stops waiting and closes result
publication; it does not promise to forcibly terminate arbitrary uncooperative
callback code. Lost connections and incomplete streams produce diagnostics, not
successful empty reports.

## CLI

`aspire test` shares AppHost build, launch, and shutdown infrastructure with
`aspire run`, but has its own finite orchestration and output. Normal
`aspire run` keeps its existing lifetime and behavior. Testing disables watch and
does not support detached execution, profile capture, or extension-managed debug
sessions. Unannotated services remain available until annotated resources and
collectors finish.

The aggregate passes only when resource execution and collection pass. Collector
exceptions and unsuccessful published results cannot be overridden by a zero
process exit code. No annotated resources is an explicit error. Normal completion
returns `0` for success or `23` for test failure; user cancellation returns `130`.
Startup, transport, artifact, and shutdown errors also fail with diagnostics.

Text output shows progress, resource outcomes, published results, and a final
verdict. `--format json` writes one summary object to stdout, with diagnostics on
stderr. The persisted `summary.json` tracks observed results during execution and
is finalized after shutdown, retaining partial evidence on failure or cancellation.

The report uses schema version `1`. `testResults` is an object keyed by logical
resource name, containing execution `exitCode`/`outcome`, `collectorStatus`, and
nested `testCases`. Cases carry their name, outcome, optional duration and message,
without repeating the resource name. `testSummary` contains the overall outcome
and passed/failed/skipped counts of actual published cases, not process exits.
Collector failure retains previously published cases and independently fails the
run. A resource-only driver has an empty `testCases` array and
`collectorStatus: "notConfigured"`; a missing collector observation is `null`,
not an assumed successful collection.

Multi-replica resources retain per-instance state, exit code and outcome in a
`replicas` object keyed by instance ID. The logical resource passes execution
only when all expected replicas pass. Different or unavailable replica exit
codes leave the logical `exitCode` null rather than selecting an arbitrary code.
CLI exit status and diagnostics remain separate metadata, including interruption
during keep-alive inspection. This JSON projection does not change backchannel
events.

Each invocation gets a unique private directory under the configured Aspire
home, normally `~/.aspire/testruns/<run-id>/`. Its layout is flat by logical
resource; replicas share that resource's directory:

```text
<run-id>/
  summary.json
  frontend-tests/
  backend-tests/
```

These are host filesystem paths. Framework report files are not automatically
redirected there, and container drivers must explicitly configure mounts and
access. Directory and CLI-owned file permissions restrict access to the current
user; they do not set permissions for framework-written files.

`--timeout-seconds` optionally bounds the whole command, including discovery,
build, execution, collection, and inspection. There is no default deadline.
Expiry returns `17` after bounded teardown. `--keep-alive` retains the foreground
AppHost and dependencies after completion for inspection, without rerunning
tests. In JSON mode it emits the completed summary once; the file subsequently
records the final CLI exit status when inspection ends.

Command registration is preview-only and gated by `testCommandEnabled`, default
`false`. The public testing primitives are experimental (`ASPIRETESTING001`).

### Invocation and output examples

Enable the preview for just one invocation (Bash):

```bash
features__testCommandEnabled=true aspire test --apphost ./AppHost/AppHost.csproj
features__testCommandEnabled=true aspire test --apphost ./apphost.ts --timeout-seconds 300 --format json
features__testCommandEnabled=true aspire test --apphost ./apphost.ts --keep-alive
```

For example, a Go case passing and a pytest case failing still produce one result.
Representative interactive text, with launch logs, colors, icons, and some
resource transitions omitted:

```text
Tests are running
Test go-tests / TestHealth: passed
Resource go-tests: passed; 1 passed, 0 failed, 0 other
Test python-tests / test_checkout: failed
Resource python-tests: failed; 0 passed, 1 failed, 0 other
Test run failed
Published test results: 1 passed, 1 failed, 0 skipped
```

The equivalent illustrative JSON excerpt uses the actual summary field names.
Timestamps, artifact paths, replica/state details, and nullable diagnostics are
omitted here; the real object includes them:

```json
{
  "schemaVersion": 1,
  "cliExitCode": 23,
  "testResults": {
    "go-tests": {
      "exitCode": 0,
      "outcome": "passed",
      "collectorStatus": "completed",
      "testCases": [
        { "name": "TestHealth", "outcome": "passed" }
      ]
    },
    "python-tests": {
      "exitCode": 1,
      "outcome": "failed",
      "collectorStatus": "completed",
      "testCases": [
        { "name": "test_checkout", "outcome": "failed" }
      ]
    }
  },
  "testSummary": {
    "outcome": "failed",
    "passed": 1,
    "failed": 1,
    "skipped": 0
  }
}
```

Collection completed successfully in this example: it read a failing pytest case
rather than failing to read its report. A passing run instead reports
`testSummary.outcome: "passed"` and `"cliExitCode": 0`; resource and individual-result verdicts
remain separate in both cases.
