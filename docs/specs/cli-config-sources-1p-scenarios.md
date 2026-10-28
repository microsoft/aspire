# Config-source scenarios for Microsoft first-party developers

This companion to the [CLI config-source specification](cli-config-sources.md) follows hypothetical Microsoft first-party (1P) developers. It describes the CLI's configuration materialization flow. Skills, template, and integration customization schemas remain separate future work.

The Microsoft default configuration repository is `https://msft.ghe.com/coreai/aspire-1p-init`. The developers, application names, team-specific configuration paths, and documentation URLs below are illustrative. The `.example` documentation hosts do not identify actual Microsoft services.

## Scenario 1: Maya creates her first Aspire app

Maya is a Microsoft developer creating an Inventory application on her development machine. Inventory is an illustrative application name that she supplies to `aspire new`, not an application provided by the configuration repository. Assume a CLI build containing config-source support, Git on `PATH`, and the normal prerequisites for her chosen Aspire template.

### Prepare Git authentication

Maya needs permission to read the configuration repository and working Git authentication for `msft.ghe.com`. Being recognized as a Microsoft employee does not grant repository access, authenticate Git, or authorize use of the source.

Aspire uses normal Git authentication, not an Aspire-specific repository login. Maya can prepare authentication upfront or complete it interactively when Git acquires the configuration.

#### Pre-setup authentication

Maya configures her organization's approved Git credential helper or SSH setup before running Aspire. For an environment that uses GitHub CLI as its HTTPS credential helper, an example setup is:

```powershell
gh auth login --hostname msft.ghe.com --git-protocol https --web
gh auth setup-git --hostname msft.ghe.com
git ls-remote https://msft.ghe.com/coreai/aspire-1p-init HEAD
```

She completes the host's sign-in and any required organizational access or SSO steps. Authentication to `github.com` alone is not authentication to `msft.ghe.com`. The final command checks that Git itself can read the repository.

Pre-setup is useful for verifying access before creating the app and is required for runs that cannot obtain credentials interactively. Terminal credential prompts are disabled with `--non-interactive`, `ASPIRE_NON_INTERACTIVE`, or CI detection that disables interactive input.

#### Interactive authentication

Alternatively, Maya runs Aspire in an interactive terminal without preparing credentials first. After she accepts the configuration source during app creation, Git can prompt for missing terminal credentials, and configured credential helpers can handle their own sign-in flows. The available authentication flow depends on her Git setup and the repository host's requirements.

She completes authentication when prompted, and configuration acquisition continues if access succeeds. The two-minute acquisition timeout and cancellation still apply while waiting for credentials. If authentication fails, Aspire reports the error instead of silently creating the app with standard configuration.

With either approach, Maya never puts credentials in the repository URL or configuration JSON.

### Create the app and accept the source

Maya runs:

```powershell
aspire new aspire-starter --name Inventory --output .\Inventory
```

Assume employee detection succeeds and none of the initialization-source environment variables are set. Before generating the app, Aspire proposes:

```text
Configuration source: https://msft.ghe.com/coreai/aspire-1p-init
Configuration file: aspire.config.json
Configuration ref: default branch
Source origin: Microsoft employee default

Use configuration from this source?
  Yes
  No, use standard Aspire configuration
```

Detection only chooses a candidate. Maya's **Yes** is the project's consent to use it.

If Maya sets `ASPIRE_INIT_SOURCE_PATH` or `ASPIRE_INIT_SOURCE_REF` without explicitly setting `ASPIRE_INIT_SOURCE_URL`, both overrides are ignored, even if their values are invalid. The inferred Microsoft source still uses the root `aspire.config.json` on the default branch. Customizing that source requires explicitly setting its URL, as in scenario 6.

After she accepts, Aspire:

1. Uses Git to fetch the repository's current default-branch snapshot and read `aspire.config.json`, without checking out a working tree.
2. Validates the source and selects only the settings on the specification's allowlist.
3. Creates the app from the selected starter.
4. Merges the allowed values into `Inventory\aspire.config.json`, saving the source binding and applied source-file version alongside the generated settings.
5. Retains the fetched source and fetch metadata in Maya's Aspire home for tracking, not for supplying runtime configuration.

For this scenario, suppose the repository initially supplies:

```json
{
  "docs": {
    "llmsTxtUrl": "https://aspire-docs.example/llms-full.txt"
  }
}
```

The application is still created from the selected Aspire starter. Acquiring this configuration does not itself download a custom starter, install a private integration, or replace the embedded agent skills.

If Maya declines, creation continues with standard Aspire configuration and no new source binding or Git acquisition. If she cancels the source prompt, the app is not created. If she accepts but repository access or validation fails, creation reports the error rather than silently switching to standard configuration.

## Scenario 2: Maya finds out what was saved

There are three different things to distinguish:

| Item | Location | Purpose |
|---|---|---|
| Organization-owned configuration | `aspire.config.json` in the configuration repository | Versioned settings proposed by the organization |
| App configuration | The application's `Inventory\aspire.config.json` | Contains copied allowlisted values, app-owned settings, source identity, and applied version |
| Local tracking copy | The resolved Aspire home's `configsources` directory | Records the source last fetched and its metadata; separate check metadata records update availability |

The relevant excerpt from the project's file is:

```json
{
  "configSource": {
    "repository": "https://msft.ghe.com/coreai/aspire-1p-init",
    "path": "aspire.config.json",
    "appliedVersion": "<initial-source-file-version>"
  },
  "docs": {
    "llmsTxtUrl": "https://aspire-docs.example/llms-full.txt"
  }
}
```

This is an excerpt, not a replacement for the generated file. Maya keeps its AppHost, SDK, package, and other project-owned settings.

Only [allowlisted settings](cli-config-sources.md#materialize-an-allowlist-into-the-app-configuration) are copied, not the entire source JSON. Initially these are `docs.llmsTxtUrl` and `docs.api.sitemapUrl`. Other source values, including AppHost paths, SDK/channel selection, packages, and arbitrary feature settings, are not imported.

Maya commits the resulting app file with the application. It contains neither Git credentials nor an absolute tracking path, so another machine can use the saved values immediately.

The fetched source is retained separately:

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

Aspire home is normally `<user-profile>\.aspire`; the resolved location honors `ASPIRE_HOME` and the installation's home resolution. The source key distinguishes the repository, upstream file path, and selected ref (or default-branch behavior). Different branch/commit selections do not share check state. The retained filename is always `aspire.config.json`, even when the upstream file has another name.

`metadata.json` records the source identity, selected-file version, resolved Git commit, and successful fetch time. `current` selects the published tracking snapshot. `check.json` separately records the last successful version check and observed source-file version; a check does not pretend the source was fetched or applied.

Only initial creation/initialization and an explicit `aspire update --config-source` replace the retained source JSON. Ordinary commands do not load it as configuration or rewrite it when checking for updates. Deleting or losing this tracking copy does not remove the settings already saved in Inventory.

Maya does not commit the tracking directory or edit it to customize the app. Git credentials remain managed by her credential helper or SSH setup, not by these files.

## Scenario 3: Leo expands the shared configuration, and Maya overrides one setting

Leo maintains the organization's configuration repository. He expands its configuration to include the API documentation source:

```json
{
  "docs": {
    "llmsTxtUrl": "https://aspire-docs.example/llms-full.txt",
    "api": {
      "sitemapUrl": "https://aspire-docs.example/sitemap.xml"
    }
  }
}
```

He commits the change to the repository's default branch. This makes it available for an explicit configuration update; it does not change settings in developers' apps.

Maya explicitly applies the update as described in scenario 5. Both allowed URLs are now saved in Inventory's app file. She then wants Inventory to use a team-specific general documentation source while keeping the organization's API documentation source:

```powershell
Set-Location .\Inventory
aspire config set docs.llmsTxtUrl https://inventory-docs.example/llms-full.txt
```

Her project file now contains both documentation settings, with her edit to the general documentation URL, in addition to its other project settings:

```json
{
  "configSource": {
    "repository": "https://msft.ghe.com/coreai/aspire-1p-init",
    "path": "aspire.config.json",
    "appliedVersion": "<expanded-source-file-version>"
  },
  "docs": {
    "llmsTxtUrl": "https://inventory-docs.example/llms-full.txt",
    "api": {
      "sitemapUrl": "https://aspire-docs.example/sitemap.xml"
    }
  }
}
```

There is no extra source-defaults merge at command execution. The normal CLI configuration system reads these saved app values alongside its usual sources, such as global settings and explicit command options. The API URL is a copied setting in this file, not a value inherited from the user-directory source copy.

Maya's edit takes effect for subsequent commands. It is not a protected override against a future explicit source update: if that update supplies `docs.llmsTxtUrl`, it replaces her edit. Keys omitted by the source and settings outside the allowlist are preserved.

Maya can inspect these effective values and use them:

```powershell
aspire config get docs.llmsTxtUrl
aspire config get docs.api.sitemapUrl
aspire docs search "service discovery"
aspire docs api list csharp
```

Documentation consumers use the URLs saved in the app's configuration. Git authentication for the config repository is not forwarded to documentation hosts; those hosts must be independently accessible.

The source allowlist does not permit changing the project's AppHost, SDK/channel, package references, or launch profiles. Existing SDK lookups that explicitly inherit parent settings still search those parents before falling back to global settings.

## Scenario 4: Noah clones Inventory and uses its saved source

Noah joins Maya's team and clones the application repository, including its materialized `aspire.config.json`. He already has the saved documentation settings, even though his own Aspire home has no source tracking copy.

From Inventory's project directory, he runs:

```powershell
aspire docs list
```

The command reads Inventory's saved documentation settings. Documentation commands do not perform advisory source checks, fetch configuration to fill in runtime values, or copy Maya's tracking directory.

When Noah later runs a command that resolves the project or selects a running AppHost, such as `aspire start` or `aspire logs`, the saved binding identifies the source for a background advisory version check. Noah does not need `ASPIRE_INIT_SOURCE_URL`, and Aspire does not repeat employee detection or the creation-time consent prompt. With no prior check metadata on his machine, the CLI checks the source's version and compares it with Inventory's saved `appliedVersion`, without writing a fetched JSON snapshot or changing the app file.

Noah can prepare Git authentication as Maya did. If a check completes with an access or other failure, the CLI reports that failure at command exit; the requested operation continues using the saved app configuration. Unfinished checks are cancelled at command exit, so a fast command may produce no check result. Machine-readable invocations suppress advisory notices. Documentation hosts still need to be independently accessible.

The behavior is project-scoped:

- A bound project's ordinary commands use its app file; commands that resolve the project or select a running AppHost also check the source for update availability.
- A project without a binding does not check or acquire a source merely because Noah is a Microsoft employee or another project uses one.
- Changing initialization environment variables does not retarget Inventory's saved binding.
- Having a tracking entry does not opt another project into using the source or supply that project's settings.

For example, subsequent `aspire start` and `aspire logs` invocations check when due and report completed update notices or check failures on stderr at command exit. `aspire docs list` and `aspire docs search` only consume the saved documentation settings and do not check the source. None of these commands automatically merges new source values into Inventory.

The binding's applied version records what was copied into this app, not which branch or commit to follow. Inventory has no explicit `ref`, so its checks and updates follow the repository's current default branch. An explicit branch tracks that branch; an explicit commit SHA pins the source, as shown in scenario 6.

## Scenario 5: Maya needs Leo's latest change immediately

Maya's app still contains the documentation settings she last saved. Leo publishes a new source-file version. On the next due version check, an ordinary command reports:

```text
A new version of the configuration source is available.
Run `aspire update --config-source` to apply it.
```

The command continues using Inventory's saved values. It writes neither new source values into the app file nor a new fetched JSON tracking copy.

Maya chooses to apply the update from Inventory's project directory:

```powershell
aspire update --config-source
```

This fetches and validates the source regardless of the last check time. It merges supplied allowlisted values into Inventory's `aspire.config.json`, records the newly applied version in that same file, and updates the fetched tracking copy and fetch time in Aspire home.

If Leo's source supplies `docs.llmsTxtUrl`, this explicit operation replaces Maya's team-specific edit. Other app settings are not imported or overwritten, and an allowlisted setting omitted by the source is left unchanged.

The command does not update the CLI, SDK, or packages, change the source repository/path/ref, regenerate AppHost or application code, or reinstall skills. Updating the app's configuration file is intentional.

For an explicitly selected AppHost, she can pass `--apphost` with that AppHost's path. For unattended refresh, the command is:

```powershell
aspire update --config-source --yes --non-interactive
```

Ordinary-command checks follow this cadence:

| Situation on a project-scoped invocation | Result |
|---|---|
| No successful check recorded | Check the remote source-file version; do not materialize settings |
| Successful check less than 24 hours ago | Reuse the observed version from check metadata, not cached configuration values |
| Successful check at least 24 hours ago | Check the remote source-file version again |
| Newer version observed | Print the update instruction and continue with saved app settings |
| Check fails, including offline or authentication failure | Report the check failure and continue with saved app settings |
| Explicit configuration update | Fetch and apply allowed values regardless of check age |

There is no background update timer. Availability notices can be repeated from check metadata without another network check. Changes to unrelated files in Leo's repository do not count as a new version of the selected configuration file.

An ordinary check failure is visible but does not change the requested operation's exit code. That operation may still fail for its own reasons, such as an inaccessible documentation host. A failed source check must not be presented as an "up to date" result.

A successful explicit fetch records the fetch time even if the source-file version is unchanged. Another app bound to the same source still keeps its own saved values and applied version; updating Inventory does not silently update it or suppress its update notice.

If authentication expires, Maya repairs Git access, verifies it with `git ls-remote`, and retries. If acquisition or validation fails during an explicit update, the update exits nonzero and preserves the previous app configuration, applied version, and fetched tracking copy. This is different from an advisory check failure.

`aspire update --config-source` is not a way to enable a source on an unbound project. If neither the nearest local settings file nor the global settings file has a saved binding, it reports that there is no configuration source to update and exits successfully without changing configuration or acquiring a source. The option cannot be combined with CLI self-update, migration, or channel/package-source overrides. A binding without an applied version gets instructions to run this explicit update rather than having source values silently loaded. Configuration sources are not supported in legacy `.aspire/settings.json` files.

## Scenario 6: Priya explicitly selects a team configuration during initialization

Priya adds Aspire to an existing application that has no saved source binding. Her development environment does not produce a positive employee-detection result, or her team wants a different configuration file in the Microsoft repository.

She first prepares Git access. Then, from the application directory, she explicitly identifies the source:

```powershell
$env:ASPIRE_INIT_SOURCE_URL = 'https://msft.ghe.com/coreai/aspire-1p-init'
$env:ASPIRE_INIT_SOURCE_PATH = 'teams/inventory.json'
$env:ASPIRE_INIT_SOURCE_REF = 'config-preview'
aspire init
```

The team must actually publish `teams/inventory.json` on the `config-preview` branch; both are illustrative. The path is repository-relative and uses `/` separators, including on Windows.

The explicit repository bypasses employee detection, but not authentication, validation, or consent. The prompt identifies `config-preview` as the selected ref. Priya accepts the proposed source. Initial materialization writes only allowed values into her app's configuration, together with this binding:

```json
{
  "configSource": {
    "repository": "https://msft.ghe.com/coreai/aspire-1p-init",
    "path": "teams/inventory.json",
    "ref": "config-preview",
    "appliedVersion": "<preview-source-file-version>"
  }
}
```

Its tracking entry is separate from entries for other paths or refs. Ordinary checks look for changes on `config-preview`, and `aspire update --config-source` applies that branch's current version, not the default branch's configuration.

For a reproducible source pinned to a reviewed commit, Priya can instead set the same variable to a full commit SHA before initializing a different app, keeping the explicit source URL set:

```powershell
$env:ASPIRE_INIT_SOURCE_REF = '0123456789abcdef0123456789abcdef01234567'
aspire init
```

This SHA is illustrative and must be replaced with an actual commit available from the repository. The new app saves that SHA in `configSource.ref`. Later checks do not announce updates just because a branch moved, and explicit updates reapply the pinned commit rather than advancing it. A commit unavailable from the server is an explicit acquisition failure, not a reason to fall back to the default branch.

Leaving `ASPIRE_INIT_SOURCE_REF` unset follows the repository's current default branch instead. If `ASPIRE_INIT_SOURCE_URL` is unset, both `ASPIRE_INIT_SOURCE_PATH` and `ASPIRE_INIT_SOURCE_REF` are ignored, including when employee detection selects the Microsoft repository. Priya must keep the URL explicitly set to customize that repository's path or ref.

For scripted creation or initialization, `--use-config-source true` explicitly accepts the candidate and `--use-config-source false` explicitly declines it. `--non-interactive` alone is not consent; when a candidate exists, omitting that choice fails with instructions. Other creation or initialization choices must also be supplied as needed for non-interactive execution.

The variables affect initial source selection, not later commands on a bound project. Re-running initialization on an already-bound app does not retarget its source or implicitly apply new source values; it can perform the same advisory check as other ordinary commands.

## What these scenarios do not imply

These scenarios describe proposed behavior, not an implementation change delivered by this documentation update. Configuration materialization is separate from future content-acquisition customizations. Accepting a source does not currently promise organization-specific template catalogs, additional skill bundles, integration catalogs, or package feeds. Those settings need a defined schema and explicit allowlist entries before they can be copied into apps.

Ordinary commands, including plain `aspire update`, do not apply source updates. Runtime configuration comes from the saved app file and normal CLI configuration sources, never the retained source JSON in Aspire home.

Repository access is also not authorization to private documentation or package services. Any later content acquisition retains its own authentication and validation requirements.
