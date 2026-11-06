---
applyTo: "eng/github-ci/**,tools/SelectTests/**,tests/Infrastructure.Tests/TestTriggerMap/**,.github/actions/select-tests/**"
---

# Test Selection Guidance

Use this checklist when authoring or reviewing test routing, selector behavior,
or changed-file filtering. Read the **Maintenance** section of
`docs/ci/test-trigger-map.md` for the complete routing contract and examples.
For selector implementation changes, also read
`docs/ci/test-trigger-selector-design.md`; for upstream skip-gate changes, read
`docs/ci/ci-skip-entirely-patterns.md`.

1. **Establish ownership and actual consumers.** Verify evaluated inputs and
   consumer edges in the `Aspire.slnx` ProjectGraph before relying on Layer 1.
   Directory containment and project-directory fallback exemptions are not
   evidence of graph attribution. Trace runtime package/fixture consumption,
   loose inputs, and reusable workflows to the tests or jobs that execute them.
   Check existing conventions and routes before adding another rule.
2. **Match the execution lane.** Curated runtime-only edges to regular-PR jobs
   need eligible regular-PR consumers. Audit scheduling-attribute changes and
   retain an edge only while an eligible consumer still requires it. Preserve
   Layer 1 dependencies and actual quarantine-runner infrastructure routes;
   quarantine-workload discovery is not a manual per-project map inventory.
   Explicitly classify unconditional, dedicated, advisory, and no-consumer
   inputs instead of hiding a regular-PR dependency behind `ignore` or a filter.
   When eligible consumers change the required target set, update the map and
   focused regression coverage in the same change.
3. **Choose the existing representation that expresses the dependency.**
   `path_rules` handle loose inputs, `affected_project_rules` handle additional
   production/non-test project consumers, and `derived_targets` handle work
   implied by selected tests. Runtime-only test/support consumers need path or
   derived routing because affected-project rules exclude test projects.
   Group related inputs sharing consumers and routing constraints; preserve
   expanded selections when regrouping. Follow the maintenance contract for
   glob precision, package-input directory resilience, target groups, and
   concise reasons. Broadening globs merely to shorten YAML is not cleanup.
4. **Check the complete selection path.** Wire selector-gated jobs through
   emitted `run_*` booleans, job dependencies/outputs, and reusable-workflow
   routes. Keep targets outside PR job gates advisory. For filtering-sensitive
   changes, exercise the production prefilter and validate the upstream skip
   gate separately; selector-only tests do not establish workflow admission.
5. **Choose coverage that detects independent mistakes.** Prefer checks that
   discover consumers from workflow dependencies, evaluated project metadata,
   or fixture declarations and compare them with production selector output.
   Guard against empty or incomplete discovery within the documented scan scope
   and cover its supported syntax.
   Use representative real-map positives and deliberate exclusions for distinct
   routing boundaries, not an acceptance case for every YAML entry. Keep broad
   Layer 1 graph-to-dependent-test coverage and representative CLI-bundle and
   Polyglot cases. Synthetic maps cover selector-engine and CLI failure modes.
   Different mechanisms are not interchangeable merely because targets match.

Before removing a case, identify the retained assertion that catches its
regression or disclose the remaining gap. Representation-only regrouping can
reuse existing coverage; compare expanded selections before and after instead
of asserting configuration-list equality, rule counts, ordering, or group names.
Audit complete affected consumer lists and loose-input routes without copying
them into tests or expanding the audit to unrelated map sections. Recheck
consumer artifacts and execution lanes before relaxing a negative expectation.

Finish by running the focused selector suite documented in the maintenance
guide and checking representative selection traces. Confirm that changed paths
reach their actual consumers and independently scheduled targets remain
advisory. Report unverified workflow execution separately from selector results.
