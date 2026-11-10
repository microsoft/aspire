# MSBuild performance experiment

The [experiment workflow](../../.github/workflows/msbuild-performance.yml)
compares normal multiprocess MSBuild with experimental in-process multithreaded
MSBuild (`-mt`). It does not change normal CI or the supported MSBuild versions
of shipped Aspire tasks. Aspire's pinned SDK already supports the switch.
Unmigrated tasks run in isolated TaskHost processes in multithreaded builds.

Run the workflow manually on the desired ref once it exists on the default
branch:

```bash
gh workflow run msbuild-performance.yml --ref <branch>
```

Before that, pushing a feature-branch change to the workflow file starts the
experiment. Ordinary source pushes do not. Inspect the Actions summary and
download the `msbuild-performance` artifact for CSV measurements, environment
details, console logs, and per-invocation binlogs. Failed runs retain available
diagnostics; do not treat partial results as a completed comparison.

Restore and a warm-up of each mode are outside the measured region. Three
iterations alternate execution order on the same runner. Each mode measures a
rebuild plus pack, followed by a no-op build plus pack. The package scope
excludes native builds, bundled executables, tests, and playground projects.
Both modes disable MSBuild node reuse. Compare medians for each operation;
these measurements describe a warm package cache, not a cold checkout or the
entire CI critical path. Maximum process RSS is not aggregate build memory.

## Migration boundaries

Only the repository-internal inline task in
[`eng/ReplaceText.targets`](../../eng/ReplaceText.targets) uses the new
`IMultiThreadableTask` / `TaskEnvironment` APIs. RoslynCodeTaskFactory compiles
it against the running, pinned SDK. Its file paths resolve against the task's
project directory rather than the shared process working directory.

The current pinned SDK still routes this inline task to a sidecar despite its
attribute. A strict-mode smoke build confirmed this in diagnostic logs. The
[upstream inline-task routing fix](https://github.com/dotnet/msbuild/pull/15128)
is needed before this preparatory migration can reduce task-host overhead.
Do not count this task as an in-process migration or attribute any measured
speedup to it until a future SDK update and binlog inspection confirm routing.
No SDK upgrade is part of this experiment.

Do not introduce these dependencies into `src/Aspire.Hosting.Tasks` or other
shipped build assets: those must continue to support older MSBuild/Visual
Studio. Do not mark a task multithreadable without auditing its full call chain,
including path resolution, process creation, environment access, and static
state. The
[upstream migration skill](https://github.com/dotnet/msbuild/tree/main/plugins/mt-migration)
is an authoring/review playbook, not a build feature switch. The
[task-authoring analyzer](https://www.nuget.org/packages/Microsoft.Build.TaskAuthoring.Analyzer)
can help audit compiled task projects, but does not replace that review and is
not automatically applied to inline task compilation.

MSBuild 18.12 adds strict-mode checks. Keep MSB4286, MSB4287, and MSB4288 visible
and inspect binlogs for unexpected `MT-sentinel-CWD` paths; the strict-mode enable
message itself is expected. Successful compilation alone does not establish
correct path handling or that a task actually ran in-process.

## Other optimization candidates

- **Reduce unnecessary project scope.** The current reusable package build sets
  `SkipTestProjects=true` but not `SkipPlaygroundProjects=true`. Measure adding
  the latter separately, and check package completeness before changing normal
  CI. The experiment already excludes playground to isolate package work.
- **Fix no-op work before adding caching.** Use the second measurement and
  binlogs to identify targets that still execute, repeated restores, generated
  files rewritten without content changes, and expensive evaluation. Preserve
  generated-file and packaging correctness when making targets incremental.
- **Measure restore independently.** Static-graph restore
  (`-p:RestoreUseStaticGraphEvaluation=true`) is a separate experiment, not part
  of the timed build comparison here.
- **Reduce repeated CI builds.** Use existing test selection and dependency
  partitions to locate duplicate compilation on the critical path. Reusing
  artifacts requires matching configuration, target framework, RID, version,
  and runtime-only dependencies; do not replace required coverage with
  `--no-build`.
- **Profile before changing compiler/analyzer settings.** Binlogs and MSBuild
  evaluation profiling can separate evaluation, compilation, analyzer, and
  packaging costs. Do not globally disable analyzers or correctness checks to
  make timing numbers smaller.

[PerfStar](https://github.com/dotnet/msbuild/blob/main/documentation/specs/proposed/perfStar.md)
tracks upstream MSBuild performance; it does not automatically benchmark Aspire.
Use Aspire's own measurements to decide whether `-mt` should progress beyond
this opt-in experiment. Validate the full build/test matrix and official
packaging separately before enabling it by default.
