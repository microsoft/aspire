#!/usr/bin/env bash

# Preserve the Actions log connection on fd 3 before silencing stdout/stderr.
# The ERR trap writes only a fixed phase/exit-code summary through fd 3;
# its ::error:: prefix tells the Actions runner to create an error annotation.
# API bodies and command diagnostics must never reach the public runner log.
exec 3>&2
exec >/dev/null 2>&1
set -euo pipefail
phase=initialization
trap 'printf "::error::Auto-sec collection failed (%s; exit %s).\n" "$phase" "$?" >&3' ERR
mkdir -p .auto-sec

# Only structured fields are kept. Advisory text (summaries, descriptions,
# CVE prose) is dropped so it cannot be copied into a public PR or prompt.
# A 403 here means the token lost alert access; fail loudly rather than
# report a misleading "nothing to do". Every advisory range for the alert's
# package is kept, not just the one the installed version fell in, so the
# coverage check below rejects a target inside a later disjoint range, as the
# approval job does. The projection runs in `jq`, not `gh --jq`, so the
# linter treats the `$pkg` and `$key` bindings as jq variables.
phase=dependabot-alerts
gh api --paginate "/repos/${REPO}/dependabot/alerts?state=open&per_page=100" --jq '.[]' \
  | jq -s 'map(.dependency.package as $pkg | ($pkg.name | ascii_downcase | gsub("[-_.]+"; "-")) as $key | {number, ecosystem: $pkg.ecosystem, package: $pkg.name, manifest_path: .dependency.manifest_path, scope: .dependency.scope, relationship: .dependency.relationship, vulnerable_version_range: .security_vulnerability.vulnerable_version_range, vulnerable_ranges: ([.security_vulnerability.vulnerable_version_range] + [(.security_advisory.vulnerabilities // [])[] | select(.package.ecosystem == $pkg.ecosystem and (.package.name | ascii_downcase | gsub("[-_.]+"; "-")) == $key) | .vulnerable_version_range] | map(select(type == "string" and . != "")) | unique), first_patched_version: .security_vulnerability.first_patched_version.identifier})' \
  > .auto-sec/dependabot-alerts.json
phase=malware-alerts
gh api --paginate "/repos/${REPO}/dependabot/alerts?state=open&classification=malware&per_page=100" \
  --jq '.[] | .number' | jq -s '.' > .auto-sec/malware-alert-numbers.json
phase=alert-annotation
jq --slurpfile malware .auto-sec/malware-alert-numbers.json \
  'map(. + {malware: (.number as $n | $malware[0] | index($n) != null)})' \
  .auto-sec/dependabot-alerts.json > .auto-sec/alerts.json

# Code scanning alerts are in scope only when the rule maps to an action or
# package version. Everything else is a code finding and is only counted.
VERSION_RULES='["zizmor/unpinned-uses","zizmor/impostor-commit","zizmor/ref-confusion","zizmor/known-vulnerable-actions","zizmor/stale-action-refs","zizmor/archived-uses"]'
phase=code-scanning-alerts
gh api --paginate "/repos/${REPO}/code-scanning/alerts?state=open&per_page=100" \
  --jq '.[] | {number, rule: .rule.id, tool: .tool.name, path: .most_recent_instance.location.path, start_line: .most_recent_instance.location.start_line}' \
  | jq -s --argjson rules "${VERSION_RULES}" '{
      version_alerts: [ .[] | select(.rule as $r | $rules | index($r)) ],
      other_rule_counts: ([ .[] | select(.rule as $r | ($rules | index($r)) | not) ] | group_by(.rule) | map({rule: .[0].rule, count: length}))
    }' > .auto-sec/code-scanning.json

# Open Dependabot PRs with their parsed updates, CI rollup, and the alerts each
# one provably covers. Coverage uses the same module as the approval gate:
# the head version of every changed manifest is fetched and must bind the
# alert package to the new version in the alert's own manifest. The base
# version is diffed too, so lockfile-only (transitive) upgrades count. Bodies
# are parsed and then dropped. Alerts describe main, so only PRs into main count.
phase=dependabot-pull-requests
gh pr list --repo "${REPO}" --author "app/dependabot" --state open --base main --limit 200 \
  --json number,title,body,headRefName,headRefOid,baseRefOid,isDraft,files,statusCheckRollup \
  > .auto-sec/dependabot-prs-raw.json
phase=dependabot-coverage
node .github/workflows/auto-sec/collect-prs.js
rm .auto-sec/dependabot-prs-raw.json .auto-sec/dependabot-alerts.json

phase=auto-sec-pull-requests
gh pr list --repo "${REPO}" --label auto-sec --state open --limit 20 \
  --json number,title,headRefName,headRefOid,author > .auto-sec/auto-sec-prs.json

# Bind public totals to collection, not the agent-writable copies of these files.
phase=report-counts
node -e '
  const fs = require("node:fs");
  const path = require("node:path");
  const m = require("./.github/workflows/auto-sec/auto-sec.js");
  const counts = m.collectReportCounts(
    JSON.parse(fs.readFileSync(".auto-sec/alerts.json", "utf8")),
    JSON.parse(fs.readFileSync(".auto-sec/code-scanning.json", "utf8")));
  fs.writeFileSync(path.join(process.env.RUNNER_TEMP, "auto-sec-gate", "collected-counts.json"),
    JSON.stringify(counts), { flag: "wx", mode: 0o600 });
'

echo "Alerts: $(jq length .auto-sec/alerts.json) (malware: $(jq length .auto-sec/malware-alert-numbers.json))"
echo "Version-mapped code scanning alerts: $(jq '.version_alerts | length' .auto-sec/code-scanning.json)"
echo "Open Dependabot PRs: $(jq length .auto-sec/dependabot-prs.json)"
echo "Open auto-sec PRs: $(jq length .auto-sec/auto-sec-prs.json)"
