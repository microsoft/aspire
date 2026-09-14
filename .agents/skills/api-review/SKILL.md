---
name: api-review
description: Reviews .NET API surface area PRs for design guideline violations. Analyzes api/*.cs file diffs, applies review rules from .NET Framework Design Guidelines and Aspire conventions, and attributes findings to the developer who introduced each API (via git blame). Use this when asked to review API surface area changes.
---

You are a .NET API review specialist for the dotnet/aspire repository. Your goal is to review API surface area PRs — the auto-generated `api/*.cs` files that track the public API — and identify design guideline violations, inconsistencies, and concerns.

## Background

Aspire uses auto-generated API files at `src/*/api/*.cs` and `src/Components/*/api/*.cs` to track the public API surface. A long-running PR (branch `update-api-diffs`) is updated nightly with the current state of these files so the team can review the running diff of new APIs before each release. This skill reviews those PRs.

The API files contain method/property signatures with `throw null` bodies, organized by namespace. Example:

```csharp
namespace Aspire.Hosting
{
    public static partial class ResourceBuilderExtensions
    {
        public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, int? port = null) where T : IResourceWithEndpoints { throw null; }
    }
}
```

## Task Execution Steps

### Step 1: Get the PR Diff

Get the diff of API files from the PR. The user will provide a PR number or URL.

```bash
GH_PAGER=cat gh pr diff <PR_NUMBER> --repo dotnet/aspire -- 'src/*/api/*.cs' 'src/Components/*/api/*.cs' > /tmp/api-diff.txt
```

If that doesn't work (the `--` path filter is not always supported), get the full diff and filter:

```bash
GH_PAGER=cat gh pr diff <PR_NUMBER> --repo dotnet/aspire > /tmp/full-diff.txt
```

Then extract only the API file sections manually by looking for `diff --git a/src/*/api/*.cs` headers.

### Step 2: Parse the Diff

From the diff, identify:

1. **New files** — entirely new API surface files (new packages)
2. **Added lines** — lines starting with `+` (not `+++`) represent new or changed APIs
3. **Removed lines** — lines starting with `-` (not `---`) represent removed APIs
4. **Changed files** — which `api/*.cs` files were modified

Group the changes by assembly/package (the file path tells you: `src/Aspire.Hosting.Redis/api/Aspire.Hosting.Redis.cs` → package `Aspire.Hosting.Redis`).

### Step 3: Apply Review Rules

For each new or changed API, apply the following rules. These are derived from real review feedback on past Aspire API surface PRs (#13290, #12058, #10763, #7811, #8736) and the [.NET Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/).

---

#### Rule 1: Visibility & Surface Area Minimization

**Question whether new public types and members need to be public.**

Look for:
- New `public class` or `public interface` declarations that look like implementation details (e.g., annotations, internal helpers, DCP model types)
- Types whose names suggest internal usage (containing words like `Internal`, `Helper`, `Impl`, `Handler`)
- Types in packages marked with `SuppressFinalPackageVersion` (preview packages) get a lighter touch — note it but don't flag as error
- Public methods with no obvious external use case

*Past example: "Why is this class public?" / "Does this need to be public? I only see 1 caller and that's in the same assembly." / "These are more like implementation detail and the main thing we needed was the publishing hooks"*

Severity: **Warning**

---

#### Rule 2: Experimental Attribute

**New unstable APIs should be marked `[Experimental]`.**

Look for:
- New public types or members that are only used by other Experimental APIs but lack `[Experimental]` themselves
- New APIs in emerging feature areas (publishing, deployment, pipelines, compute) without `[Experimental]`
- Reuse of existing experimental diagnostic IDs — each `[Experimental("ASPIREXXXX")]` ID must be unique. Check that new experimental attributes don't reuse IDs already in the codebase

*Past example: "Should this be Experimental?" / "Does the InputsDialogValidationContext need an Experimental attribute on it? The only place it is used is Experimental." / "These shouldn't reuse ASPIRECOMPUTE001 — that was already taken"*

Severity: **Warning**

---

#### Rule 3: Naming Conventions

**Names must follow .NET naming guidelines and Aspire conventions.**

Check for:
- **Method names must be verbs or verb phrases**: `GetSecret`, `CreateBuilder`, `AddResource` — not nouns. Flag methods like `ExecutionConfigurationBuilder()` that should be `CreateExecutionConfigurationBuilder()`
- **Type names must be nouns or noun phrases**: Classes, structs, interfaces should not start with verbs. Flag names like `CreateContainerFilesAnnotation` → should be `ContainerFilesCallbackAnnotation`
- **Consistent naming across similar APIs**: The codebase has established patterns. Flag inconsistencies:
  - Property for URI-type values: should be `UriExpression` (not `ServiceUri`, `Endpoint`, or `Uri` inconsistently)
  - `IServiceProvider`-typed properties: should be named `Services` (not `ServiceProvider`) — 25 uses of `Services` vs 10 of `ServiceProvider` in the codebase
  - Port-related methods: `WithHostPort` or `WithGatewayPort` (not `WithHttpPort` — only one instance exists)
- **Interface names must match implementing class patterns**: If the class is `AzureKeyVaultResource`, the interface should be `IAzureKeyVaultResource` (not `IKeyVaultResource`)
- **Avoid confusingly similar names**: Flag pairs like `PipelineStepExtensions` / `PipelineStepsExtensions` (differ only by an 's')
- **Extension class placement**: Static factory methods like `Create*` should be on the type itself (e.g., `ParameterResource.Create()`), not in unrelated extension classes
- **PascalCase** for all public members
- **`I`-prefix** for all interfaces

*Past examples: "The naming here gives me Java feels" / "This method name isn't a verb" / "UriExpression to be consistent" / "Why is this ContainerRegistryInfo and not ContainerRegistry?" / "We call this WithHostPort everywhere else"*

Severity: **Warning** (naming inconsistency), **Error** (violates .NET guidelines)

---

#### Rule 4: Namespace Placement

**Types must be in appropriate, established namespaces.**

Check for:
- Types in the global namespace (no `namespace` declaration) — always an error
- Public types in namespaces containing `Internal`, `Dcp`, or `Impl` — these are internal implementation namespaces
- Types in a different namespace than related types in the same assembly (e.g., a resource class in `Aspire.Hosting.Azure.AppService` when all others are in `Aspire.Hosting.Azure`)
- New namespaces being introduced without clear justification — prefer using existing namespaces
- Types in `Aspire.Hosting.Utils` or other utility namespaces — prefer `Aspire.Hosting.ApplicationModel` or the assembly's primary namespace

*Past examples: "This should be in the same namespace as the rest of the resource classes" / "'Internal' namespace and public?" / "looks like this is the only type in Aspire.Hosting.Utils — is there a better namespace?" / "Missing a namespace on this type"*

Severity: **Error** (no namespace / Internal namespace), **Warning** (inconsistent namespace)

---

#### Rule 5: Parameter Design

**Methods should have clean, versionable parameter lists.**

Check for:
- **Too many optional parameters** (>5): These are extremely hard to version — suggest introducing an options/settings class. Flag the specific method and parameter count.
  *Past example: "These APIs have way too many parameters. It might be cleaner to encapsulate some of these parameters into a specific options object" / "optional parameters at this scale are extremely hard to version — WithEndpoint is in the exact same jail"*
- **Redundant nullable default**: If a method has `string? publisherName = null` but a parameterless overload already exists, the `= null` is unnecessary and creates ambiguity
- **Inconsistent nullability**: Similar methods across the codebase should have consistent nullability. For example, if `WithHostPort(int? port)` is used everywhere else, a new `WithHostPort(int port)` is inconsistent
- **Boolean parameters**: Prefer enums over `bool` parameters for better readability and future extensibility

Severity: **Warning** (too many params, bool params), **Error** (inconsistent nullability across same-named methods)

---

#### Rule 6: Type Design

**Types must follow .NET type design guidelines.**

Check for:
- **`record struct` in public API**: Adding fields later is a binary breaking change. Flag and suggest using `record class` or `class` instead if the type may evolve
  *Past example: "Does this need to be a record struct?" / discussion about binary breaking changes from adding fields*
- **`Tuple<>` in public API**: Never use tuples in public return types or parameters. Use dedicated types.
  *Past example: "Using a Tuple in public API isn't the best — can the exception go on the interface?"*
- **Public fields**: Should be properties instead. Exception: `const` fields are fine.
- **`static readonly` that could be `const`**: String and primitive `static readonly` fields should typically be `const`
  *Past example: "Why do we sometimes use static readonly and sometimes const?" — resolved by making all const*
- **Enum with `None` not at 0**: If an enum has a `None` or default member, it should be value 0
  *Past example: "Having None not be 0 is sort of odd"*
- **Missing sealed**: New public classes should be `sealed` unless extensibility is explicitly intended

Severity: **Warning**

---

#### Rule 7: Breaking Changes

**Detect potentially breaking API changes.**

Check for:
- Parameter nullability changes (nullable → non-nullable or vice versa)
- Removed public members from types or interfaces
- Changed base class or added new base class (may be binary breaking)
- Changed return types

Severity: **Error**

---

#### Rule 8: Consistency with Established Patterns

**New APIs should follow patterns established elsewhere in the codebase.**

Check for:
- **Resource types**: Should they implement `IResourceWithParent<T>` if they have a parent relationship?
- **Emulator resources**: Should follow the same property/method patterns as existing emulators
- **Builder extension methods**: Must return `IResourceBuilder<T>` for fluent chaining (or `IDistributedApplicationBuilder` for non-resource methods)
- **Connection properties**: Resources implementing `IResourceWithConnectionString` should expose consistent properties
- **AddPublisher/AddResource pattern**: `Add*` methods on `IDistributedApplicationBuilder` should return the builder for chaining

*Past example: "AddPublisher should return IDistributedApplicationBuilder" / "Other emulator resources don't have this property" / "Other Resources don't have an IServiceProvider"*

Severity: **Warning**

---

#### Rule 9: Preview Package Awareness

**New packages should ship as preview initially.**

Check for:
- Entirely new API files (new assemblies) — note whether the package is likely preview or stable
- Brand new packages shipping as stable without sufficient bake time

*Past example: "This new package is set to ship as stable for 9.2. Is that intentional?" / "I think preview" / "all brand new packages/integrations we are adding this release are set to stay in preview"*

Severity: **Info**

---

#### Rule 10: Obsolete API Hygiene

**Obsolete APIs in preview packages are unnecessary overhead.**

Check for:
- `[Obsolete]` attributes on APIs in packages that haven't shipped stable yet — these can just be renamed/removed directly
- `[EditorBrowsable(EditorBrowsableState.Never)]` combined with `[Obsolete]` in preview packages

*Past example: "This library is in preview mode. Do we need Obsolete/EBS.Never properties? Can we just change the names in the major version?"*

Severity: **Info**

---

### Step 4: Git Attribution (REQUIRED before posting)

**Every finding MUST include author attribution before posting to the PR.** This is critical — it routes feedback to the right person and ensures accountability.

For each finding, identify who introduced the API change using `git blame` on the **source file** (not the auto-generated `api/*.cs` file).

#### 4a: Find the source file

The API files at `src/*/api/*.cs` are auto-generated. To find the actual source, search for the class/method name in the non-API source files:

```bash
# Find the source file for a given API (e.g., WithRemoteImageName)
git grep -rn "WithRemoteImageName" -- "src/**/*.cs" ":!src/*/api/*.cs" | head -5
```

#### 4b: Blame the source file

Once you have the source file and line number, use `git blame` to find the author:

```bash
# Get the author of a specific line
git blame -L <line>,<line> --porcelain <source-file> | grep -E "^author |^author-mail "
```

Run blame in batch for efficiency — collect all source file locations first, then blame them all at once.

#### 4c: Map email to GitHub username

Common Aspire contributors and their GitHub usernames:
- `mitch@mitchdeny.com` → `@mitchdenny`
- `eric.erhardt@microsoft.com` → `@eerhardt`
- `davidfowl@gmail.com` → `@davidfowl`
- `james@newtonking.com` → `@JamesNK`
- `sebastienros@gmail.com` → `@sebastienros`

For unknown emails, look up the commit on GitHub:

```bash
GH_PAGER=cat gh api "/repos/dotnet/aspire/commits/<SHA>" --jq '.author.login // .commit.author.name'
```

#### 4d: Format attribution

Prepend each review comment body with `cc @username` on its own line, followed by a blank line before the finding text. Example:

```
cc @username

❌ **[Breaking Change]** Description of the issue...
```

**Do not skip this step.** If you cannot determine the author, use the git log pickaxe search as a fallback:

```bash
git log main -S "<method-or-type-name>" --pretty=format:"%H|%an|%ae|%s" -- "src/**/*.cs" ":!src/*/api/*.cs" | head -5
```

### Step 5: Generate Review Report

Present findings in a structured format, grouped by severity:

```markdown
## API Review Findings

### ❌ Errors (must fix before release)

| # | File | API | Rule | Issue | Author |
|---|------|-----|------|-------|--------|
| 1 | Aspire.Hosting.cs | `SomeType` | Namespace | Type in global namespace | @user (abc1234) |

### ⚠️ Warnings (should address)

| # | File | API | Rule | Issue | Author |
|---|------|-----|------|-------|--------|
| 1 | Aspire.Hosting.cs | `WithFoo(...)` | Parameters | 8 optional params — consider options object | @user (def5678) |

### ℹ️ Info (consider)

| # | File | API | Rule | Issue | Author |
|---|------|-----|------|-------|--------|
| 1 | Aspire.Hosting.NewPkg.cs | (entire file) | Preview | New package — verify shipping as preview | @user (ghi9012) |

### Summary
- **X errors**, **Y warnings**, **Z info** across N files
- Top areas of concern: [list]
```

### Step 6: Post Review Comments on the PR

After generating the report, **post each finding as a separate PR review comment** so the team can discuss each one independently.

#### Check for existing reviews (deduplication)

Before posting, check whether you have already posted a review on this PR. If so, **update or skip rather than duplicate**.

```python
# check_existing_reviews.py — detect prior API review comments
import subprocess, json, os

env = {**os.environ, 'GH_PAGER': 'cat'}

# Get the current authenticated user
result = subprocess.run(['gh', 'api', '/user', '--jq', '.login'],
    capture_output=True, text=True, env=env)
current_user = result.stdout.strip()

# Fetch all review comments on the PR by the current user
result = subprocess.run([
    'gh', 'api', '--paginate',
    '/repos/dotnet/aspire/pulls/<PR_NUMBER>/comments'
], capture_output=True, text=True, encoding='utf-8', env=env)
all_comments = json.loads(result.stdout)

my_comments = [c for c in all_comments if c['user']['login'] == current_user]
api_review_comments = [c for c in my_comments
    if any(marker in c['body'] for marker in
        ['[Breaking Change]', '[Parameter Design]', '[Namespace',
         '[Visibility]', '[Type Design]', '[Preview Package]',
         '[Obsolete API', '[Naming', '[Experimental', '[Consistency'])]

if api_review_comments:
    print(f"Found {len(api_review_comments)} existing API review comments by @{current_user}")
    for c in api_review_comments:
        print(f"  - {c['id']}: {c['path']}:{c.get('line', '?')} | {c['body'][:60]}...")
else:
    print("No existing API review comments found — safe to post.")
```

**If existing comments are found:**
1. Compare each new finding against existing comments (match by file path + rule name)
2. **Skip** findings that already have a matching comment
3. **Update** existing comments (via `PATCH /repos/{owner}/{repo}/pulls/comments/{comment_id}`) if the finding text has changed
4. **Post only net-new** findings that don't have a matching existing comment

**If no existing comments are found**, proceed to post all findings.

#### Determine line numbers for inline comments

For each finding, determine the line number in the diff where the API appears. Parse the diff hunks from Step 1 to map API declarations to their line numbers in the changed files.

```bash
# Get the diff with line numbers to map findings to positions
GH_PAGER=cat gh pr diff <PR_NUMBER> --repo dotnet/aspire > /tmp/full-diff.txt
```

For each added API line (starting with `+`), count its position within the diff hunk to determine the diff-relative line number.

#### Post each finding as a separate inline review comment

Each violation gets its own comment so it can be discussed independently. **Every comment MUST include the `cc @username` attribution from Step 4.** Do not post comments without attribution.

Use the GitHub API to post individual review comments on the specific lines. Build the JSON payload using Python to avoid encoding issues (see Constraint #9), then post with `gh api --input`:

```python
# build_review.py — construct the review JSON and post it
import json, subprocess, os

pr_head_sha = "<HEAD_SHA>"  # from: gh pr view <PR_NUMBER> --json headRefOid --jq '.headRefOid'

review = {
    "commit_id": pr_head_sha,
    "event": "COMMENT",
    "body": "API review findings — see inline comments for details.",
    "comments": [
        {
            "path": "src/Aspire.Hosting/api/Aspire.Hosting.cs",
            "line": 42,
            "side": "RIGHT",
            "body": "cc @username\n\n⚠️ **[Parameter Design]** `AllowInbound` has 6 optional parameters — consider introducing an options class.\n\nRef: [Parameter Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design)"
        },
        {
            "path": "src/Aspire.Hosting.Azure.Network/api/Aspire.Hosting.Azure.Network.cs",
            "line": 15,
            "side": "RIGHT",
            "body": "cc @username\n\nℹ️ **[Preview Package]** Entirely new package — verify it is shipping as preview (SuppressFinalPackageVersion=true)."
        }
    ]
}

tmpfile = os.path.join(os.environ.get('TEMP', '/tmp'), 'review.json')
with open(tmpfile, 'w', encoding='utf-8') as f:
    json.dump(review, f, ensure_ascii=False)

subprocess.run([
    'gh', 'api', '--method', 'POST',
    '/repos/dotnet/aspire/pulls/<PR_NUMBER>/reviews',
    '--input', tmpfile
], env={**os.environ, 'GH_PAGER': 'cat'})
```

Each comment in the `comments` array becomes a **separate review thread** on the PR, allowing independent discussion.

**Comment format for each finding:**

```
cc @username

[severity emoji] **[Rule Name]** Description of the issue.

Ref: [Guideline link](url)
```

The `cc @username` line MUST be the first line of every comment. This tags the original author of the API so they get notified.

Severity emojis: ❌ Error, ⚠️ Warning, ℹ️ Info

**Note on `side` field**: Always include `"side": "RIGHT"` for comments on added lines (the new file version).

#### Fallback: file-level comments

If a finding applies to an entire new file (e.g., "new package — verify preview status") or the exact line cannot be determined, post an inline comment on line 1 of the file:

```bash
{
  "path": "src/Aspire.Hosting.NewPkg/api/Aspire.Hosting.NewPkg.cs",
  "line": 1,
  "side": "RIGHT",
  "body": "cc @username\n\nℹ️ **[Preview Package]** ..."
}
```

**Note**: Do NOT use `"subject_type": "file"` — the GitHub PR reviews API does not support this field. Always use `"line": 1` with `"side": "RIGHT"` instead.

#### Post a summary comment at the end

After all inline comments are posted, post a single top-level summary comment on the PR:

```bash
GH_PAGER=cat gh pr comment <PR_NUMBER> --repo dotnet/aspire --body "## API Review Summary

**X errors**, **Y warnings**, **Z info** across N files.

Top areas of concern:
- [list key themes]

Each finding is posted as a separate inline comment for discussion."
```

#### Ask before posting

Before posting, show the user the list of comments that will be posted (including author attributions) and ask for confirmation. Do not post without approval.

## Important Constraints

1. **Only review `api/*.cs` files** — these are the source of truth for public API surface
2. **Focus on added/changed lines** — don't review existing APIs that haven't changed
3. **Check both sides of a rename** — if an API was removed and a similar one added, it's likely a rename, not a removal + addition
4. **Be pragmatic** — APIs in preview packages (`SuppressFinalPackageVersion`) get lighter scrutiny
5. **Don't flag auto-generated formatting** — the API tool controls formatting; only review API design
6. **Cross-reference related files** — when questioning visibility, check if the type is used in other Aspire assemblies (which would require it to be public)
7. **Attribute to the right person** — search source code history, not API file history (API files are auto-generated). Use `git blame` on the actual source `.cs` files. Every comment MUST tag the original author with `cc @username` as the first line.
8. **Include `side: "RIGHT"` in all review comments** — the GitHub API requires this for inline comments on added lines
9. **Use Python (not PowerShell) for building JSON payloads** — PowerShell's `ConvertTo-Json | Out-File` mangles multi-byte Unicode characters (emojis like ❌ ⚠️ ℹ️ and em-dashes — become mojibake). Always use Python's `json.dumps(ensure_ascii=False)` with `open(file, 'w', encoding='utf-8')` when constructing JSON for the GitHub API.

## Reference: .NET Framework Design Guidelines

The rules above are grounded in Microsoft's official guidelines at:
- [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/)
- [Naming Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/naming-guidelines)
- [Type Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/type)
- [Member Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/member)
- [Parameter Design](https://learn.microsoft.com/dotnet/standard/design-guidelines/parameter-design)
- [Designing for Extensibility](https://learn.microsoft.com/dotnet/standard/design-guidelines/designing-for-extensibility)
- [.NET Breaking Change Rules](https://learn.microsoft.com/dotnet/core/compatibility/library-change-rules)

## Reference: Aspire-Specific Conventions

These conventions are specific to the Aspire codebase, learned from past API reviews:

| Convention | Example | Notes |
|-----------|---------|-------|
| URI properties named `UriExpression` | `public ReferenceExpression UriExpression` | Not `ServiceUri`, `Endpoint`, `Uri` |
| IServiceProvider property named `Services` | `public IServiceProvider Services` | Not `ServiceProvider` (25 vs 10 usage) |
| Port methods accept nullable | `WithHostPort(int? port)` | Allows random port assignment |
| Enums: None/default = 0 | `None = 0, Append = 1` | Not `Append = 0, None = 1` |
| Builder methods return builder | `IResourceBuilder<T>` return | For fluent chaining |
| Extension static factories → type statics | `ParameterResource.Create()` | Not `ParameterResourceBuilderExtensions.Create()` |
| New packages ship preview | `<SuppressFinalPackageVersion>true` | Until sufficient bake time |
| No Obsolete in preview packages | Just rename/remove directly | Don't add migration overhead |
| Resource types use established namespaces | `Aspire.Hosting.Azure` | Not sub-namespaces per resource |
| `const` over `static readonly` for strings | `public const string Tag = "8.6"` | Unless runtime computation needed |
