---
name: bump-aspire-version
description: Bumps the Aspire repository product version in eng/Versions.props using previous version-bump commits as guidance. Use when asked to bump Aspire branding, advance the repository to a new major or minor version, or create a version-bump PR. Not for external dependency, SDK, or container-image updates.
---

# Bump the Aspire repository version

The product version is defined in the first property group of `eng/Versions.props`.
Keep the target branch's automatic milestone assignment aligned in
`.github/policies/milestoneAssignment.prClosed.yml`. Do not replace every occurrence
of the old version throughout the repository.

## Inspect the target and history

1. Establish the requested version and target branch. Interpret `X.Y` as `X.Y.0`.
   Ask if the target version is missing; do not infer it from the calendar or SDK.
2. Read the working-tree status and preserve unrelated changes. Use the current
   session branch and follow any app-managed branch naming requirements.
3. Read both files and inspect previous bumps, including their complete diffs so
   related automation changes are not missed:

   ```bash
   git log -8 --oneline -G '<MajorVersion>|<MinorVersion>|<PatchVersion>|AspireDashboardImageTag' -- eng/Versions.props
   git log -5 --oneline -- .github/policies/milestoneAssignment.prClosed.yml
   git show <relevant-commit>
   ```

   Useful precedents are #19139 (`be77aa36da`, 13.5 to 13.6) and #20037
   (`1fd72c6d05`, 13.6 to 14.0 with an unpublished dashboard-image workaround).
   Prefer the latest applicable history over copying an old diff blindly.
4. Confirm the exact target milestone exists and is open in GitHub. If it is
   missing or closed, ask before creating or reopening it; do not leave the policy
   pointing at a milestone the bot cannot assign.
5. Determine whether the bump advances `main` because a new release branch was
   just created (for example, main moves to 17.1 after `release/17.0` is
   branched, as in #21850). If so, confirm that branch exists with
   `git ls-remote --heads origin release/<X.Y>`. The scheduled API and ATS diff
   workflows must follow it; otherwise they keep generating baseline PRs for the
   previous release, and the new release's shipped API surface is never captured.
   Servicing bumps and other bumps that do not follow a new release branch skip
   this.

## Run the deterministic edit

Use the bundled C# file-based app with the repository's .NET SDK rather than
generating ad-hoc editing code.
From the repository root, substitute the requested version, target branch, and
confirmed open milestone:

```bash
dotnet run --file .agents/skills/bump-aspire-version/BumpVersion.cs -- 17.0 --branch main --milestone 17.0
```

When the bump follows a newly created release branch, add
`--api-diff-release-branch release/<X.Y>` to the same command:

```bash
dotnet run --file .agents/skills/bump-aspire-version/BumpVersion.cs -- 17.0 --branch main --milestone 17.0 --api-diff-release-branch release/13.6
```

`--branch` is the PR's target branch, not the feature branch. `--milestone` is
explicit because servicing milestones can differ from product versions (for
example, product `13.6.1` can use milestone `13.6.x`). The script does not create,
reopen, or query GitHub milestones; confirm availability in the previous step.

The script updates all three version components (`X.Y` means `X.Y.0`) and exactly
one matching branch's milestone rule. With `--api-diff-release-branch`, it also
replaces the previous release branch in the scheduled `target_branch` matrix of
`.github/workflows/generate-api-diffs.yml` and
`.github/workflows/generate-ats-diffs.yml`. It validates the expected file
structure before writing any file and fails on missing or ambiguous matches. It
preserves formatting, line endings, unrelated rules, and all other properties.
Repeating the same command makes no further changes. If the file layout changes,
update the script rather than bypassing its checks. Precedent: #22172 retargeted
both workflows from `release/13.6` to `release/17.0`.

When retargeting, also check whether the previous release's shipped baselines
reached `main`. Those are the `src/**/api/*.cs` and `src/**/api/*.ats.txt` files
from the automated API/ATS diff PRs against `release/<old>`; #22152 merged the
13.6 baselines. Report any baselines still missing to the user. Do not import them
in the bump PR unless asked.

Prerelease/stabilization settings and the dashboard-image pin remain unchanged:
a product version bump neither stabilizes packages nor publishes a dashboard image.
Review any version-specific comments separately. Do not change other release-branch
rules, dependencies, SDKs, generated APIs, or sample versions without explicit scope.
The old `.github/workflows/milestone-assignment.yml` was removed in #20390 in favor
of the policy bot; do not recreate it.

## Validate

Review `git diff --check` and the complete diff. Confirm only the intended version
properties, milestone assignment, any directly related comments, and (when
applicable) the scheduled API/ATS diff release branch changed.

Confirm the resulting version components and milestone title match the request.
Run the edit command again and confirm it reports every file unchanged.

Check the diff separately to confirm prerelease settings and the dashboard-image
pin were preserved. If the change extends beyond metadata, run focused validation
for that behavior and follow the repository's SDK setup instructions when needed.

## Create the PR when requested

Use the `create-pr` skill and the repository PR template. Commit only the intended
files, include required commit trailers, push without force, and use the available
PR creation tool when the environment requires it. Describe the old and new
versions, milestone routing, any API/ATS diff workflow retargeting, unchanged
prerelease status, any retained dashboard-image override, and the validation
actually performed.
Do not claim a full build or test run unless
one was performed. Do not merge automatically.
