# Aspire CLI config sources

**Status:** Configuration acquisition and allowlisted app-file materialization are implemented in the CLI. Content-acquisition customizations remain proposed.

For example developer journeys, see [Microsoft first-party config-source scenarios](cli-config-sources-1p-scenarios.md).

Config sources let organizations provide shared Aspire CLI configuration through a Git-hosted `aspire.config.json`. There are two separate phases:

1. **Acquire configuration:** select a remote source, obtain consent, acquire the file using Git, and merge allowlisted values into the application's `aspire.config.json`. Retain the fetched source and metadata in Aspire home for tracking.
2. **Acquire content:** use the application's saved configuration to acquire skills, templates, and integrations.

This specification focuses on the first phase. Content acquisition is summarized at the end; its detailed schema and behavior are deferred.

The application's `aspire.config.json` is the runtime configuration, including values previously copied from a source. Normal CLI configuration precedence and explicit parent-setting inheritance continue to apply. The source is not an additional runtime configuration provider, and ordinary commands never load the cached source JSON to supply settings.

The `configSource` binding itself is not copied or inherited into a child configuration. Source checks and explicit refresh inspect the nearest local settings file found from the selected project's directory and the global settings file, matching normal configuration loading. Existing local-file discovery determines the nearest file; do not continue to farther ancestors when that file has no binding. Each binding belongs to the file that declares it, and refresh updates that file only. Global and local settings remain separate, with local values taking precedence at runtime.

Configuration sources are supported in modern `aspire.config.json` files, not legacy configurations such as `.aspire/settings.json`. This feature does not add legacy migration or compatibility handling. Existing shared discovery behavior is unchanged; incidental behavior with legacy files is not a supported contract.

Source-driven writes to the app configuration and the user-directory tracking copy happen only during initial materialization by `aspire new` or `aspire init`, or an explicit `aspire update --config-source`. Other commands may check for updates and report their availability, but must not apply them. This restriction does not prohibit explicit local configuration edits or a command's unrelated project bookkeeping.

## Phase 1: Acquire remote configuration

### Select a source

During `aspire new` or `aspire init`, resolve a candidate source:

| Priority | Source |
|---|---|
| 1 | `ASPIRE_INIT_SOURCE_URL`, when explicitly set |
| 2 | `https://msft.ghe.com/coreai/aspire-1p-init`, when the existing Microsoft-employee detector reports an internal Microsoft user |
| 3 | No remote source; continue with standard Aspire behavior |

| Environment variable | Purpose | Default |
|---|---|---|
| `ASPIRE_INIT_SOURCE_URL` | Git repository address or absolute local repository path, not a raw JSON URL | Microsoft repository for detected employees; otherwise none |
| `ASPIRE_INIT_SOURCE_PATH` | Repository-relative configuration file path; only applies when `ASPIRE_INIT_SOURCE_URL` is explicitly set | `aspire.config.json` at the repository root |
| `ASPIRE_INIT_SOURCE_REF` | Branch name or full commit SHA; only applies when `ASPIRE_INIT_SOURCE_URL` is explicitly set | The repository's current default branch |

When `ASPIRE_INIT_SOURCE_URL` is unset, ignore `ASPIRE_INIT_SOURCE_PATH` and `ASPIRE_INIT_SOURCE_REF`, including when employee detection selects the Microsoft repository. Do not validate ignored overrides. The inferred Microsoft source always uses the root `aspire.config.json` and the repository's current default branch. To customize its path or ref, explicitly set `ASPIRE_INIT_SOURCE_URL` to the Microsoft repository URL.

For example:

```powershell
$env:ASPIRE_INIT_SOURCE_URL = 'https://git.acme.example/aspire-init'
$env:ASPIRE_INIT_SOURCE_PATH = 'teams/platform.json'
$env:ASPIRE_INIT_SOURCE_REF = 'main'
aspire new
```

When enabled by an explicit source URL, repository-relative paths use `/` separators on every platform. Reject empty or absolute paths, `.` or `..` segments, empty segments, backslashes, and control characters. Git addresses may use HTTPS, SSH URLs, or SCP-style SSH syntax. Absolute local repository paths are also supported, including paths with spaces; they use the current platform's path syntax and remain machine-specific when saved in a binding. Relative repository paths and `file://` URLs are not supported. Git remote-helper syntax, control characters, and remote addresses with embedded passwords/tokens, query strings, or fragments are not supported.

Reuse `IInternalMicrosoftDetector` directly, independently of telemetry opt-in. An explicit source bypasses employee detection. Detection selects a default, not authorization or consent: Git still authenticates repository access. An inconclusive or failed detection does not select the Microsoft source.

Keep this mechanism separate from Aspire SDK/channel identity and existing package-source flags. Invalid explicit source values produce an error rather than falling back to another source.

#### Select a branch or commit

When `ASPIRE_INIT_SOURCE_URL` is explicitly set, use one `ASPIRE_INIT_SOURCE_REF` value for either a branch name or a full commit SHA. Otherwise ignore this override, even for the inferred Microsoft source.

| Selection | Acquisition and later updates |
|---|---|
| Variable unset | Resolve the repository's current default branch each time |
| Branch name, such as `main`, `config-preview`, or `refs/heads/config-preview` | Resolve that branch's current commit each time |
| Full commit SHA | Read that exact commit; do not follow subsequent branch changes |

When the override applies, an explicitly empty or invalid value is an error. Require full commit IDs for commit selection rather than abbreviated hashes or arbitrary revision expressions. Resolve branch selectors as branch refs, not as tags or CLI options. If the selected branch or commit cannot be acquired, report the failure rather than falling back to the default branch.

Git supports a ref name or a fully spelled object ID as the source of a [fetch refspec](https://git-scm.com/docs/git-fetch). A single logical selector can therefore cover both branches and commits, but acquisition must handle commit IDs explicitly rather than treating them as branch names. Whether a particular commit can be fetched depends on remote availability and server policy; a shallow default-branch clone alone does not guarantee that an older or different-branch commit is available.

### Prompt during project creation

Before acquiring or using the remote configuration, `aspire new` and `aspire init` display the candidate repository, selected file path, branch/commit selector or default-branch behavior, and whether the repository came from the environment or employee detection:

```text
Configuration source: https://msft.ghe.com/coreai/aspire-1p-init
Configuration file: aspire.config.json
Configuration ref: default branch
Source origin: Microsoft employee default

Use configuration from this source?
  Yes
  No, use standard Aspire configuration
```

- **Accept:** acquire and validate the remote file, then materialize allowlisted values and a `configSource` binding in the created or initialized project's `aspire.config.json`.
- **Decline:** continue normally, without Git acquisition or a `configSource` entry.
- **Cancel:** stop without creating the application.

The choice applies to the project being created or initialized, not every project beneath the invoking directory. Existing projects use their saved `configSource` without repeating the prompt. Initialization checks the target directory's `aspire.config.json`; it does not reuse an ancestor's binding. An environment variable or employee detection alone never enables a source.

Initialization selects and materializes the source in the configuration file used by the AppHost. In C# solution mode, a new AppHost template creates `aspire.config.json` inside `<solution>.AppHost`, so that file receives the binding and source values rather than the solution-root configuration. An existing AppHost uses its nearest configuration; the solution-root file remains the target when there is no nearer AppHost configuration. An existing binding in the selected file is reused without renewed consent or reapplying source values, even when the initialization environment proposes another source. A parent binding hidden by the AppHost's own configuration is not reused or changed. File-based and polyglot initialization use the working-directory configuration. Agent setup still uses the solution/workspace root independently of the AppHost configuration.

Whether selected implicitly or with an explicit AppHost file or directory, `aspire update --config-source` finds the nearest local settings file from the selected AppHost directory and refreshes its binding and the global settings file's binding, when present. A solution-root binding is selected only when that file is the nearest local configuration. The declaring configuration does not need to identify the AppHost through `appHost.path`.

An explicit AppHost path must exist as a file or directory. A nonexistent target fails before Git acquisition or changes to local or global settings. An existing directory without an AppHost can still refresh its nearest local and global configuration bindings.

If neither settings file has a saved binding, report that there is no configuration source to update and exit successfully without Git acquisition or configuration changes. Do not enable a source from initialization environment variables or employee detection during refresh.

Other commands, including standalone `aspire agent init` and `aspire add`, do not prompt to enable a source. Without a saved project `configSource`, they ignore the initialization-source environment variables and employee default.

Non-interactive creation requires `--use-config-source true` to accept the candidate or `--use-config-source false` to decline it. When a candidate is available and neither value is supplied, fail with instructions rather than waiting for input or silently treating employee detection as consent. The same option can bypass the source prompt during interactive creation:

```shell
aspire new aspire-starter --use-config-source true
aspire init --use-config-source false
```

### Acquire using Git

Use the user's Git installation and normal credential helpers or SSH authentication. Do not introduce a separate login flow or reuse credentials from employee detection.

When the CLI host supports interactive input, fetch with inherited terminal input and output so Git and credential helpers can display instructions immediately and handle credentials directly. Keep local Git queries captured, including configuration-file contents. Do not copy interactive Git diagnostics into Aspire logs or telemetry; report a failed interactive fetch as an inaccessible repository after Git displays its own diagnostics.

Disable terminal prompts and Git Credential Manager interaction for `--non-interactive`, `ASPIRE_NON_INTERACTIVE`, and CI environments where the host disables input; those invocations need authentication configured beforehand. Other credential helpers must also be configured for unattended use. Keep the existing acquisition timeout and cancellation behavior, including while waiting for credentials.

These settings do not disable SSH launcher prompts. Preserve configured SSH launchers rather than rewriting their commands or options. If a non-interactive fetch blocks waiting for authentication, the 30-second timeout terminates its process tree and reports the timeout. Capture output without truncation, including prompts without a terminating newline, and display the last 64 Ki characters of the combined fetch output on stderr so the user can diagnose and correct authentication. Local query results remain complete. Do not display captured configuration-file contents or copy these diagnostics into exception messages, logs, or telemetry. User cancellation and shutdown cancellation do not display captured output. Advisory timeout output is deferred with the warning until command exit and does not change the foreground command's exit code.

For creation, initialization, and explicit updates, acquire the selected branch or exact commit, using the current default branch only when no ref was selected. Resolve a branch to one commit and read the selected configuration file from that snapshot. Validate it before applying values to the project or publishing a tracking copy. Record the configured selector, source-file version, and resolved commit separately; a branch name is not the immutable commit actually fetched. Do not turn the tracking copy into runtime configuration.

Git acquisition and advisory checks support cancellation and have a 30-second timeout. Cancellation terminates the running Git process tree, with bounded waits for exit and output draining. Captured output is also drained with a bounded wait after normal process exit, so descendants holding output pipes cannot block indefinitely; incomplete capture fails rather than returning successful empty output. Read the Git blob without checking out the repository, reject symlink configuration entries, and validate the JSON object and known Aspire settings before use. Configuration files are limited to 4 MiB and cannot declare another configuration source. Normalization must scale with the input's property count and key lengths, rather than repeatedly scanning sibling keys or shifting root entries for each flat key. Report missing Git, inaccessible repositories, missing files, and invalid configuration distinctly; classify authentication failures separately when diagnostics are captured non-interactively. Do not silently substitute standard configuration after the user accepts a source.

### Materialize an allowlist into the app configuration

Only explicitly allowlisted leaf settings may be copied. The initial allowlist is:

| Setting | Purpose |
|---|---|
| `docs.llmsTxtUrl` | General documentation source URL |
| `docs.api.sitemapUrl` | API documentation sitemap URL |

Use the same root-only normalization for local settings and source files before matching the allowlist, and match keys case-insensitively like other CLI settings. Root-level flat colon-separated keys merge into existing nested sections regardless of casing. Resolve collisions against the complete existing path across those sections, preserving its spelling, preferring an existing non-null nested value, and filling null leaves in their declaring section. Normalize source files before configuration-provider validation; genuine duplicate nested leaves remain validation errors. Nested colon keys remain literal, including profile names, environment-variable names, and partially flattened documentation keys such as `{"docs":{"api:sitemapUrl":"..."}}`. Those partially flattened keys are not expanded or matched to the source allowlist. There is no source-specific normalization.

Resolve each allowed leaf across all case-equivalent sections, so `DOCS.llmsTxtUrl` and `docs.api.sitemapUrl` can both supply values. When updating an existing leaf, write to its declaring section rather than adding a duplicate path under a different casing. Reject a merge that would introduce duplicate configuration paths, leaving the saved file unchanged rather than rewriting literal keys. Allowing a leaf does not allow its entire parent object. Everything else is excluded unless a later specification change explicitly adds it to this list.

In particular, do not copy `$schema`, `configSource`, AppHost identity or paths, SDK/channel selection, language, package references, launch profiles, or arbitrary feature/content settings. The CLI creates and maintains its own source binding; the remote file cannot supply or replace it. The proposed content schema does not implicitly extend this allowlist.

Merge the allowed values into the selected application's file, not a configuration file in the CLI's launch directory when a different app is selected:

- On initial creation or initialization, combine generated project settings with the supplied allowlisted source values and save the result in the application's `aspire.config.json`.
- On explicit update, a supplied allowlisted value replaces the existing app value at that leaf, including a locally edited value. The explicit update/confirmation is the authorization to apply these changes.
- Preserve unrelated app settings and allowlisted keys omitted by the new source. Omission does not delete a previously materialized value.
- An explicit null is a supplied value and replaces the existing value where valid for that setting. Future allowlisted arrays replace the whole value rather than appending.
- Do not merge a runtime view of global settings into the app file. Global settings remain separate.

For example, after applying a source that supplies documentation URLs:

```json
{
  "configSource": {
    "repository": "https://git.acme.example/aspire-init",
    "path": "teams/platform.json",
    "ref": "main",
    "appliedVersion": "<source-file-version>"
  },
  "docs": {
    "llmsTxtUrl": "https://docs.acme.example/llms-full.txt",
    "api": {
      "sitemapUrl": "https://docs.acme.example/sitemap.xml"
    }
  }
}
```

This is an excerpt; other generated or existing app settings remain in the same file. Once saved, these documentation settings are ordinary project settings. Editing them locally changes subsequent command behavior without editing the source or its tracking copy. A later explicit source update may overwrite them according to the rules above.

Stage and validate an update before replacing the project file. Write the settings and their applied version together so the file cannot claim a version whose values were not saved. If neither the supplied settings nor the saved binding change, report that the app configuration is already up to date and leave the project file untouched. Acquisition, validation, or project-write failure must be reported, without claiming a successful update or leaving a partially written app file.

Success notices identify the destination settings file, so local and global refresh results can be distinguished.

### Store a tracking copy in Aspire home

Retain fetched configuration centrally using the resolved Aspire home, including `ASPIRE_HOME` overrides:

```text
<AspireHome>\configsources\<source-key>\
  cache.lock
  check.json
  current
  <snapshot-id>\
    aspire.config.json
    metadata.json
    repository.git\
```

The usual home is `<user-profile>\.aspire`. The source key distinguishes repository, upstream file path, and configured ref (or an explicit default-branch marker when omitted). Different branch/commit selections have separate tracking and check records, even when they currently resolve to the same commit. The retained filename remains `aspire.config.json`, even for another upstream filename.

The retained JSON and `metadata.json` describe the last successfully fetched source: its identity, selected-file version, resolved commit, and fetch time. They are tracking data, not defaults for command execution. Only initial materialization and explicit config-source updates publish a new fetched snapshot or replace `current`; normal commands must not read this JSON to configure themselves or rewrite it while checking for updates. After successful publication, remove older snapshots under the source cache lock, retaining only the latest snapshot. Failed acquisition leaves the previous snapshot intact; cleanup failures are logged without failing the command.

Keep advisory check state separate in `check.json`: the last successful check time and last observed source-file version. A check is not a fetch-and-apply operation, and must not advance the fetched snapshot's timestamp or a project's applied version. Tracking records must distinguish an actually fetched source from one whose version was only checked.

Use locks and atomic publication to prevent partially written tracking records. Retain user-scoped permissions for private content. Never store credentials in configuration or metadata, log them, or send private source details in ordinary telemetry.

Publish fetched tracking data before changing the app file. A tracking-publication failure leaves the app unchanged. If a later app read or write fails, report the operation as failed: tracking can record the successfully fetched source, but the app's settings and applied version remain unchanged. Apply settings and their version together using an atomic sibling-file replacement under the workspace configuration lock, re-reading the app file after acquisition to preserve concurrent edits. Create the temporary file with owner-only Unix permissions or the existing destination's Windows access rules. Preserve the destination's Unix mode or Windows DACL when replacing it; new Unix configuration files remain owner-only.

Global source refresh uses the same path-based configuration lock as local refresh. Both local and global `aspire config set` and `aspire config delete` operations acquire that lock so they cannot overwrite a refresh with a stale read or lose an unrelated edit to the refresh.

A tracking copy is shared by source identity, not a record of what every application has applied. An ordinary command must work from its app file even when this directory is missing, another app refreshed the same source, or the retained JSON is unreadable. Problems with the tracking/version check are reported separately; they do not make cached values a fallback runtime provider.

Missing `configSource.repository` or `configSource.path` must not prevent general configuration loading or discard the app's saved settings. Validate these fields when the binding is used: an advisory check reports the invalid binding without failing the project operation, while an explicit configuration-source refresh fails before Git acquisition or application-file changes.

### Check for updates without applying them

Operations that resolve an Aspire project or select a running AppHost check saved sources in the nearest local settings file and the global settings file for newer versions. Initialization of an already-bound project uses the same discovery from the resolved project directory. Commands that do not resolve a project, such as agent setup, configuration inspection, and documentation lookup, do not perform an ambient source check. Agent setup targets an agent workspace, which is not necessarily the AppHost directory. The check is advisory: the requested operation uses saved configuration whether a source is newer, unavailable, or has never been fetched on this machine.

Advisory checks run in the background without delaying project operations or prompting for Git credentials. At command exit, unfinished checks are cancelled and their child processes are cleaned up. Completed update notices and advisory warnings are displayed after the CLI version update notification, never concurrently with command output; machine-readable invocations suppress these notices. A fast command may exit before a check produces a result, in which case no notice is shown. Explicit `aspire update --config-source` remains a synchronous fetch-and-apply operation, including its normal credential prompts and failure reporting.

Each configuration-source service instance handles one project's nearest local and global sources. Repeated calls for the same project reuse a single background check; attempting to reuse the instance for another project is an error. Sources are processed serially, local before global, never in parallel, so Git terminal output and authentication input cannot compete between sources. If the local and global paths identify the same file, process it only once. Bindings with identical repository, file path, and ref share one version check or acquisition per invocation, regardless of their applied versions. Compare the shared observation against each binding's applied version; reuse cached check metadata only when it is valid for all those versions.

Directory cleanup failures are logged at debug level without displaying a UI warning or replacing the check's notification.

| Command scope | Source behavior |
|---|---|
| `aspire new` / `aspire init` initially creating source-bound app configuration | After consent, fetch, validate, and materialize allowed values; save the binding, applied version, and tracking copy |
| `aspire update --config-source` | Fetch and explicitly apply allowed values to the nearest local and global configurations with saved bindings, regardless of check age |
| Other commands scoped to an existing source-bound app, including re-running initialization on an already-bound app | Check for updates when due; never apply source values or rewrite the fetched tracking copy |
| Commands scoped to an app without a `configSource` in either the nearest local or global configuration | No source check or acquisition |
| Global commands not scoped to an app, help, and completion | No source check or acquisition |

Compare the selected-file version at the binding's branch or commit with the application's `configSource.appliedVersion`, not with another app's latest fetch. The version identifies the selected file's contents, such as its Git blob identity; unrelated commits to other files do not constitute a configuration update.

For a branch selection, checks follow that branch, not the repository's default branch. A commit-pinned source remains at the saved commit: newer commits on any branch do not produce an availability notice, and an explicit update must not move the pin.

Checks may fetch transient Git objects into a private temporary directory to inspect the selected file's blob identity. They do not extract its JSON, retain a new fetched snapshot, or load source settings.

Preserve the 24-hour check cadence, measured from the last successful version check:

| Tracking/check state | Ordinary-command behavior |
|---|---|
| Missing check state | Check the source's current version; do not acquire/apply a configuration snapshot |
| Last successful check less than 24 hours ago | Reuse the observed version from check metadata, not settings from the retained JSON |
| Last successful check at least 24 hours ago | Check the source's current version again |
| Observed version differs from the app's applied version | Report that an update is available |
| Version check fails | Report the check failure and continue the requested operation using the app's saved settings |

When reusing a cached mismatch, record every applied version compared against the shared observation, including differing versions in local and global bindings. Repeated commands can then reuse that observation for both bindings within the check interval. A cloned app can already contain a version newer than the cached observation; revalidate an unfamiliar applied version before advertising an update. Older check metadata without the compared-version set is revalidated before reusing a mismatch. A cached observation equal to the app's applied version can be reused directly.

If an update is available, emit a notice such as:

```text
A new version of the configuration source is available.
Run `aspire update --config-source` to apply it.
```

Emit distinct availability notices and check failures once per invocation on stderr, preserving machine-readable stdout. A failure must remain visible even if an earlier source produced an update notice. Stop checking further sources after a failure, but do not fail the requested project operation. Report authentication, network, missing-file, and tracking errors explicitly and safely; do not report a failed check as "up to date" or advance its successful-check time. Advisory failure does not change the requested operation's exit code; its own success/failure still determines that code.

A known newer version can be reported on later invocations within the check interval without another network check. No background timer applies settings. In particular, offline or unauthorized checks must not cause the CLI to substitute standard configuration, load cached defaults, or discard the app's saved values.

### Explicitly update the app configuration

Use `aspire update --config-source` to fetch the source at the saved branch or commit selection and merge allowed values into the app's `aspire.config.json`:

```shell
aspire update --config-source
aspire update --config-source --apphost MyApp.AppHost.csproj
```

This is a configuration-update mode of `aspire update`, separate from its package-update workflow:

- Resolve the project using the existing project-selection behavior, including `--apphost`, and process saved bindings in its nearest local settings file and the global settings file, local first. Do not select a source from environment variables or employee detection. If neither configuration has a saved source, report an error and exit nonzero without Git acquisition.
- When `--apphost` names a directory, discover and select an AppHost within that directory before considering a configuration-only fallback in the requested directory. If discovery finds no AppHost, use the nearest local settings file from that fallback directory plus global settings; discovery ambiguity or unsupported-project errors must not silently select another binding.
- Acquire the saved branch's current commit, the saved exact commit, or the repository's current default branch when the binding has no ref, even when a recent check found no update. Validate it, merge only supplied allowlisted values into each declaring configuration file, and save its applied version with those values. Reuse one acquisition for identical repository/path/ref bindings, preserving each file's unrelated settings independently.
- Never process sources in parallel. On the first acquisition, validation, or write failure, stop and exit nonzero; preserve earlier successful updates without rolling them back.
- Publish the fetched source JSON and fetch metadata in Aspire home as tracking data. Record a successful fetch/check time even if the selected file's version is unchanged. If tracking publication fails, report it explicitly; do not use shared tracking as proof that an app was updated.
- Report the source, resolved commit, and whether the app configuration changed. Make clear that supplied allowlisted values can replace local edits.
- Do not update the CLI, SDK, or packages, change the binding's repository, path, or ref, regenerate AppHost source or application code, or reinstall content. The app configuration file and its applied-version stamp are intentionally updated. Reject combinations with `--self`, `--migrate`, or channel/package-source overrides. Plain `aspire update` retains its existing package-update behavior and only performs an advisory source check.
- Follow `aspire update`'s existing non-interactive confirmation contract: automation uses `aspire update --config-source --yes --non-interactive`. The saved binding supplies source consent; this command does not establish a new binding or prompt to enable one.

Failed or cancelled acquisition or validation must not replace the app settings, applied version, or last valid fetched tracking copy, and must not report success. Unlike an advisory check failure, failure of this explicit update is an operation failure with a nonzero exit code.

A successful update to one app must not change another app's file or make it appear updated. The other app keeps its own materialized settings and applied version until explicitly updated.

### Persist source identity and the app's applied version

The app file records the remote identity and the selected-file version actually applied, never a machine-specific tracking path:

```json
{
  "configSource": {
    "repository": "https://git.acme.example/aspire-init",
    "path": "teams/platform.json",
    "ref": "main",
    "appliedVersion": "<source-file-version>"
  }
}
```

Persist the selected file path even when it is the default. Persist `ref` when a branch or commit was explicitly selected; omit it to keep following the repository's current default branch. Do not replace a branch selector with its resolved commit: branch tracking and commit pinning are intentionally different. Bindings that already omit `ref` retain default-branch behavior.

Commit the materialized settings and binding with the app. A teammate uses the same saved settings immediately, without fetching configuration to supply runtime values, and uses their own Git access for version checks or explicit updates.

Later commands resolve source identity from this binding rather than repeating employee detection or using the current initialization environment variables. `appliedVersion` changes only when source values are successfully materialized in that app, not when a version check or another app's update succeeds.

A pre-existing binding without an applied version must not trigger automatic materialization. Report that `aspire update --config-source` is needed to establish the applied version and saved values. Do not infer that app's state from the user-directory tracking copy.

### Validation

Cover source precedence and telemetry-independent detection; default/custom file paths, validation, consent, authentication modes, and failure handling. Verify that initial creation/initialization saves allowlisted values, binding identity, and applied version in the correct app file, while excluded settings are never copied.

Cover the renamed environment variables and default/explicit ref selection when the source URL is explicit. Verify that path/ref overrides, including invalid values, are ignored without an explicit URL, both when Microsoft employee detection selects a source and when no source is selected. The inferred source must retain the root path and default-branch behavior. Explicitly setting the Microsoft URL enables both overrides.

Verify non-default and slash-containing branch names, full commit IDs, acquisition of a commit absent from a shallow default-branch snapshot, and explicit errors for invalid/missing refs or server-denied commits without fallback. Verify persisted ref portability, separate tracking for different selectors, branch movement, default-branch changes, and commit pins remaining unchanged during checks and explicit updates. Initialization environment variables must not retarget a saved binding.

Verify overwrite behavior for supplied allowlisted values, preservation of omitted and unrelated settings, valid null replacement, and no accidental copying of a parent section or normalized-key bypass. Verify atomic app-file updates and unchanged app/fetch state after failed or cancelled acquisition.

For ordinary commands, verify the exact 24-hour check boundary, cached availability notices, file-version comparison instead of unrelated repository commits, and actionable stderr output. Confirm no source-driven app writes, fetched-JSON writes, or loading cached JSON as configuration. Cover missing/corrupt tracking, unavailable Git/authentication/network, and continued command execution with saved app values after a reported check failure.

For explicit updates, cover fresh/missing tracking, unchanged source versions, `--apphost` selection, non-interactive confirmation, incompatible options, missing bindings, and bindings without an applied version. Verify that updating one app does not alter another app's settings or applied-version comparison, including on another machine with no tracking copy. Confirm no CLI/SDK/package updates, code regeneration, or content reinstallation.

Cover nearest-local and global bindings independently and together, including no local file, an unbound nearest modern file that hides a farther ancestor binding, a nonstandard global file path, and local/global paths identifying the same file. Verify serial acquisition, shared-source deduplication and cache freshness across differing applied versions, preservation of unrelated files, and failure/cancellation after an earlier successful refresh.

Keep no-source, declined-source, global-command, help, and completion paths free of source acquisition/checks. Verify plain `aspire update` does not implicitly materialize configuration-source changes.

## Phase 2: Acquire content from configuration

Skills, templates, and integrations are acquired according to the application's saved configuration and normal CLI precedence. These are separate operations from fetching or materializing a configuration source, and must not load source settings from the user-directory tracking copy.

| Content | Acquisition today | Possible configuration customizations |
|---|---|---|
| Skills | `aspire agent init` installs the embedded Aspire bundle by default, with existing optional remote-bundle support | Additional validated bundles, selected/default skills, and workspace installation destinations |
| Templates | .NET templates are installed from NuGet; CLI-native starters use embedded files or scaffolding | Template catalogs, package identities, versions, and feeds; external starter content could follow later |
| Integrations | Templates declare dependencies; `aspire add` and integration list/search discover NuGet packages | Additional catalog entries, package versions, feeds, and package-source mappings |

Keep standard content available initially and preserve explicit user selections and suppression options. Future source-driven content settings must be explicitly added to the allowlist and materialized before they affect acquisition choices. An availability notice never changes those choices. An explicit configuration update does not automatically regenerate applications or reinstall skills. Git access does not grant access to private package feeds, and all acquired content must retain its existing validation and authentication requirements.
