# Child process management

Aspire's CLI and AppHost server use a source-shared process implementation for launch, output forwarding, exit observation, cancellation, and disposal. Lifetime policy determines whether an execution owns only an ordinary child, contains a runtime and its workers, or preserves the special AppHost/DCP cleanup relationship.

This document describes process management, not integration discovery, RPC recovery, or the lifetime of resources created through the hosting model. Integration-specific behavior is documented in [Polyglot integrations](polyglot-integrations.md).

## Lifetime policies

| Policy | Intended use | Behavior |
| --- | --- | --- |
| `CallerManaged` | Ordinary commands whose ownership is managed by their caller | Launches the command directly. Cancellation and disposal use the shared shutdown implementation, but natural root exit does not establish that all descendants have exited. |
| `OwnedTree` | Helpers, primary guest-language runtimes, integration hosts, and integration package installation | Launches a guardian that contains the runtime and its workers. Owner death, natural runtime exit, cancellation, and disposal retire the contained scope. Successful full completion requires containment cleanup. |
| `AppHost` | .NET AppHosts and AppHost servers | Preserves existing console, Windows breakaway-job, and cooperative owner-watchdog behavior so DCP can finish resource cleanup. Does not add an owned-tree guardian. |

The CLI's existing `KillOnParentExit` option selects `OwnedTree` unless the caller explicitly selects `AppHost`. It is not just a Windows option for ordinary owned children.

Detachment is a separate launch option. Detached CLI children intentionally survive their launcher, have null standard handles, and are not guardian-owned. `Detached` and `OwnedTree` cannot be combined. Callers must retain or relinquish the execution according to their intended lifetime rather than disposing it while expecting the child to remain running.

Do not make every subprocess `OwnedTree`. Build servers can intentionally outlive a command, AppHosts need DCP breakaway, and detached launches explicitly have different ownership semantics.

## Components and ownership

The types under [`src/Shared`](../../src/Shared) are compiled into both the CLI and RemoteHost. They share source and behavior, not a single object or process.

| Component | Responsibility |
| --- | --- |
| `IChildProcess` / `ChildProcess` | Lazy OS launch, process identity, output pumps, root/full exit waits, shutdown, containment verification, and handle disposal. |
| `ChildProcessOptions` / `ChildProcessLifetime` | Lifetime policy, output callbacks, graceful-shutdown callbacks, deadlines, clock, and guardian launch construction. |
| `IChildProcessFactory` / `ChildProcessFactory` | Injectable creation boundary used by RemoteHost. |
| `IProcessExecutionFactory` / `ProcessExecutionFactory` | CLI creation boundary; configures command arguments, environment filtering, redaction, console behavior, and launch options. |
| `ProcessInvocationOptions` / `ProcessExecution` | CLI policy adaptation. `ProcessExecution` **inherits** `ChildProcess` and supplies the command-wide graceful window and process-tree signaler; it does not contain a second execution or implement another shutdown ladder. |
| `ProcessScope` | Holds an `IChildProcess` and its root-exit task for a supervising owner. Delegates full completion and disposal to the execution without adding termination policy. |
| `ProcessSupervisor` | Runs in a separate guardian process, establishes OS containment, launches the runtime, watches the owner, forwards graceful signals, and hands off runtime completion status. |
| `ParentProcessLivenessMonitor` / `ProcessStartTimeHelper` | Detects owner death using PID plus stable start identity rather than PID alone. |
| `ProcessSupervisorLogger` | Writes synchronously flushed guardian diagnostic records and forwards them to the owner's `ILogger` without losing severity. |
| `ProcessStartInfoHelper` | Formats executable arguments, including Windows batch shims and the .NET Framework argument-quoting path. |

CLI layout helpers use `LayoutProcessRunner`. `GuestRuntime` supplies its injected process factory to `ProcessGuestLauncher`, and integration package installation in `GuestAppHostProject` uses the same factory. RemoteHost's `IntegrationHostProcessLauncher` supplies the raw runtime command to `ChildProcessFactory` with `OwnedTree`, then retains the started execution in a `ProcessScope`.

Factories configure executions without starting them. This matters for extension-managed launches, which inspect command and environment metadata without starting a CLI-owned process. Guardian wrapping happens only inside `ChildProcess.StartAsync`, so constructing an execution does not allocate a guardian or orphan a runtime.

## Owned-tree launch and containment

1. The caller provides the runtime command and selects `OwnedTree`.
2. `ChildProcess.StartAsync` prepares a private completion path and calls `ProcessSupervisor.CreateStartInfo`. The guardian reenters the owner's executable; managed launches preserve the application DLL when reentering through `dotnet`.
3. The guardian receives the runtime command through a private environment handoff, preserving both raw `Arguments` and `ArgumentList`. The handoff also carries owner identity, completion path, and termination timeout.
4. Before launching the runtime, the guardian establishes an isolated Unix session/process group or enrolls itself in a Windows job.
5. The guardian launches the runtime using its own `ChildProcess` with `CallerManaged`. This avoids recursively creating guardians. The runtime inherits the guardian's output pipes and containment.
6. The guardian watches runtime exit and owner liveness independently of user code. Its private handoff variables are removed from the runtime's environment.

The guardian removes the normal CLI watchdog identity from its environment. A competing cooperative watchdog must not terminate it before it reaps descendants.

### Unix

The guardian establishes a new session/process group before spawning the runtime. Group membership survives runtime exit and descendant reparenting, unlike ancestry-based tree traversal. The guardian and the owning `ChildProcess` can therefore terminate workers after their original root has exited.

On retirement the guardian kills its group, including itself. If the guardian has already died, the owner still terminates and verifies the group. Startup cleanup also covers the interval before the guardian has established its group: the owner kills the root, then checks group cleanup again.

### Windows

The guardian enrolls itself in a non-breakaway, kill-on-close job before starting the runtime. Workers inherit the job. Closing the guardian's job handle, including on guardian death, causes the OS to terminate the contained processes.

Owned-tree launch preserves the caller's `CreateNoWindow` choice and restricts inherited handles. AppHost launches retain their separate breakaway policy; placing them inside a non-breakaway guardian job would prevent DCP from finishing container cleanup.

Containment is a lifecycle boundary, not a sandbox against hostile code. Workers must not deliberately escape their group or job.

## Exit observation and completion

`WaitForRootExitAsync` observes the directly launched OS process without waiting for inherited pipes to close. For an owned execution that process is the guardian, and `ProcessId` identifies the guardian, not the runtime. Supervisors use this early signal to begin recovery; it is not a substitute for verified cleanup.

`WaitForExitAsync` observes exit, verifies owned containment, and drains trailing output within the output-drain budget. Callers must await full completion or dispose the execution before treating its scope as retired.

The guardian writes the runtime's exit code to the private completion path before cleanup. This is necessary on Unix because group termination kills the guardian: its OS exit code is not the runtime's result. `ChildProcess` reads the runtime result while still requiring containment cleanup. A guardian that exits successfully without reporting command completion is an error, not a successful command.

The default completion directory is securely created on start and deleted on disposal, including failed launches. A caller-supplied completion path remains caller-owned.

Integration package installation succeeds only after command completion and cleanup have both been verified. Installation is a CLI restore operation; integration-host recovery does not reinstall dependencies.

## Cancellation and disposal

`ChildProcess` owns the common shutdown implementation for both cancellation and disposal:

1. If the caller supplies an enabled graceful window and signaler, signal and exit observation run concurrently within that window.
2. If graceful exit does not finish in time, terminate the process tree and bound exit observation by the termination timeout.
3. For owned executions, retire and verify containment even if the root already exited.
4. Drain output and release process handles. Disposal is idempotent.

Without a graceful window, cleanup goes directly to the force-kill path. Force-tree cleanup does **not** send a courtesy signal first: the root could exit and reparent workers before the subsequent tree walk. Root-only Unix cleanup retains its courtesy signal.

On Unix, the guardian forwards graceful signals to the runtime while remaining alive to perform containment cleanup. On Windows, console events reach the guardian and runtime; the guardian suppresses its own early console-event exit so runtime cleanup can proceed.

Termination and group-disappearance phases are bounded. `ChildProcessOptions` defaults to five seconds for termination and five seconds of output-drain inactivity; callers can supply different values and a `TimeProvider`. The CLI supplies its shared command-wide graceful-shutdown window rather than allocating a new grace period for every child.

Cleanup failures are logged and propagated. Cancellation plus failed termination preserves both errors. Launch owners preserve an operation failure together with a cleanup failure rather than losing one during disposal. Recovery must not start a replacement while cleanup of the previous scope is unverified.

## Output and diagnostics

Children receive null input or an immediately closed redirected input writer, so package-manager lifecycle scripts see EOF rather than inheriting a terminal and blocking. Output pumps read stdout and stderr concurrently. Root-exit observation is separate from output drain because a surviving descendant can hold inherited pipes open.

Output drain uses an idle budget: progressing output resets the clock. A throwing consumer callback does not stop the pump and back-pressure the runtime; its failure is logged during drain. Idle-budget exhaustion produces a warning rather than an unbounded wait.

The guardian starts before normal application logging is initialized. `ProcessSupervisorLogger` writes single-line JSON records to inherited stderr with a reserved prefix, original severity, supervisor PID, and escaped exception text. It flushes synchronously because Unix group cleanup terminates the guardian itself.

The owner forwards valid records through `ILogger`. Startup and successful completion are informational; nonzero runtime completion and owner death are warnings; supervisor failures are errors. Ordinary runtime stderr remains separate. Malformed prefixed records produce a warning and preserve the original stderr line. These records are intentional diagnostic transport, not unstructured debugging console output.

## Command construction and testability

Callers pass raw runtime commands to injected factories rather than manually wrapping them in a guardian. `GuestRuntime` and scaffolding use the supplied `IProcessExecutionFactory` instead of creating a private factory. This keeps launch policy consistent and allows tests to substitute the creation boundary.

Windows `.cmd` and `.bat` commands still require `cmd.exe`. `ProcessStartInfoHelper` preserves empty arguments, quoted values, shell metacharacters, literal percent/exclamation text, and trailing backslashes. Guardian handoff preserves the formatter's raw command line rather than reconstructing it from `ArgumentList`.

Real-process tests under [`tests/Shared/Processes`](../../tests/Shared/Processes) exercise natural root exit, worker cleanup, cancellation, disposal, graceful forwarding, escalation, exit-code handoff, diagnostics, and Windows guardian-to-batch-to-final-child argument preservation. CLI adapter tests cover option propagation and real guardian console signaling. [`IntegrationHostLifetimeTests`](../../tests/Aspire.Cli.EndToEnd.Tests/IntegrationHostLifetimeTests.cs) exercises crashes, stalls, recovery, and cleanup through actual CLI runs.

MTP test executables cannot be blindly reentered as guardians: that would recursively run the tests. Shared fixtures reenter a minimal host compiled from production shared source, while CLI guardian tests reenter the actual CLI. They substitute the entry executable without replacing command handoff or OS containment.

When adding a launch path, choose its lifetime explicitly, reuse the injected factory, retain ownership until full completion or disposal, and verify actual child/worker identities in lifecycle tests. A root-exit assertion alone does not prove that an owned scope has been cleaned up.
