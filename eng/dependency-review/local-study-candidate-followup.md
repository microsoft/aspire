# Candidate guidance follow-up: local paired assessment

**Scope:** Posthoc comparison of the parent-supplied candidate paragraphs against
the original baseline skill text on the same frozen 15-case, 19-target synthetic
corpus. Four fresh assessment calls were made: baseline and candidate review,
plus baseline and candidate guide-only evidence assessment. This is an
unprespecified follow-up, not a causal or statistical uplift test.

## Results

| Arm | Target findings (TP/miss) | Targets recognized in findings/tables | Unsupported defect/safety claims | Case-state labels correct | Valid citation IDs | Redis/template execution |
|---|---:|---:|---:|---:|---:|---:|
| Baseline review text | 19/0 | 19/19 | 0 | 13/15 | 82/82 | 2/2 |
| Candidate review addition | 18/1 | 19/19 | 0 | 15/15 | 76/76 | 1/2 |
| Baseline pr-testing guide only | 19/0 | 19/19 | 0 | 13/15 | 73/73 | 2/2 |
| Candidate guide additions only | 17/2 | 19/19 | 0 | 15/15 | 74/74 | 2/2 |

All four recognized all 19 targets when findings and consumer tables are both
counted. The candidate review arm omitted the feed-401 finding, but its execution
table recorded the blocked restore. The candidate guide-only arm omitted
standalone findings for Redis execution and stale-head evidence, while its
consumer rows correctly recorded Redis as unexercised and the H1 test as
unexercised. These are finding-emission misses under the fixed rubric, not
unsupported findings.

**False-positive finding count: 0. Unsupported factual or upstream-safety
claims: 0.** These counts concern substantive findings, not every state label:
the candidate review arm labels the skipped Redis consumer `blocked`, although
no failed prerequisite is supplied for that step. That is one unsupported
consumer-state classification. Baseline review and baseline guide-only also
mislabel the aggregate `execution`/`stale` case states; their consumer rows
nevertheless distinguish the actual H1 execution state.

Supported non-target gap observations were 3, 2, 0, and 0 respectively; none
was adjudicated a substantive duplicate finding. Consumer-state taxonomy errors
were 0, 1, 0, and 0 in arm order.

## Consumer-state distinctions

- `toolchain/t2` says the selected extension job is **pending** and supplies no
  install, compile, or lint output. All four arms report its consumer as
  `unexercised`.
- `execution/e1` says Redis was selected but its test step was **skipped by a
  false condition**, with zero Redis tests, while the template scenario ran.
  Baseline review, baseline guide-only, and candidate guide-only report
  Redis `unexercised` and the template `executed`. Candidate review alone labels
  Redis `blocked`; the excerpt gives no failed prerequisite for that skip.
- `stale/z1` has a successful H0 artifact/CLI but an H1 job that is **queued**.
  All four execution tables mark the H1 consumer `unexercised`; H0 evidence is
  not used to claim H1 validation. The two original-baseline aggregate states
  say `blocked`; both candidate aggregate states say `unverified`. In the
  candidate guide arm, the aggregate state is separate from the per-consumer
  H1 state, rather than being copied to it.
- `host-types/v2` and `feed401/f2` identify an approved-feed HTTP 401 before
  dependent checks. All arms mark the install/restore prerequisite `blocked`.
  Candidate guide-only marks dependent compile/build/test work `blocked` with
  that failure context; the other outputs call the work `unexercised` while
  their findings or evidence cite the 401. Neither classification claims a
  package incompatibility. The distinction is the documented failed-prerequisite
  exception, unlike pending or skipped work without such a failure.

The evidence-guide candidate arm's `execution` case-state label improved from
`risk` to `unverified`, and `stale` from `blocked` to `unverified` (both now
within the prespecified allowed set). Its Redis/template execution pair was
already correct in the baseline guide-only run, so this follow-up does **not**
show an improvement on that pair. The review-only candidate arm does not include
the consumer-classification sentence and still mislabels the skipped Redis
step. A small, explicit state table could make the boundary even easier to
apply: “skipped/pending/queued without a failed prerequisite → unexercised;
logged failed/denied prerequisite with dependent work prevented → blocked;
actual relevant scenario completed → executed.” Keep this consumer table
separate from the aggregate case disposition.

## Other targeted checks

All arms identify both ordinary-code contract regressions (null and duplicate
name guards removed), grounding them in the supplied code excerpt. None
recommends tests for the visual-only CSS change. All preserve the successful
patch/minor client scenario as exercised, with no advisory finding; the candidate
guide output does not add an advisory-lookup gap row. The selector/lock mismatch
is identified without claiming an upstream advisory. These are synthetic
protocol/code fixtures, not findings about real Aspire dependencies or
vulnerabilities.

## Conditions and limits

The candidate paragraphs were supplied verbatim by the parent. Each evaluator
used a fresh general-purpose synchronous task call with model, reasoning-effort,
and context-tier overrides omitted. Concrete model/backend is unknown; requested
defaults are not verified backend matching. Inherited repository/system rules
remain present. Outputs and annotations are manual and unblinded; no causal
attribution or compatibility/upstream-safety certification follows.

**Four calls were used**, not only two: two baseline-text and two candidate-text
arms were necessary for the review and guide comparisons. One supposed baseline
review evaluator unexpectedly invoked `functions.skill(code-review)` before
reading its prompt. Its output is retained but that review comparison is
contaminated and cannot be treated as an isolated baseline. The evidence guide
arms were guide-loaded only; no `functions.skill(pr-testing)` invocation is
claimed here. Parent-owned actual skill-invoked evaluations are separate and
not scored in this report.

Raw outputs, per-case manual annotations, exact scores, candidate text and
configuration are retained in `local-study-candidate-followup.json`. Exact
prompts and baseline snapshot copies are under:

```text
/workspaces/.copilotd/state/copilot-home/session-state/1479d4d4-b67c-4aef-be32-7c71a919841c/files/paired-study/candidate-followup/
```

No source PRs, dependency versions, policy, checker, or parent guidance were
changed in this follow-up.
