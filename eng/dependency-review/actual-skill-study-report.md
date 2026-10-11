# Dependency review pilot: final comparison (not CI-confidence)

**IGNORE — DO NOT MERGE. Detection-only experiment.** The comparison does not
demonstrate dependency-defect detection uplift. The actual skill-invoked pair
invoked the repository's existing `pr-testing` skill and ties on the frozen
targets and execution classifications. It did **not** evaluate the user's
separate, local-only CI-confidence skill, which was not available in this
remote session. The Copilot reviewer/CI-confidence-skill combination was
therefore not tested. Concise repository evidence guidance improves some
aggregate labels in exploratory text-only runs, but does not consistently
improve review findings. No dependency defect was repaired, original PR
mutated, or compatibility/upstream-safety certification issued.

Implementation draft: [#20741](https://github.com/microsoft/aspire/pull/20741).
Seeded drafts: [NuGet #20742](https://github.com/microsoft/aspire/pull/20742) and
[npm #20743](https://github.com/microsoft/aspire/pull/20743).
Instruction-only hosted control:
[#20744](https://github.com/microsoft/aspire/pull/20744).

## Prespecified outcomes and separation of evidence

The same 15 synthetic cases and 19 target findings were supplied to both
repository `pr-testing` skill assessors. The oracle was frozen separately before
the initial assessment calls. Cases cover documented pins/coupled majors,
host/type and runtime-floor risks, coupled toolchain updates, approved-feed
authentication blockage,
generated Actions drift versus harmless wrapper/runtime differences, unknown
allow-list policy, stateful image migration/platform evidence, resolved versus
selector advisory claims, legitimate patch/minor updates, encoded registry
scopes, stale-head/selection-versus-execution evidence, ordinary invariant
regressions, and visual-only CSS.

Expected negatives include no advisory accusation without an affected resolved
graph, no policy violation inferred from API 403, no incompatibility inferred
from feed 401, no wrapper/runtime equality requirement, and no CSS-only test
demand. The ordinary-code case must retain both null and duplicate-name
correctness findings; dependency guidance must not eclipse those findings.

Checker errors, expectations, previous outputs and scoring materials were
excluded from assessment inputs. Both assessors used only the same recorded
scenario evidence; neither installed packages or examined source PRs. H0/H1
are fixture labels, not real commit SHAs. Frozen finding emission is the primary
metric; recognition in states/tables is a separately disclosed posthoc metric.
The machine-check fixtures are a different arm, not hints fed to these models.

## Existing repository `pr-testing` skill pair (not CI-confidence)

Both fresh project sessions called `functions.skill` with `skill="pr-testing"`
as their first action and received:
`Skill "pr-testing" loaded successfully. Follow the instructions in the skill context.`
Raw assessment return outputs, recorded invocation returns, loaded file blobs,
per-case decisions and recomputed scores are retained in
[actual-skill-study-results.json](actual-skill-study-results.json).
The `actual-skill-*` filenames refer only to this repository `pr-testing`
skill experiment. They are not evidence about, or a substitute for, the distinct
CI-confidence skill the user intended to evaluate.

The user's correction clarified that CI-confidence is installed only on their
local machine, outside the available repository/files and skill catalog in
this remote environment. An attempted folder attachment reported `ENOENT` for
the user's local path, so its contents could not be inspected. Accordingly,
no CI-confidence behavior, output, invocation, or combined Copilot-plus-skill
result is assessed here. Findings about this repository's `pr-testing` skill,
enhanced repo instructions, deterministic checks, and hosted Copilot reviews
must remain separate. This report's conclusions are provisional with respect
to the requested CI-confidence comparison; the skill contents must first be
made available to assess it.

Baseline used head `e5f8dc6b1194773458881b400b1e2c91f91a33d8`, original skill blob
`513355cf0b21c2d51045a723556b61a29addbdfd` and CI reference blob
`847c79cea0bb235530a6024845a10cf1e14cdeb2`. Candidate used head
`2bf727a1dcb9fb352d6a600fb7de1b89a6dae8be`, skill blob
`72531e61ad4028003abe939730952dade247e1d5` and dependency guide blob
`25c3fb46d2c56c1d4d73272201bccbc5af98f3b6`.

A fresh judge received anonymized outputs A/B, the same corpus, oracle and
scoring implementation, but no arm mapping, invocation provenance, prior scores,
or enhanced guide. Decisions were saved before revealing A=candidate/B=baseline.
This is **arm-blinded adjudication**, not blinding to expected answers. The
scorer validates complete case coverage and reproduces explicit decisions; it
does not independently establish truth from generated prose.

| Repository `pr-testing` arm | Finding TP/miss | Recognition | FP/unsupported/duplicate findings | Row states | Provided consumer states | Redis/template | Relevant citations | Head labels |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Baseline | 19/0 | 19/19 | 0/0/0 | 15/15 | 28/28 | 2/2 | 77/77 | 15/15 |
| Candidate | 19/0 | 19/19 | 0/0/0 | 15/15 | 23/23 | 2/2 | 73/73 | 15/15 |

Each emits 18 findings, including two supported non-target evidence gaps.
Grouped findings can cover multiple targets. Both preserve ordinary invariants,
correctly distinguish installation blockage from incompatibility, and separate
selected/skipped Redis from executed template coverage. Neither invents an
advisory or CSS testing requirement. Different consumer-row denominators reflect
output granularity; perfect correctness of supplied rows is **not** proof that
every possible required consumer was enumerated. Head scores check synthetic
label equality, not actual artifact provenance.

The pair's configurations were the same requested default project-session
Auto/autopilot settings; concrete backend, reasoning effort, sampling and
context tier were not independently exposed or pinned. The repository
`pr-testing` skill/reference loads differed, but repository heads and inherited
instructions/environment also differed. The evidence therefore does not
isolate skill content as the sole cause. Invocation provenance records actual
reported calls/returns and file identities, not inference from output
resemblance; an independent low-level event-audit export is unavailable.

## Earlier review and guide-only experiments

[Initial report](local-study-report.md) and
[candidate follow-up](local-study-candidate-followup.md) retain all raw outputs
and separate scoring. The initial four full arms have finding TP/miss
19/0, 17/2, 17/2 and 19/0, respectively; all recognize 19/19 when tables count.
Three-case repeats vary at 3/1, 4/0, 4/0 and 4/0, with 4/4 recognition each.
Finding-emission misses can be correctly represented prerequisite gaps in
evidence rows, not failures to recognize the issue.

The candidate follow-up used four calls, although only two were initially
requested: baseline/candidate review and baseline/candidate guide-only evidence.
It is posthoc and unblinded. Finding TP/miss is 19/0, 18/1, 19/0 and 17/2;
aggregate state accuracy is 13/15, 15/15, 13/15 and 15/15. Recognition is 19/19
throughout. One baseline review unexpectedly invoked `code-review`, contaminating
that pairing. Evidence task arms loaded guide text only; they did **not** invoke
`pr-testing` and are not credited as skill-invoked assessments.

Zero substantive finding false positives does not mean every classification
was supported: the initial enhanced review had three unsupported consumer
`blocked` labels; candidate review retains one skipped-Redis label error.
Candidate guide keeps Redis unexercised, but baseline guide already did too.
Supported non-target observations and duplicate counts are retained rather
than discarded as noise. Tiny repeats show variability, not statistical
significance or causal uplift.

Candidate guidance now explicitly separates aggregate dispositions from each
consumer's state, avoids repeating one failed prerequisite as multiple defect
comments, bounds advisory caveats, and preserves ordinary mixed-diff correctness
review. No package-by-package prompt expansion or broader CI selection was added.
The observed reviewer classification error is a remaining candidate limitation,
not evidence that more instructions automatically help.

## Hosted reviewer: different experiment and control confounds

The baseline branch `special-spoon`, head
`f12cfb619e096a31715d9af448e47532aa168152`, retains original instruction surfaces.
Enhanced #20744, head `d3e4346235a381e8e3563f42ba1a52b94ca8a55f`, has the same
base `e5f8dc6b1194773458881b400b1e2c91f91a33d8` and five seed version changes,
plus three guidance files only. Manifest equality was verified before review.
The supported PR tool failed twice for the baseline; live REST enumeration
still finds no baseline PR. No CLI workaround or indefinite retry was used.
Thus there is **no completed hosted pair**.

Enhanced [review 5420252289](https://github.com/microsoft/aspire/pull/20744#pullrequestreview-5420252289)
completed on its exact frozen head at 2026-10-05 20:42:00Z by
`copilot-pull-request-reviewer[bot]`, visible effort Balanced, concrete model
unknown. Its three dependency comments substantiate both Npgsql changes, the
CDN hold and older-VS MSBuild hold: four seeded changes grounded. Kusto is
referenced under CDN/Front Door reasoning without its distinct cluster/API
migration constraint; five changes referenced is not five independently grounded
findings.

Two additional comments flag the checker paths referenced by copied guidance.
The helper was intentionally withheld to avoid supplying deterministic answers.
Its absence is real in this manipulated control, so the root observation is not
an invented false positive; the two comments duplicate one dependency problem.
They are control-artifact noise, not bugs in the production pilot where the
helper exists. Available evidence cannot establish that this noise caused the
weaker Kusto grounding or prove reviewer prioritization. A review-request event
preceded body cleanup, so the exact PR-description snapshot seen by the reviewer
is unknown; seed-disclosure contamination cannot be excluded.

Separately, seeded full-pilot NuGet #20742 has completed exact-head
[review 5419936161](https://github.com/microsoft/aspire/pull/20742#pullrequestreview-5419936161)
at `5f69aaf1599f600bcfbb87fe87293601c45ee102`, retaining all five seeded
violations. That reviewer had the pilot tooling and is not a blind hosted
instruction-only control. Review-request or wrapper-job success was never
counted as a completed review or skill invocation.

## Deterministic and execution evidence

The current focused suite passes **27 Python tests**; baseline constraint and
canonical Node registry scans pass. Earlier relevant Infrastructure.Tests
validation passed **237 tests**, with quarantine/outerloop excluded and no
subsequent C# edits. Guards cover documented holds/majors, approved sources,
URL traversal, empty inspection, legitimate patches and encoded scopes.
They do not snapshot all dependencies, audit upstream advisories, or replace
frozen Yarn coherence checks or the existing generated-workflow compiler gate.

NuGet #20742 remains an intentionally defective five-version fixture: expected
five objective violations, observed exactly five/exit 1. npm #20743 preserves
the full two-file source diff and 962 resolved URLs: source scans pass, but
observed frozen installation is blocked by approved-feed HTTP 401. Compatibility
and minimum-host/runtime consumer scenarios remain unvalidated, not incompatible.
Read-only Actions source evidence proves seven generated workflow outputs drift;
a wrapper/runtime version difference alone would not prove that. Policy API 403
leaves allow-list verification unknown.

Historical real preparation logs independently confirm 13 checker tests and
source checks passed on then-current implementation/npm heads; package builds
and selector artifacts are separate prerequisite evidence. At the later
`2bf727a...` comparison head, CI run 37370938553 is pending, not successful;
Markdownlint succeeded. New report commits are not retroactively covered by
earlier CI runs. Relevant consumer tests selected but queued/skipped are not
counted as executed. No privileged workflow or credential bypass was used.

## Remaining work and decision

The bounded repository-tooling/`pr-testing` comparisons are complete, but the
requested CI-confidence comparison remains pending because its skill content
is unavailable here. Once supplied, a separate matched assessment can be
designed; do not treat the results above as its baseline or outcome.

**No user approval, push or kick is required for these completed bounded local
experiments.** Hosted baseline publication remains blocked by the supported
app-side PR creation path; repairing that path or manually creating a marked
draft would enable a future hosted pair. That optional follow-up is not a
prerequisite for the reported local result. Genuine minimum-host, older-VS,
deployment/platform and privileged-policy evidence remains unexercised or
unverified and would require appropriate environments/access.

All experimental PRs remain drafts marked IGNORE / DO NOT MERGE. No posts,
reviews, branch edits or defect fixes were made on original #20281, #20716,
#20686 or #14555, and nothing was merged. The pilot is a useful narrow objective
guard and evidence-reporting procedure; the present comparison does not justify
a claim of model-review detection improvement or a production rollout.
