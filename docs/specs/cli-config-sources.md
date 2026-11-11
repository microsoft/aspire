# Aspire CLI config sources

**Status:** Configuration acquisition, allowlisted app-file materialization, named agent skill sources, runtime named template-source inheritance, and named integration sources are implemented in the CLI. Other content-acquisition customizations remain proposed.

For example developer journeys, see [Microsoft first-party config-source scenarios](cli-config-sources-1p-scenarios.md).

**Configuration sources provide optional defaults, not policy enforcement.** A source
configured on a machine, including through organization-managed environment variables,
or proposed by employee detection is a suggestion, not a requirement for new applications.
Developers may decline it, choose another source, or edit the resulting application
configuration. Opting out or overriding these defaults is supported, not a security
bypass. This feature does not let administrators restrict which configuration,
templates, or packages developers may use, and it does not establish a security boundary.

Config sources let organizations provide shared Aspire CLI configuration through a Git-hosted `aspire.config.json`. There are two separate phases:

1. **Acquire configuration:** select a remote source, obtain consent, acquire the file using Git, and merge allowlisted values into the application's `aspire.config.json`. Retain the fetched source and metadata in Aspire home for tracking.
2. **Acquire content:** use the application's saved configuration to acquire skills, templates, and integrations.

This specification covers the first phase, named agent skill sources, and runtime template-source inheritance. Other content acquisition is summarized at the end; its detailed schema and behavior are deferred.

The application's `aspire.config.json` is the runtime configuration, including values previously copied from a source. Normal CLI configuration precedence and explicit parent-setting inheritance continue to apply. The source is not an additional general-purpose runtime configuration provider. Named template sources have a separate, runtime-only inheritance path described below.

The `configSource` binding itself is not copied or inherited into a child configuration. Source checks and explicit refresh inspect only the nearest local settings file found from the selected project's directory. Existing local-file discovery determines the nearest file; do not continue to farther ancestors when that file has no binding. Each binding belongs to the file that declares it, and refresh updates that file only. Global configuration does not support `configSource`: adding a binding there has no effect, even when the binding is invalid. Other global settings retain their normal runtime precedence.

Configuration sources are supported in project-local modern `aspire.config.json` files, not global configuration or legacy configurations such as `.aspire/settings.json`. This feature does not add legacy migration or compatibility handling. Existing shared discovery behavior is unchanged; incidental behavior with legacy files is not a supported contract.

Source-driven writes to app configuration happen only during initial materialization by `aspire new` or `aspire init`, consented adoption by `aspire update`, or an explicit `aspire update --config-source`. Configuration sources never refresh global configuration. Runtime template discovery and consented template-source listing can also acquire configuration and publish the user-directory tracking snapshot, but use only the `templateSources` overlay without saving inherited values into app or global configuration. Other commands may check for updates and report their availability, but must not apply them. This restriction does not prohibit explicit local configuration edits or a command's unrelated project bookkeeping.

## Phase 1: Acquire remote configuration

### Select a source

During `aspire new`, `aspire init`, or adoption by `aspire update`, resolve a candidate source:

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
| `ASPIRE_INIT_SOURCE_README_URL` | Absolute HTTP or HTTPS URL explaining the proposed configuration source | `https://msft.ghe.com/coreai/aspire-1p-init/blob/HEAD/README.md` for the Microsoft source; otherwise none |

When `ASPIRE_INIT_SOURCE_URL` is unset, ignore `ASPIRE_INIT_SOURCE_PATH` and `ASPIRE_INIT_SOURCE_REF`, including when employee detection selects the Microsoft repository. Do not validate ignored overrides. The inferred Microsoft source always uses the root `aspire.config.json` and the repository's current default branch. To customize its path or ref, explicitly set `ASPIRE_INIT_SOURCE_URL` to the Microsoft repository URL.

For example:

```powershell
$env:ASPIRE_INIT_SOURCE_URL = 'https://git.acme.example/aspire-init'
$env:ASPIRE_INIT_SOURCE_PATH = 'teams/platform.json'
$env:ASPIRE_INIT_SOURCE_REF = 'main'
$env:ASPIRE_INIT_SOURCE_README_URL = 'https://git.acme.example/aspire-init/blob/main/README.md'
aspire new
```

The README URL override applies to both explicit and employee-detected candidates.
It is used when presenting the source for consent and when displaying access help
after an acquisition error. It is not fetched by the CLI
and is not saved in the application's configuration or source cache. An explicitly
empty value, a non-HTTP(S) URL, or a value containing control characters is an error
when a candidate is available. Without a candidate, the override is ignored.

If acquisition fails and a valid README URL is available, retain the existing
error message and append README access help on a new line in that same error.
Display only one error message. This also applies when explicitly refreshing a saved source
with `aspire update --config-source`. Invalid README overrides do not replace an
acquisition error. Background advisory checks retain their existing warning behavior.

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

Before acquiring or using the remote configuration, `aspire new` and `aspire init` display the candidate repository as **Git source** and whether it came from the environment or employee detection as **Source origin**. The file path is shown only when it differs from the default `aspire.config.json`, and the branch/commit selector is shown as **Git ref** only when explicitly specified:

```text
A configuration source was detected on your machine. It lets an organization share Aspire settings to customize your development experience.

      Git source:  https://msft.ghe.com/coreai/aspire-1p-init
   Source origin:  Microsoft employee default

Read more about this configuration source

Only use sources you trust. Content from these sources can run code on your machine.

Use configuration from this source? [Y/n]:
```

The explanation precedes the source details, which use an aligned two-column
Spectre.Console grid with bold green labels and plain-text values, matching the
AppHost and dashboard summaries. When a README URL is available,
**Read more about this configuration source** is a Spectre.Console hyperlink.
Terminals without hyperlink support show the label and URL as plain text.
The standard yes/no confirmation defaults to **Yes** when Enter is pressed.

The source trust warning appears at most once per CLI command, shared across
configuration, template, integration, and skill sources. Source explanations,
inventories, and consent prompts still appear where applicable. A separate
command can show the warning again.

Before Git acquisition begins, the CLI writes an informational notice to stderr:
**Fetching configuration source from https://example.test/team/configuration.git using Git. Git may prompt you to authenticate.**
Non-interactive acquisitions show
**Fetching configuration source from https://example.test/team/configuration.git using Git...**
instead, because authentication prompts are disabled. Configuration and template
Git acquisition run without animated status indicators so Git and SSH
authentication prompts remain visible. Repository URLs are redacted before display so embedded
credentials and query tokens are not exposed. Reusing an in-memory snapshot
does not repeat the notice, and background version checks remain quiet.
Repository templates identify the repository in these notices, for example
**Fetching templates from https://example.test/team/templates.git using Git. Git may prompt you to authenticate.**
Non-default paths and refs are included to distinguish sources within the same
repository. Show this identity before Git can ask for credentials so users can
check the destination before entering a basic-authentication password.

- **Accept:** acquire and validate the remote file, then materialize allowlisted values and a `configSource` binding in the created or initialized project's `aspire.config.json`.
- **Decline:** continue normally without Git acquisition or template-source inheritance from that source for the rest of the command, and save `"configSource": { "optOut": true }` after successful project creation or initialization. Independently configured global template sources remain available.
- **Cancel:** stop without creating the application.

The choice applies to the project being created or initialized, not every project beneath the invoking directory. Existing projects use their saved `configSource` or opt-out without repeating the prompt. An opt-out does not require repository, path, ref, or applied-version fields, and source checks and refresh skip that file without falling back to ancestor or global bindings. Initialization checks the target directory's `aspire.config.json`; it does not reuse an ancestor's binding. An environment variable or employee detection alone never enables a source.

Initialization selects and materializes the source in the configuration file used by the AppHost. In C# solution mode, a new AppHost template creates `aspire.config.json` inside `<solution>.AppHost`, so that file receives the binding and source values rather than the solution-root configuration. An existing AppHost uses its nearest configuration; the solution-root file remains the target when there is no nearer AppHost configuration. An existing binding in the selected file is reused without renewed consent or reapplying source values, even when the initialization environment proposes another source. A parent binding hidden by the AppHost's own configuration is not reused or changed. File-based and polyglot initialization use the working-directory configuration. Agent setup still uses the solution/workspace root independently of the AppHost configuration.

Whether selected implicitly or with an explicit AppHost file or directory, `aspire update --config-source` finds the nearest local settings file from the selected AppHost directory and refreshes its binding, when present. A solution-root binding is selected only when that file is the nearest local configuration. The declaring configuration does not need to identify the AppHost through `appHost.path`. Global bindings are ignored.

An explicit AppHost path must exist as a file or directory. A nonexistent target fails before Git acquisition or changes to local settings. An existing directory without an AppHost can still refresh its nearest local configuration binding.

If the nearest local settings file has no saved binding, report that there is no configuration source to update and exit successfully without Git acquisition or configuration changes, even when global configuration contains a binding. Do not enable a source from initialization environment variables or employee detection during refresh.

Other project commands, including standalone `aspire agent init` and `aspire add`, do not prompt to enable a source. Without a saved project `configSource`, they ignore the initialization-source environment variables and employee default. Template search and source listing have the separate runtime-only inheritance contract below.

Non-interactive creation requires `--use-config-source` to accept the candidate or `--use-config-source false` to decline it. The bare flag means `true`; an explicit `--use-config-source true` is also accepted for compatibility. Omitting the option retains unspecified consent. When a candidate is available and the option is omitted, fail with instructions rather than waiting for input or silently treating employee detection as consent. The same option can bypass the source prompt during interactive creation:

```shell
aspire new aspire-starter --use-config-source
aspire init --use-config-source false
```

### Adopt a source in an existing project

Plain `aspire update` also offers the candidate source when the selected AppHost's nearest local configuration has neither a `configSource` binding nor an explicit opt-out. It uses the same environment-variable and employee-detection precedence and the same consent prompt as project creation. If no candidate is available, nothing is saved.

Accepting acquires and validates the source, then merges allowlisted settings and a binding with its applied version into that nearest configuration file. If no local configuration exists, create `aspire.config.json` beside the AppHost. Declining saves only the opt-out alongside existing settings:

```json
{
  "configSource": {
    "optOut": true
  }
}
```

Both choices prevent subsequent adoption prompts. Saved bindings are not retargeted or reapplied by plain `aspire update`; their existing advisory checks remain unchanged. Remove the `configSource` entry to allow a later adoption prompt. CLI self-update and repository-tool-only updates do not offer project configuration sources.

Unattended package updates do not introduce a new consent requirement: without `--use-config-source`, skip adoption entirely, without recording an opt-out. `--yes` alone does not consent to source adoption. To accept or decline explicitly:

```shell
aspire update --yes --non-interactive --use-config-source
aspire update --yes --non-interactive --use-config-source false
```

These options affect only initial adoption; an existing binding or opt-out remains authoritative. Adoption failure leaves the existing configuration unchanged and fails the command rather than silently continuing with another source. `aspire update --config-source` remains a refresh-only operation and never discovers or adopts a source.

### Source trust

Only use sources you trust. Review the configuration and the template repositories,
package feeds, and documentation it references before accepting it. Source content
can run code on your machine, and documentation can influence AI agents. The CLI's
correctness checks do not establish that a source or its content is trustworthy.

Optional defaults do not remove the need to trust a source that is used. Explicit source
settings must still be valid, and failures acquiring a selected source are reported
rather than silently falling back to other defaults.

The CLI uses one shared localized warning when offering a configuration source in
`aspire new` or `aspire init`, refreshing it with `aspire update --config-source`,
or acquiring configuration for runtime template inheritance. Explicit consent flags
do not suppress the warning. Repeated configuration acquisition in the same command
does not repeat it, and background update checks remain silent until their ordinary
advisory notification.

The same warning is used for Git-backed templates, `aspire template source add`,
and explicit package sources supplied through `aspire new --source` or
`aspire add --source`. Warnings go to stderr to preserve machine-readable stdout.
They do not introduce new confirmation prompts or change existing automation flags.

Configuration, template, skill, and integration sources use aligned detail blocks
with bold-green labels and literal values. Each source is separated by one blank
line. Template and integration pickers use source-name groups only when more than
one source supplies choices. Directly before those pickers, the shared trust warning
is shown if templates are not all built-in or integrations come from a feed other
than nuget.org.

### Acquire using Git

Use the user's Git installation and normal credential helpers or SSH authentication. Do not introduce a separate login flow or reuse credentials from employee detection.

Configuration sources and repository templates share [GitClient](../../src/Aspire.Cli/Git/GitClient.cs) for repository initialization, shallow fetches, commit identification, and Git object access. [GitCommandRunner](../../src/Aspire.Cli/Git/GitCommandRunner.cs) owns process capture, environment isolation, failure classification, and lifecycle diagnostics. It reuses the existing tree-aware process-capture infrastructure and records all Git stdout and stderr in the CLI log at debug level, including output from successful, failed, and canceled operations. Output is logged as it is read, including partial lines, so authentication diagnostics remain available when a command stalls. Command arguments and exception details remain excluded from process diagnostics. Git output can include private repository information, configuration-file contents, or credentials emitted by Git or its helpers; review logs before sharing them. Callers retain their operation-wide timeout budgets and explicitly select interaction policy. Configuration sources use bare repositories and validate blobs without checkout; templates retain their ref-resolution rules, detached checkouts, and immutable cache publication. Template acquisition continues disabling terminal prompts without changing its existing credential-helper policy.

[ConfigSourceService](../../src/Aspire.Cli/Configuration/ConfigSourceService.cs) owns configuration acquisition and version-check policy directly: each operation uses the five-minute timeout shared with Git-backed templates, versions are blob IDs, and acquisition validates Git blobs and configuration content before publishing a snapshot. The overall budget allows time for browser sign-in, MFA, and other credential-helper interaction because Git does not expose a portable signal distinguishing authentication waits from other work.

Resolve the configured selector before initializing the bare acquisition repository, and explicitly select SHA-1 or SHA-256 from the resolved commit ID. This also supports SHA-256 repositories selected by branch or default ref, rather than only by a pinned commit. Ref resolution shares the operation's timeout and interactive or unattended authentication policy. Interactive ref resolution captures the advertised refs on stdout and forwards stderr to the terminal as it is read so Git authentication instructions remain visible immediately.

When the CLI host supports interactive input, fetch with inherited terminal input and forward stdout and stderr unchanged as they are read so Git and credential helpers can display instructions immediately and handle credentials directly. Forward partial prompts without adding newlines. Keep local Git query results captured rather than displaying them, including configuration-file contents. Both captured and forwarded output are recorded in the CLI log, but not copied into exception messages or telemetry. Classify failed interactive operations using the captured stderr.

Repository initialization, shallow fetches, and detached checkouts use Git's supported `--quiet` option to suppress routine success/progress output without suppressing authentication prompts or diagnostics. Ref and blob queries retain complete stdout capture for parsing. Explicit operations display captured stderr on the error console on both success and failure, but parsed stdout is never copied there. Background version checks defer all captured stderr, including successful-command warnings, through the ordinary advisory notification path until command exit; machine-readable invocations suppress those notifications and diagnostics together without suppressing file logging. Interactive remote stderr is forwarded immediately and is not displayed again after completion. Quiet acquisition does not change source consent, credential-helper behavior, or the unattended authentication and cancellation policies below.

Unattended configuration-source Git operations (including opted-in JSON listing, non-interactive `new`/`init`, and advisory checks) override `core.askPass` only for the child process and disable `GIT_ASKPASS`, `SSH_ASKPASS`, terminal prompts and Git Credential Manager interaction. Standard input receives EOF. This also applies when `--non-interactive`, `ASPIRE_NON_INTERACTIVE`, or CI disables host input. Credential helpers remain enabled for cached credentials; custom credential helpers must themselves run without prompting. Interactive Git acquisition and captured template-repository acquisition retain their existing authentication behavior.

Unattended SSH resolves the selected executable through Git's effective child `PATH`, including Git for Windows' bundled executable directories, rather than substituting an OpenSSH client found on the CLI's own `PATH`. It verifies that executable and supplies `BatchMode=yes`, preserving configured identity files, known hosts and agent authentication. The version probe allows slow process startup within a 30-second limit and honors the enclosing operation's cancellation budget. Unknown host keys and unavailable credentials fail rather than prompt. Bare OpenSSH executable selections through `GIT_SSH`, `GIT_SSH_COMMAND` or `core.sshCommand` are supported, but custom command strings with arguments, shell expansions or wrappers fail with an actionable diagnostic instead of being silently overwritten or receiving potentially incompatible OpenSSH options. Move identity/host options into OpenSSH configuration or use interactive acquisition for those commands. Custom credential helpers, SSH agents and proxy commands are not sandboxed and must already be unattended. Git URL rewrites are resolved before selecting this SSH policy, so HTTPS selectors rewritten to SSH receive the same protection. No user Git configuration is modified.

If a non-interactive fetch blocks waiting for a custom authentication helper, the five-minute timeout terminates its process tree and reports the timeout. Capture output without truncation, including prompts without a terminating newline, and display the last 64 Ki characters of the combined fetch output on stderr so the user can diagnose and correct authentication. Local query results remain complete. Do not display captured configuration-file contents or copy diagnostics into exception messages or telemetry. User cancellation and shutdown cancellation do not display captured output, but output already read remains in the CLI log. Advisory timeout output is deferred with the warning until command exit and does not change the foreground command's exit code.

For creation, initialization, and explicit updates, acquire the selected branch or exact commit, using the current default branch only when no ref was selected. Resolve a branch to one commit and read the selected configuration file from that snapshot. Validate it before applying values to the project or publishing a tracking copy. Record the configured selector, source-file version, and resolved commit separately; a branch name is not the immutable commit actually fetched. Do not turn the tracking copy into runtime configuration.

Git acquisition and advisory checks support cancellation and have a five-minute timeout. Cancellation terminates the running Git process tree, with bounded waits for exit and output draining. Captured output is also drained with a bounded wait after normal process exit, so descendants holding output pipes cannot block indefinitely. That bound only gives up once the CLI can promptly schedule the reads, so a saturated thread pool does not discard complete output from a process that already exited; that extension still ends at the operation timeout or cancellation, which is reported as such. Incomplete capture fails rather than returning successful empty output. Read the Git blob without checking out the repository or interpreting file modes, and validate the JSON object and known Aspire settings before use. Symlink blobs contain their target path as text; they are read as content, not followed, and must pass the same JSON validation as other blobs. There is no configuration-file size limit. These checks ensure correctness, not security enforcement. Reject declarations of another configuration source to reserve recursive sources for future support, rather than silently activating previously ignored declarations after an upgrade. Normalization must scale with the input's property count and key lengths, rather than repeatedly scanning sibling keys or shifting root entries for each flat key. Report missing Git, inaccessible repositories, missing files, and invalid configuration distinctly; classify authentication failures separately when diagnostics are captured non-interactively. Do not silently substitute standard configuration after the user accepts a source.

Once the thread pool responds promptly, allow one additional two-second drain for pending reads to finish reaching EOF. A responsive probe can finish before earlier reads complete; it is not evidence that a descendant is holding the pipe open. The recovery drain still observes the operation timeout and cancellation, and gives up when its bound expires.

### Materialize an allowlist into the app configuration

Only explicitly allowlisted top-level settings may be copied. Each supplied section is copied in full, including all nested properties. The allowlist is:

| Setting | Purpose |
|---|---|
| `skillSources` | Named agent skill sources and all their settings |
| `integrationSources` | Named NuGet integration feeds and allowed package patterns |

Use the same root-only normalization for local settings and source files before matching the allowlist, and match top-level names case-insensitively like other CLI settings. Root-level flat colon-separated keys merge into existing nested sections regardless of casing. Normalize source files before configuration-provider validation; genuine duplicate nested leaves remain validation errors. Nested colon keys remain literal, including profile names, environment-variable names, and custom skill-source keys such as `{"skillSources":{"team":{"custom:key":"..."}}}`. These keys are copied unchanged when their top-level section is allowed. There is no source-specific normalization.

Replace every local case-equivalent section when an allowed top-level setting is supplied. Preserve all source sections, including disjoint `SKILLSOURCES` and `skillSources` objects that `IConfiguration` combines. Do not filter or merge individual nested properties. Validate the complete resulting configuration before writing it. Other top-level settings remain excluded unless explicitly added to the allowlist.

In particular, do not copy `docs`, `$schema`, `configSource`, AppHost identity or paths, SDK/channel selection, language, package references, launch profiles, or arbitrary feature/content settings. Documentation settings remain configurable locally but are not imported from a configuration source. The CLI creates and maintains its own source binding; the remote file cannot supply or replace it. The proposed content schema does not implicitly extend this allowlist.

Merge the allowed values into the selected application's file, not a configuration file in the CLI's launch directory when a different app is selected:

- On initial creation or initialization, combine generated project settings with the supplied allowlisted source values and save the result in the application's `aspire.config.json`.
- On explicit update, a supplied allowlisted section replaces the entire existing app section, including locally edited or omitted nested properties. The explicit update/confirmation is the authorization to apply these changes.
- Preserve unrelated top-level app settings and allowlisted sections omitted by the new source. Omitting an entire section does not delete a previously materialized value.
- An explicit null is a supplied value and replaces the existing value where valid for that setting. Future allowlisted arrays replace the whole value rather than appending.
- Do not merge a runtime view of global settings into the app file. Global settings remain separate.

For example, after applying a source that supplies named skill sources:

```json
{
  "configSource": {
    "repository": "https://git.acme.example/aspire-init",
    "path": "teams/platform.json",
    "ref": "main",
    "appliedVersion": "<source-file-version>"
  },
  "skillSources": {
    "platform": {
      "repository": "https://git.acme.example/platform-skills",
      "ref": "main"
    }
  }
}
```

This is an excerpt; other generated or existing app settings remain in the same file. Once saved, these skill sources are ordinary project settings. Editing them locally changes subsequent agent initialization without editing the configuration source or its tracking copy. A later explicit source update may overwrite them according to the rules above.

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

The retained JSON and `metadata.json` describe the last successfully fetched source: its identity, selected-file version, resolved commit, and fetch time. They are tracking data, not general defaults for command execution. Initial materialization, explicit config-source updates, runtime template discovery, and consented template-source listing can publish a new fetched snapshot or replace `current`. Runtime template operations read only the selected source's `templateSources` section from their acquired snapshot; they do not apply source values to app or global configuration or fall back to a prior invocation's stale snapshot. Other normal commands must not read this JSON to configure themselves or rewrite it while checking for updates. After successful publication, remove older snapshots under the source cache lock, retaining only the latest snapshot. Failed acquisition leaves the previous snapshot intact; cleanup failures are logged without failing the command.

Keep advisory check state separate in `check.json`: the last successful check time and last observed source-file version. A check is not a fetch-and-apply operation, and must not advance the fetched snapshot's timestamp or a project's applied version. Tracking records must distinguish an actually fetched source from one whose version was only checked.

Use locks and atomic publication to prevent partially written tracking records. Retain user-scoped permissions for private content. Never store credentials in configuration or metadata, log them, or send private source details in ordinary telemetry.

Publish fetched tracking data before changing the app file. A tracking-publication failure leaves the app unchanged. If a later app read or write fails, report the operation as failed: tracking can record the successfully fetched source, but the app's settings and applied version remain unchanged. Apply settings and their version together using an atomic sibling-file replacement under the workspace configuration lock, re-reading the app file after acquisition to preserve concurrent edits. Create the temporary file with owner-only Unix permissions or the existing destination's Windows access rules. Preserve the destination's Unix mode or Windows DACL when replacing it; new Unix configuration files remain owner-only.

Configuration-source refresh uses the same path-based configuration lock as local `aspire config set` and `aspire config delete` operations so they cannot overwrite a refresh with a stale read or lose an unrelated edit to the refresh.

A tracking copy is shared by source identity, not a record of what every application has applied. Outside the separately acquired runtime template overlay, an ordinary command must work from its app file even when this directory is missing, another app refreshed the same source, or the retained JSON is unreadable. Problems with the tracking/version check are reported separately; they do not make cached values a fallback runtime provider.

Missing `configSource.repository` or `configSource.path` must not prevent general configuration loading or discard the app's saved settings. Validate these fields when the binding is used: an advisory check reports the invalid binding without failing the project operation, while an explicit configuration-source refresh fails before Git acquisition or application-file changes.

### Edit array-valued settings

Configuration keys use dots (or colons) for object properties and bracketed indices for arrays:

```shell
aspire config set "skillSources.team.plugins[0]" deployment
aspire config get "skillSources.team.plugins[0]"
aspire config delete "skillSources.team.plugins[0]"
```

Quote bracketed keys to prevent shell glob expansion. Brackets are reserved for non-negative integer indices, including nested arrays such as `matrix[0][1]`.

Setting an existing index replaces only that element. An index equal to the array length appends an element; larger indices and negative indices are rejected without changing the file. If the array property is missing or null, `[0]` creates it. Explicit bracket syntax never replaces an existing object or scalar with an array.

Numeric dot or colon segments can also address an existing array, so `skillSources.team.plugins.0` edits element zero when `plugins` is already an array. Otherwise numeric segments remain object property names: creating `custom.0` creates an object with a `"0"` property. Use brackets when creating an array rather than relying on numeric property names.

Deleting an array element shifts subsequent elements down by one index. Deleting the final element leaves an empty array, and deleting a property within an array element preserves the element even if it becomes an empty object. `aspire config list` displays individual array entries using bracketed indices, including `null` for null array elements, while empty arrays and objects remain visible.

### Check for updates without applying them

Operations that resolve an Aspire project or select a running AppHost check the saved source in the nearest local settings file for newer versions. Global bindings are not read, validated, checked, or acquired. Initialization of an already-bound project uses the same discovery from the resolved project directory. Configuration inspection and documentation lookup do not perform an ambient source check. Agent setup may discover an AppHost to locate saved skill-source settings, but does not check the configuration source. It targets an agent workspace, which is not necessarily the AppHost directory. The check is advisory: the requested operation uses saved configuration whether a source is newer, unavailable, or has never been fetched on this machine.

Advisory checks run in the background without delaying project operations or prompting for Git credentials. At command exit, unfinished checks are cancelled and their child processes are cleaned up. Completed update notices and advisory warnings are displayed after the CLI version update notification, never concurrently with command output; machine-readable invocations suppress these notices. A fast command may exit before a check produces a result, in which case no notice is shown. Explicit `aspire update --config-source` remains a synchronous fetch-and-apply operation, including its normal credential prompts and failure reporting.

Each configuration-source service instance handles one project's nearest local source. Repeated calls for the same project reuse a single background check; attempting to reuse the instance for another project is an error. Different projects with identical repository, file path, and ref share cached check metadata. Compare that shared observation against the current project's applied version; reuse cached metadata only when it is valid for that version.

Directory cleanup failures are logged at debug level without displaying a UI warning or replacing the check's notification.

| Command scope | Source behavior |
|---|---|
| `aspire new` / `aspire init` initially creating source-bound app configuration | After consent, fetch, validate, and materialize allowed values; save the binding, applied version, and tracking copy |
| `aspire update --config-source` | Fetch and explicitly apply allowed values to the nearest local configuration with a saved binding, regardless of check age |
| Other commands scoped to an existing source-bound app, including re-running initialization on an already-bound app | Check for updates when due; never apply source values or rewrite the fetched tracking copy |
| Runtime template discovery in `aspire new` | With an explicit `ASPIRE_INIT_SOURCE_URL` and no decline during creation consent, acquire the source and publish its tracking snapshot; use only `templateSources` at runtime without saving inherited settings |
| `aspire template source list` | After shared consent (or explicit `--use-config-source` opt-in for JSON), acquire the selected source and publish its tracking snapshot; overlay only `templateSources`, without materializing project files or saving inherited settings |
| Commands scoped to an app without a `configSource` in the nearest local configuration, excluding the runtime template operations above | No source check or acquisition, regardless of global bindings |
| Other global commands not scoped to an app, help, and completion | No source check or acquisition |

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

- Resolve the project using the existing project-selection behavior, including `--apphost`, and process the saved binding in its nearest local settings file. Ignore global bindings and do not select a source from environment variables or employee detection. If the local configuration has no saved source, report that there is no configuration source to update and exit successfully without Git acquisition.
- When `--apphost` names a directory, discover and select an AppHost within that directory before considering a configuration-only fallback in the requested directory. If discovery finds no AppHost, use the nearest local settings file from that fallback directory; discovery ambiguity or unsupported-project errors must not silently select another binding.
- Acquire the saved branch's current commit, the saved exact commit, or the repository's current default branch when the binding has no ref, even when a recent check found no update. Validate it, merge only supplied allowlisted values into the declaring configuration file, and save its applied version with those values, preserving unrelated settings.
- On acquisition, validation, or write failure, stop and exit nonzero without changing the app configuration or its applied version.
- Publish the fetched source JSON and fetch metadata in Aspire home as tracking data. Record a successful fetch/check time even if the selected file's version is unchanged. If tracking publication fails, report it explicitly; do not use shared tracking as proof that an app was updated.
- Successful application does not display a completion summary with the repository, resolved commit, or settings-file path. Git acquisition notices, source-trust warnings, and errors remain visible. Supplied allowlisted values can replace local edits.
- Do not update the CLI, SDK, or packages, change the binding's repository, path, or ref, regenerate AppHost source or application code, or reinstall content. The app configuration file and its applied-version stamp are intentionally updated. Reject combinations with `--self`, `--migrate`, or channel/package-source overrides. Plain `aspire update` retains its existing package-update behavior and performs an advisory source check for saved bindings; unbound projects can explicitly adopt a source as described above.
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

Verify whole-section replacement, copying of arbitrary nested properties, preservation of omitted sections and unrelated top-level settings, valid null replacement, case-insensitive section matching, and no normalized-key bypass. Verify atomic app-file updates and unchanged app/fetch state after failed or cancelled acquisition.

For ordinary commands, verify the exact 24-hour check boundary, cached availability notices, file-version comparison instead of unrelated repository commits, and actionable stderr output. Confirm no source-driven app writes, fetched-JSON writes, or loading cached JSON as configuration. Cover missing/corrupt tracking, unavailable Git/authentication/network, and continued command execution with saved app values after a reported check failure.

For explicit updates, cover fresh/missing tracking, unchanged source versions, `--apphost` selection, non-interactive confirmation, incompatible options, missing bindings, and bindings without an applied version. Verify that updating one app does not alter another app's settings or applied-version comparison, including on another machine with no tracking copy. Confirm no CLI/SDK/package updates, code regeneration, or content reinstallation.

Verify that global bindings are ignored during advisory checks and explicit refresh, including valid, malformed, and unavailable sources, no local file, and an unbound nearest modern file that hides a farther ancestor binding. With a saved local binding, verify that only that source is checked or refreshed and that global and unrelated files remain unchanged. Preserve shared-cache freshness coverage across different projects' applied versions.

Keep no-source, declined-source materialization, unrelated global-command, help, and completion paths free of source acquisition/checks. Verify that both an interactive decline and `--use-config-source false` suppress runtime template inheritance while preserving independently configured global template sources. Template search and source listing can acquire accepted runtime template sources as described below. Verify plain `aspire update` does not implicitly materialize changes to saved bindings. Cover consented brownfield adoption from environment and employee candidates, persisted opt-outs, repeat invocations, selected AppHost configuration scope, failure preservation, and unattended updates that skip adoption unless explicit consent is supplied.

## Runtime template sources

When `ASPIRE_INIT_SOURCE_URL` is explicitly set, template discovery/search in interactive `aspire new` also reads the remote file's `templateSources` section, unless the developer declined that source during creation consent. An interactive decline or `--use-config-source false` disables its runtime template inheritance for the rest of the command, not just project-file materialization. Discovery does not repeat the consent prompt. Employee detection alone does not enable runtime template discovery inheritance. Optional `ASPIRE_INIT_SOURCE_PATH` and `ASPIRE_INIT_SOURCE_REF` use the same validation, branch/commit selection, Git authentication, timeout, and immutable snapshot cache as configuration acquisition. Search and opted-in listing reuse a snapshot within the invocation; later invocations acquire an accepted source again rather than silently falling back to stale cached configuration.

`aspire template source list` uses the same candidate selection and consent prompt as `aspire new`, including the detected employee candidate when no explicit URL is set. Accepting includes the selected source's template definitions; declining lists only explicit global definitions. In a non-interactive host, an available candidate requires `--use-config-source` or `--use-config-source false`, just as for `aspire new`.

With `--format json`, listing never prompts. Without `--use-config-source` (or its explicit `true` form), it does not select, validate, or fetch an init source, and returns only explicit global template definitions. Explicit opt-in includes inherited sources and disables interactive Git authentication; diagnostics stay on stderr and stdout contains only JSON. All commands expose the same nullable Boolean option: bare means `true`, explicit `true`/`false` are accepted, and omission remains unspecified. No listing mode materializes project files.

The shared [unattended Git authentication policy](#acquire-using-git) applies to opted-in JSON listing as well as non-interactive initialization and advisory checks.

For example, the remote file can supply:

```json
{
  "templateSources": {
    "enterprise": {
      "repository": "https://git.acme.example/platform/templates",
      "ref": "template-preview",
      "path": "templates",
      "displayName": "Enterprise templates"
    }
  }
}
```

```shell
export ASPIRE_INIT_SOURCE_URL=https://git.acme.example/platform/aspire-init
export ASPIRE_INIT_SOURCE_REF=config-preview
aspire template source list --format json
aspire template source list --format json --use-config-source
aspire new
```

Only `templateSources` participates in this runtime overlay. Other remote settings retain their existing allowlisted project-materialization behavior. Project-level `templateSources` are not used. Sources must satisfy the same complete-definition validation as global template discovery; malformed or inaccessible init configuration fails explicitly. Template repository discovery failures retain the normal per-source warnings.

The optional `displayName` supplies the friendly name in the terminal selector's source heading. Missing or blank values use the alias. Before fetching templates, the CLI explains configured template sources, lists their details, and displays the shared trust warning. Git details use **Name**, **Git source**, **Git path**, and **Git ref** labels; repository-root paths and omitted or `HEAD` refs are hidden. Local directory sources use **Path**. Built-in templates are not listed as a source. A single source has no group heading. Aliases remain the source and group identity even when headings match.

Merge source names case-insensitively with global `templateSources`. The user's entire global definition wins on a collision: do not retain an inherited `repository`, `ref`, `path`, or `displayName` when the user replaces that source with a local directory or a different repository. Non-colliding global and inherited sources are both searchable and listed, including their branch refs.

`aspire template source add` and `remove` edit only explicit global definitions. Adding an alias supplied by the init source creates a global override; removing that override reveals the inherited source again. Removing an inherited-only alias reports that no global source was found. Listing preserves inherited fields without discovering templates or fetching template repositories; acquiring the init configuration itself can require Git/network access.

Never save inherited template sources into the user's global configuration or the generated project's configuration. Unsetting `ASPIRE_INIT_SOURCE_URL` removes the explicit runtime source from subsequent search/list invocations; human-readable listing can still offer an employee-detected candidate through its normal consent flow. On `aspire new`, the creation consent decision controls both project configuration materialization and use of the source for runtime template inheritance. Accepted initialization and runtime discovery share the invocation's acquired snapshot.

## Phase 2: Acquire content from configuration

Skills and integrations are acquired according to the application's saved configuration and normal CLI precedence. Templates use built-in choices and named sources from global configuration and the explicit environment-selected runtime overlay. Content acquisition is separate from fetching or materializing a configuration source, and must not treat previously retained tracking copies as runtime configuration providers. Runtime template inheritance uses its separately acquired snapshot as described above.

Keys in `integrationSources`, `templateSources`, and `skillSources` must be 1-64 ASCII letters, digits, dots, hyphens, or underscores, starting with a letter or digit (`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`). Names merge case-insensitively. Colons, whitespace, and other characters are not allowed. Dots remain path separators in `aspire config` edit commands, so edit dotted source keys directly in the JSON file or use the template source management commands. These restrictions apply to source keys, not NuGet package patterns, Git repository names, or explicit agent marketplace/plugin identifiers. This is an Aspire naming policy, not a NuGet package-source length limit.

### Named agent skill sources

`aspire agent init`, including its chained use from `aspire new` and `aspire init`, registers all enabled sources when the `aspire` skill choice and remote mode are selected. Configure sources in `aspire.config.json` or global settings:

```json
{
  "skillSources": {
    "aspire1p": {
      "repository": "https://git.example.com/platform/skills.git",
      "marketplace": "aspire1p",
      "plugins": ["aspire", "deployment"],
      "ref": "main",
      "openCodeV1Catalog": "https://skills.example.com/opencode/v1/",
      "openCodeV2Catalog": "https://skills.example.com/opencode/v2/"
    }
  }
}
```

Sources and their fields merge by name across configuration layers, case-insensitively. Agent initialization reads fresh global settings and the selected app's nearest local settings file; it does not retain local settings from a different launch directory. Additional sources do not replace the built-in `default` source, which registers `aspire@aspire-skills` from `microsoft/aspire-skills` and the official OpenCode catalogs. Override its fields to change that source, or set `"default": { "enabled": false }` to omit it. Setting `enabled` to false does not remove previously installed registrations.

`aspire init` recommends `aspireify` only after successful or already-current registration of an enabled official Aspire plugin or OpenCode catalog. Disabled sources, arbitrary plugins, and sources without an applicable OpenCode catalog do not trigger this guidance.

Standalone agent initialization honors the configured AppHost or discovers one from the working directory using SDK-independent file inspection, prompting when multiple AppHosts are available. It does not require a usable .NET SDK or run MSBuild to find the AppHost's saved settings. It reads the selected AppHost's configuration independently of the agent workspace destination, without fetching or checking the configuration source. When there is no AppHost, it uses the working directory's nearest local settings file.

Each additional source requires a GitHub `owner/repository` or Git repository address. The marketplace identifier defaults to the source name; marketplace identifiers must be unique. The `plugins` array selects one or more plugin names within that marketplace and defaults to `["aspire"]` when omitted. Empty lists and invalid plugin identifiers are rejected. Like other configuration arrays, plugin entries merge by index across configuration layers. An optional `ref` selects a branch or full commit for newly registered marketplaces. Copilot and Claude register each marketplace once and enable each selected plugin (for example, `aspire@aspire1p` and `deployment@aspire1p`). OpenCode uses the catalog for its existing settings format, falling back to the other catalog when only one is supplied; sources without catalog URLs are not registered with OpenCode. Plugin selections do not filter OpenCode catalogs. Catalog URLs must use HTTPS. The CLI registers sources without acquiring their content; agent clients perform acquisition and authentication.

Existing matching registrations and agent-side pins are retained. GitHub shorthand and the equivalent HTTPS repository URL match an existing native GitHub registration, including URLs with a `.git` suffix. HTTPS and SSH URL schemes and hostnames match case-insensitively; non-GitHub repository paths and SSH usernames remain case-sensitive. Explicitly disabled plugins, mismatched marketplace repositories, and existing marketplace policies are conflicts rather than permission to overwrite user settings. Each target file is updated atomically, so a conflict in any source leaves that target unchanged.

Before skill selection, when additional enabled sources apply to the selected clients, the CLI displays **One or more skill sources are configured. A skill source lets an organization share skills to customize your development experience.** Aligned source-detail blocks identify those sources using **Name**. The `default` source's details are omitted without changing its registration. Selected plugin clients see **Git source**, an explicit **Git ref**, and **Plugins**. **Marketplace** is displayed only when it differs from the source name, compared case-insensitively like source and marketplace identifiers. OpenCode clients see configured V1/V2 catalog URLs. Disabled sources and catalogs unused by the selected clients are omitted. The shared source-trust warning follows the details on stderr, even if configuration or template onboarding already displayed it. Default-only registration does not display source onboarding.

Successful registration is summarized as **Configured sources in {files}**, listing configuration file paths with their agent names in parentheses, for example `.github/copilot/settings.json (GitHub Copilot)`. Multiple files are separated by commas. Repository and catalog URLs are not repeated in this summary.

The allowlist copies the entire supplied `skillSources` section, including arbitrary nested properties, replacing the existing local section. If `skillSources` is omitted, the local section remains untouched. Sources still merge by name across runtime configuration layers such as global and local settings. Refreshing `configSource` updates configuration only, not agent registrations. Run `aspire agent init` to register newly configured sources. `--skills none` and suppressed agent initialization do not register remote sources. Aspire does not acquire or install bundled Aspire skill files.

| Content | Acquisition today | Possible configuration customizations |
|---|---|---|
| Skills | `aspire agent init` registers all configured native sources; independently selected CLI-defined skills are installed into the chosen client destinations | Additional validated remote catalogs, selected/default CLI-defined skills, and workspace installation destinations |
| Templates | Built-in templates and named global/runtime-inherited sources support local directories and Git repositories | Additional catalogs, package identities, versions, and feeds |
| Integrations | Templates declare dependencies; `aspire add` and integration list/search discover packages from all configured NuGet integration sources | Additional catalog metadata and package-version constraints |

### Named integration sources

Configure integration discovery independently of the AppHost's SDK channel:

```json
{
  "integrationSources": {
    "default": {},
    "team": {
      "source": "https://packages.example.com/v3/index.json",
      "patterns": ["Contoso.Hosting.*", "Contoso.SpecialIntegration"]
    }
  }
}
```

The implicit `default` entry retains existing channel and ambient NuGet discovery, including official and Community Toolkit packages. Set `default.enabled` to `false` to search only explicitly configured sources. An entry with a `source` can omit `patterns` to discover all packages from that feed, equivalent to `["*"]`; an explicitly supplied array, including `default.patterns`, must be non-empty. Override `default.source` to replace built-in discovery with a different feed, optionally specifying `default.patterns` to restrict its packages.

Each enabled source is searched for matching package IDs, regardless of their namespace. Patterns use case-insensitive NuGet matching: an exact ID, a prefix ending in `*`, or `*` for every package. Other wildcard positions are invalid. Sources may be HTTP(S) NuGet service indexes or local package directories; relative directories resolve against the selected AppHost directory. Embedded credentials are rejected. Configure private-feed credentials using NuGet's existing credential mechanisms.

Named entries referring to the same feed identity are combined before discovery. Their allowed patterns are unioned case-insensitively, disabled entries do not contribute patterns, and the first entry supplies the display name and source spelling. Integration discovery searches each distinct prefix or exact ID separately, following pagination for each query, then filters results against the merged patterns and deduplicates matching package IDs and versions case-insensitively. An empty query searches all packages only when the patterns include `*`, including when patterns are omitted. Local feeds are likewise enumerated once. The implicit built-in discovery entry remains separate.

Paginated searches apply [nuget.org's limits](https://learn.microsoft.com/nuget/api/search-query-service-resource#request-parameters) to every feed: pages of at most 1,000 packages with offsets up to 3,000, yielding at most 4,000 packages per feed and query before filtering. Search results carry a limit-reached flag that is preserved when filtering and combining feeds, queries, and channels. A full final permitted page sets this flag because results may be incomplete; use narrower source patterns to reduce each query's result set. CLI integration discovery displays this warning on stderr at most once per operation, after the search status finishes and before the results. MCP listings instead include a plain-text `warning` property in the tool's JSON response when the limit is reached, even if filtering leaves no matching integrations; the property is omitted otherwise. A caller-requested result limit at or below 4,000 does not produce this warning.

`aspire add`, `aspire integration list`, `aspire integration search`, and MCP integration discovery use the same configured sources. MCP listings resolve sources from the single in-scope AppHost's directory when available; otherwise, they use the MCP working directory. Stable and prerelease packages are included for custom sources, and the existing deprecated-package and optional polyglot-compatibility filters still apply. The selected source is used for version lookup. Adding a package reuses existing effective routing or persists an exact package-to-feed mapping in the AppHost's NuGet configuration, preserving ambient SDK and dependency feeds so subsequent restores can resolve the package. The target directory is selected using the same logic as `aspire update`: reuse the nearest effective non-global NuGet configuration, falling back to the AppHost directory when none exists. This does not change the AppHost's SDK channel. An explicit `aspire add --source` continues to override discovery.

If a NuGet source fails during discovery, `aspire add`, `aspire integration list`, and `aspire integration search` retain results from successful sources and display a warning on stderr for each failed source after the search completes. This includes missing or unreadable local package directories, both during package discovery and polyglot compatibility scans. Cancellation still cancels the operation rather than reporting a failed source. Source names are redacted and escaped for display, and repeated failures across queries or channels produce only one warning per source. Warnings are displayed even if no packages remain; JSON listings keep stdout valid JSON.

Without an integration argument, interactive `aspire add` always asks the user to select a package, even when discovery returns only one package. An exact package ID or friendly name on the command line can bypass that selection. Non-interactive `aspire add` requires an integration argument and never installs an unnamed discovery result.

Before integration and version selection, when configured integration sources
supply choices, the CLI displays **One or more integration sources are configured.
An integration source lets an organization share integrations to customize your
development experience.** Each distinct configured feed is displayed using
**Name**, **Package feed**, and **Package patterns**. Built-in channels and ambient
NuGet sources are not included in these detail blocks. Configured feeds remain
visible when ambient discovery also supplies packages from the same feed.
SDK source names are resolved to configured feed locations when reading live or
cached SDK search output, matching native search results. The remaining search,
selection, and installation pipeline uses locations only. A source name that
looks like another feed's location is still resolved as a name in SDK output;
native locations are never reinterpreted as names.
Pickers group choices under source names only when multiple feeds supply choices.
When ambient and configured sources offer the same package and version from the
same feed, the choice retains its configured source name.
The trust warning is displayed immediately before selection if any feed is not
nuget.org. A package offered by several sources remains selectable in each source;
choosing a source group restricts version lookup to that feed. Native C# adds
persist the selected configured feed and use an exact package mapping when mapping is already
enabled, rather than passing a picker selection as `dotnet package add --source`.
Existing SDK and dependency feeds remain available, and previously disabled mapping
stays disabled. Ambient feed selections leave existing NuGet mappings unchanged.
Polyglot adds retain their exact-package source override. Explicit
`--source`, pinned-channel preferences, and non-interactive selection remain unchanged.

Sources and their fields merge case-insensitively across global and selected-app settings. As with `skillSources`, `configSource` can supply the entire `integrationSources` section. Materialization replaces a supplied section in full and preserves an omitted section. Refreshing configuration does not add packages or remove previously persisted NuGet mappings.

When adding an integration from a new feed, the NuGet package-source key is `aspire-integration-<name>` (for example, `aspire-integration-team`). This prefix identifies CLI-created integration aliases separately from SDK/channel aliases. If the feed already has a NuGet alias, that alias is retained, including unprefixed aliases and previously generated `aspire-integration-add` aliases, so credentials and other source settings remain attached. Existing user-owned mappings are left unchanged when NuGet's effective mapping precedence already routes the selected package exclusively to that feed, including through inherited mappings or multiple aliases for the same feed. Otherwise, an exact package mapping can be appended to a local alias while preserving its existing patterns and credentials. Explicitly selecting the same package from a different configured feed replaces its previous local exact integration mapping, while retaining the previous feed and all unrelated patterns. Extending or replacing an inherited mapping, including deleting a local entry that would expose inherited policy, requires a manual configuration change rather than silently shadowing or modifying that policy. Disabled sources and channel-source ownership checks remain enforced. When a new alias is required, a generated key reserved by another feed or its settings is an error, not permission to overwrite the feed or silently generate a suffixed key.

Named integration adds prepare the NuGet configuration change without writing it, then stop any running AppHost before temporarily applying the candidate for installation and restore. Successful installation commits that configuration. A failed installation, exception, or cancellation restores the original bytes or removes a newly created configuration file. The transaction rejects changed baselines and refuses to roll back over detected external edits, reporting the conflict instead. This transaction covers the NuGet configuration, not generated SDK or dependency files.

Directory-based integration sources use the same named-source persistence path as HTTP feeds, rather than CLI/PR hive setup. Discovery patterns are not copied into restore mappings when adding a named integration.

Adding an exact integration package preserves existing SDK/channel feeds, including unmapped feeds and feeds with only catch-all mappings. If package-source mapping is disabled, adding an integration keeps it disabled rather than introducing a new mapping policy.

Feeds explicitly configured in `integrationSources` are protected from automatic feed retirement, including during channel changes and when package-source mapping is disabled. Protection matches feed locations, not NuGet aliases, and applies to both persistent configuration and temporary restore policy. Setting `enabled` to `false` disables discovery but retains that protection. An implicit `default` entry without a `source` does not protect ambient feeds. Removing an entry entirely ends its protection; this does not prove that previously installed packages no longer need the feed. Returning to the stable channel removes obsolete CLI-owned SDK mappings from unprotected channel aliases while retaining integration feeds, their credentials and disabled state, and exact or narrower integration mappings. If a shared SDK/integration alias still has broad routing (`Aspire*` or `*`) that cleanup would remove, the update requires manual NuGet configuration. Surviving narrower mappings, including on equivalent feed aliases, do not establish that all private dependencies remain routed correctly. Resolve that ambiguity manually before retrying rather than deriving restore policy from discovery filters. Dedicated `aspire-integration-<name>` aliases retain their mappings, and inherited mapping changes still require manual configuration. An exact-package source override or exact package selection can still replace the selected package's routing while preserving unrelated integration mappings, including when a daily/staging channel supplies broad `Aspire*` mappings. Broad source overrides retain their existing override behavior.

Keep standard content available initially and preserve explicit user selections and suppression options. Future source-driven content settings must be explicitly added to the materialization allowlist or have a separately defined runtime inheritance contract before they affect acquisition choices. An availability notice never changes those choices. An explicit configuration update does not automatically regenerate applications or reinstall skills. Git access does not grant access to private package feeds, and all acquired content must retain its existing validation and authentication requirements.
