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
