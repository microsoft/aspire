# Dependency review pilot

This offline Python 3 check enforces a small set of existing documented holds
and Npgsql/EF Core major constraints, plus root NuGet download sources and
extension Yarn resolved download sources. It does not restore or execute
dependencies, query advisories, parse every manifest, or certify compatibility.
Audit sources are not download sources. Missing files and malformed inspected
XML/URL entries fail explicitly.

```bash
python3 eng/dependency-review/check.py
python3 -m unittest discover -s eng/dependency-review -p 'test_*.py'
(cd extension && node scripts/validate-lockfile-registry.cjs)
```

The extension's existing Node validator remains the exact-feed authority.
The pilot's Yarn URL checks complement its substring match with parsed HTTPS,
host, organization-path and credential boundaries; they do not replace it.
The PR preparation step runs both, so an otherwise approved Azure feed is not
mistaken for the extension's specific `dotnet-public-npm` feed.
Both source scans inspect present download URLs, not missing/duplicate lockfile
resolutions. Lockfile completeness and manifest coherence remain the existing
frozen Yarn restore's responsibility. A source-scan pass must not substitute
for that restore, particularly when installation is blocked.
The pilot rejects an extension lockfile with no download entries as incomplete
inspection, but does not infer per-package coherence from a nonempty file.

Exit codes: `0` means these constraints passed, `1` means a constraint failed,
and `2` means inspection could not complete. `--root <checkout>` inspects another
isolated checkout using this script's policy, without modifying it.

`constraints.json` intentionally covers documented exceptions, not a snapshot
of every dependency. An intentional migration must update the policy and nearby
manifest rationale together, with consumer evidence in the PR. Adding a hold
requires a focused positive/negative test; routine patch updates remain allowed.

Generated workflow coherence uses the existing
`.github/workflows/validate-agentic-workflows.yml` compiler gate rather than a
second compiler or a wrapper/runtime-version equality check. Approved-source
checks cannot establish feed reachability or permissions. The evidence procedure
is in `.agents/skills/pr-testing/dependency-review.md`.

## Detection-only local paired study

`corpus.json` contains 15 clearly synthetic blind-input cases, including ordinary
code and visual changes. `expected.json` is the separately frozen outcome oracle;
never supply it, adjudications, checker output, or previous model outputs to an
evaluator. This study does not repair the pilot's source PRs or dependency bumps.

`study.py` prepares exact prompts from read-only baseline skill snapshots and
additional dependency guidance, then scores **explicit human adjudications**.
It does not call a model or infer correctness from claim keywords. The caller
must use the same requested agent type/default settings for all paired calls.
The expected case set is enforced so dropping a case cannot improve scores.
Primary TP/miss counts finding emission against prespecified targets; secondary
recognition includes correct explicit states/tables. Format and state-label
errors are not automatically substantive false positives.

```bash
# Export original skills from the recorded snapshot into an artifact directory
# as baseline-code-review.txt and baseline-pr-testing.txt before preparing.
python3 eng/dependency-review/study.py prepare <artifact-directory>
# Independently execute each prepared prompt; retain raw JSON outputs.
# Record actual tool invocation history in invocations.json, not inferred usage.
# Inspect every output and update an explicit adjudication plan for a NEW study.
python3 eng/dependency-review/study.py adjudicate <artifact-directory>
python3 eng/dependency-review/study.py score <artifact-directory> --run baseline-review
python3 eng/dependency-review/study.py deterministic <artifact-directory>
python3 -m unittest discover -s eng/dependency-review -p 'test_*.py'
```

`adjudication-plan.json` and `local-study-results.json` belong to the recorded
2026-10-05 run, not future outputs. `adjudicate` writes the retained output bundle
in this directory; do not overwrite it accidentally with a different experiment.
`deterministic-fixtures.json` is a separate post-model projection of objective
hold/source cases, not blind input or a compatibility oracle.

See `local-study-report.md` for scores, paths, repeat variability and limits.
The initial enhanced skill evaluators could not invoke `skill(pr-testing)`
because the task tool did not expose it. Those outputs are **guide-loaded only**,
not compliant skill-invoked assessments. `local-study-candidate-followup.md`
records the separate posthoc, unblinded candidate-text comparison.
`actual-skill-study-report.md` records a subsequent project-session pair that
explicitly invoked the repository's existing `pr-testing` skill and was scored
by an arm-blinded judge. **This is not the user's separate local-only
CI-confidence skill and does not test that skill or its combination with
Copilot review.** Its contents were unavailable in this remote session; the
attempted local-folder attachment returned `ENOENT`. These are distinct
experiments, not interchangeable evidence. The intended CI-confidence
comparison remains pending until its actual skill contents are made available.
No result is a compatibility/safety certificate.

Reproduce the repository `pr-testing` skill pair's recorded scores without
calling a model:

```python
import sys

sys.path.insert(0, "eng/dependency-review")
import study

corpus = study.load(study.ROOT / "corpus.json")
expected = study.load(study.ROOT / "expected.json")
bundle = study.load(study.ROOT / "actual-skill-study-results.json")
for arm, run in bundle["runs"].items():
    result = study.score(
        corpus, expected, run["output"], run["adjudication"], expected["cases"].keys()
    )
    assert result == run["scores"], arm
    print(arm, result["totals"])
```
