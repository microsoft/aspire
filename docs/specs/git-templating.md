# Directory and Git-backed templates

This document describes the current implementation of custom templates in
`aspire new`. It records the command contract, component responsibilities, Git
acquisition model, parameter handling, and NuGet configuration ownership rules
for subsequent implementation work. It is not a proposal for repository
catalogs or centralized configuration.

## Scope and approach

Custom templates are unpacked .NET template packages, supplied either as a local
directory or as a directory inside a Git repository. Both sources use the same
in-process .NET template engine after source acquisition.

The implementation fits the existing `ITemplateProvider` / `ITemplateFactory`
model rather than adding a separate project-generation mechanism. It does not
invoke `dotnet new`, install templates into the user's `dotnet new` hive, or
require a .NET SDK for template generation. Concrete built-in engine components
are registered instead of dynamically loading template-engine extensions, so
the flow works in the Native AOT CLI.

The boundaries are deliberate: Git acquisition provides a pinned source tree;
the engine understands template metadata and transformations; the CLI owns
interaction, identity projection, and optional creation of missing NuGet
configuration.

## Command contract

Use an unpacked template package:

```bash
aspire new --template-path ./my-template
aspire new --template-path ./my-template --name MyApp --output ./MyApp
```

Use a repository root or a template subdirectory:

```bash
aspire new --template-repository https://github.com/example/templates.git
aspire new --template-repository https://github.com/example/templates.git --template-path templates/my-template
aspire new --template-repository git@github.com:example/templates.git --template-path templates/my-template --name MyApp --output ./MyApp
aspire new --template-repository https://github.com/example/templates.git --template-path templates/my-template --template-repository-ref v1.0
```

| Option | Behavior |
| --- | --- |
| `--template-path` without a repository | Absolute path, or a path relative to the invocation's working directory |
| `--template-path` with a repository | Repository-relative directory; defaults to `.` when omitted |
| `--template-repository` | Repository address passed to the Git acquisition layer |
| `--template-repository-ref` | Branch, tag, or full commit SHA; requires a repository and defaults to remote `HEAD` |

Repository-relative paths must not be absolute or escape the repository.
The selected directory and its ancestors within the checkout are checked for
symbolic links on both cache hits and cache misses.

These source options belong to `aspire new`; they are not inherited by its
built-in template subcommands. Custom sources cannot be combined with a built-in
template name, `--source`, `--version`, `--channel`, or `--language`. They do not
query NuGet for template packages.

The package directory must contain `.template.config/template.json`, directly
or within template subdirectories. When multiple templates are discovered, an
interactive invocation prompts for selection. Noninteractive invocations require
a source that resolves to a single template.

Existing non-empty output directories are rejected. Project name and output
selection use the existing new-command prompting and validation helpers.
Optional agent initialization still applies; `--suppress-agent-init` skips it.

## Component responsibilities

| Component | Responsibility |
| --- | --- |
| [`NewCommand`](../../src/Aspire.Cli/Commands/NewCommand.cs) | Validate option combinations, select a discovered template, and complete the normal new-command flow |
| [`TemplateProvider`](../../src/Aspire.Cli/Templating/TemplateProvider.cs) | Aggregate factory results through the existing provider abstraction |
| [`TemplateEngineFactory`](../../src/Aspire.Cli/Templating/TemplateEngineFactory.cs) | Acquire source trees, own the checkout cache, discover templates, collect parameters, and instantiate output |
| `TemplateEngineHost` | Register concrete engine components and exclude Git metadata from the engine's source view |
| [`TemplateHostConfiguration`](../../src/Aspire.Cli/Templating/TemplateHostConfiguration.cs) | Read the selected host file's parameter visibility and prompt labels without merging fallback files |
| `TemplateParameterPromptState` | Evaluate interactive parameter conditions and order their dependencies using engine types and expressions |
| [`IGitClient` / `GitClient`](../../src/Aspire.Cli/Git/GitClient.cs) | Share ref resolution and Git repository operations with configuration-source acquisition |
| [`GitCommandRunner`](../../src/Aspire.Cli/Git/GitCommandRunner.cs) | Execute Git commands with environment isolation, safe diagnostics, and classified failures |
| [`TemplateNuGetConfigService`](../../src/Aspire.Cli/Templating/TemplateNuGetConfigService.cs) | Create missing output-root NuGet configuration without taking ownership of template-generated configuration |
| [`PackageChannel`](../../src/Aspire.Cli/Packaging/PackageChannel.cs) and `PackagingService` | Resolve package-source policy from the running CLI identity and overrides |
| [`BundleNuGetService`](../../src/Aspire.Cli/NuGet/BundleNuGetService.cs) | Build and persist NuGet policy using the existing configuration hierarchy |

The template-engine host, settings, and package manager remain alive until the
factory is disposed because returned template callbacks still use their mounts.
The engine's settings hive is virtualized and separate from the user's
`dotnet new` state.

## Git acquisition and caching

### Reference resolution

Named refs are resolved to a commit before selecting a cache entry. Resolution
runs on each invocation even when a previous checkout exists, so a moving branch
or tag selects its new commit instead of silently using an old checkout.

An unqualified branch name takes precedence over a tag with the same name.
Annotated tags resolve to their peeled commit. Use `refs/tags/name` to select a
tag explicitly when a branch has the same name.

Full 40- or 64-character hexadecimal object IDs are accepted as commit pins and
normalized to lowercase without a remote ref lookup. Abbreviated object IDs are
not supported as commit pins. Git must be on `PATH` for remote ref resolution or
acquisition; an existing checkout pinned by a full commit ID does not require a
new Git invocation.

On a cache miss, acquisition initializes a repository, fetches only the resolved
commit with depth one and no tag traversal, checks it out detached, and verifies
`HEAD`. It fetches the commit ID rather than the original branch or tag, so a ref
moving between resolution and download cannot change the selected version.
A server that refuses the pinned fetch causes a failure, not a fallback to a
different ref.

### Cache identity and layout

The default cache location is:

```text
~/.aspire/cache/git/<hash>/
  .git/
  repository/
    <checked-out files>
```

The root is derived from `CliExecutionContext.CacheDirectory`. The key is the
lowercase hexadecimal XxHash3 hash of the UTF-8 repository address, a NUL
separator, and the resolved commit SHA. It is a cache identifier, not a
cryptographic integrity check.

The template path and original ref name are not part of the key. Different
template directories and refs resolving to the same repository and commit share
one checkout. Repository addresses are used as supplied; equivalent URL spellings
are not canonicalized into a common cache identity.

Acquisition uses a leased temporary directory. The checkout is validated before
atomic publication into the final cache location. If a concurrent invocation
publishes the same entry first, the completed winner is reused and the losing
temporary checkout is cleaned up. Failure and cancellation do not publish an
incomplete entry; cleanup failures are logged.

On Unix and macOS, the cache root uses owner-only (`0700`) permissions, including
repair of existing root permissions on reuse. Completed entries are retained
after generation. There is no automatic eviction policy; manual removal must
avoid entries in active use.

Cached checkouts are treated as immutable source snapshots. Reuse validates the
selected path but does not perform a fresh checkout or an integrity scan of every
cached file. Generated files and NuGet configuration are written to the output
directory, not into the source tree or retained cache.

### Authentication, environment, and failures

Normal Git credential helpers and SSH authentication configuration remain
available. Git's terminal credential prompts are always disabled with
`GIT_TERMINAL_PROMPT=0`, including when Aspire itself is interactive.
Authentication must be configured before invocation.

Captured template Git commands receive EOF on stdin rather than inheriting the
CLI's input, so Git hooks or credential helpers cannot consume pending CLI input.

`GitCommandRunner` executes Git processes through `ProcessCaptureRunner`, which
handles process-tree cancellation and bounded output draining. Git runs with
`LC_ALL=C` and `--literal-pathspecs`. Repository-routing environment variables
are filtered case-insensitively:

```text
GIT_DIR
GIT_WORK_TREE
GIT_INDEX_FILE
GIT_OBJECT_DIRECTORY
GIT_ALTERNATE_OBJECT_DIRECTORIES
GIT_COMMON_DIR
GIT_NAMESPACE
GIT_SHALLOW_FILE
```

Authentication settings are not removed by this filter. Arguments, stdout, and
raw stderr diagnostics are suppressed from process logging because they can
contain private repository information or credentials. The runner classifies
failures and exposes localized messages through `GitFailure`; callers translate
them into command errors.

The template factory owns separate two-minute deadlines for ref resolution and
checkout acquisition. Initialization, fetching, checkout, and verification share
one deadline within a checkout operation. Caller cancellation remains cancellation;
expiry of the factory's Git deadline becomes a classified timeout.

## Template parameters and generation

### Host-specific configuration

The engine host identifier is `aspirecli`, with `dotnetcli` as its fallback.
For each template, host metadata is selected from its own `.template.config`
directory in this order:

1. `aspirecli.host.json`.
2. `dotnetcli.host.json`, only when the Aspire-specific file is absent.
3. Default prompting when neither file exists.

One file is authoritative. There is no property-level merging: an empty Aspire
host file deliberately replaces the .NET CLI host file, and an invalid selected
file is an error rather than a reason to fall back. `ide.host.json` is not used;
it has a different schema and describes an IDE-specific experience.

Aspire supports the .NET CLI host file's `symbolInfo` object and `isHidden`
property, plus an optional `displayName` override for terminal prompt labels:

```json
{
  "createNuGetConfig": true,
  "symbolInfo": {
    "Framework": { "displayName": "Target framework" },
    "appHostHttpPort": { "isHidden": true },
    "skipRestore": { "isHidden": true },
    "UseRedisCache": { "displayName": "Use Redis for caching" }
  }
}
```

The Aspire-specific, template-level `createNuGetConfig` setting is an explicit
opt-in to missing-root NuGet configuration generation. It accepts only a JSON
Boolean and defaults to `false` when omitted. It is independent of `symbolInfo`
and is also valid in a host file without that object. A `true` value requests
the CLI's existing channel policy; it does not force a file when the resolved
sources are only nuget.org, and never permits overwriting template-owned
configuration. The same one-file precedence applies to this setting: an empty
Aspire host file does not inherit an opt-in from the .NET CLI fallback.

This setting expresses the template author's restore requirements, not its
programming language. For example, a TypeScript-only AppHost can pin the CLI SDK
version without needing a root NuGet configuration, while a TypeScript AppHost
with .NET services can opt in. No language inference or output scanning is used.

`isHidden` accepts JSON Booleans and the strings `"true"` and `"false"`, as
allowed by the .NET CLI schema. Omitted entries and omitted `isHidden` properties
leave parameters visible. Other .NET CLI metadata, such as argument aliases,
does not introduce custom-parameter command-line switches in Aspire.

Visibility controls prompting, not template semantics. Hidden parameters are
left unset rather than submitted with their displayed defaults. This distinction
matters for `coalesce` symbols, which distinguish user-supplied port overrides
from defaults and otherwise use generated random ports. Hidden controllers'
defaults still participate in interactive condition evaluation, and the engine
still rejects missing required values. The well-known `AspireVersion` and
`AspireNuGetSource` projections remain active even when marked hidden.

The AppHost, single-file AppHost, empty, starter, and TypeScript+C# starter
templates under `src/Aspire.ProjectTemplates/templates` include Aspire-specific
host files. They hide framework, HTTPS, port overrides, and restore controls,
following the regular `aspire new` questionnaires. The starters retain Redis and
localhost-TLD choices; the .NET starter also retains test-framework selection and
the conditional xUnit version. The single-file template retains its localhost-TLD
choice, while the AppHost and empty templates have no extra prompts. Prompt
labels avoid IDE keyboard mnemonics. Because there is no merge, each host file
includes its complete set of hiding rules. Standalone MSTest, NUnit, xUnit, and
service-defaults templates do not include Aspire-specific host files.
The five .NET templates opt into `createNuGetConfig` for their package restores.

Host metadata controls the interactive questionnaire, missing-root NuGet policy,
and explicitly configured package-version values. The .NET CLI itself primarily
exposes arguments and applies defaults; Aspire continues to prompt for ordinary
visible parameters.

Repository template sources can also contain build-time `!!REPLACE_...!!` tokens
that are normally substituted while packaging. Host metadata does not resolve
those tokens. To use a raw source directory or a copied Git template, first
materialize those values or replace them with template symbols, such as the
`AspireVersion` parameter described below.

### Project-derived prompt defaults

The engine's `defaultValue` is a literal, not an expression. A template can keep
an override parameter visible while displaying its project-derived fallback by
opting into `defaultValueSource` in the selected host file:

```json
{
  "symbolInfo": {
    "displayName": { "defaultValueSource": "displayNameValue" },
    "shortName": { "defaultValueSource": "shortNameValue" }
  }
}
```

Each source names the native `coalesce` symbol that consumes that parameter.
For example, the corresponding `template.json` symbols can be:

```json
{
  "displayName": { "type": "parameter", "datatype": "string", "defaultValue": "" },
  "shortName": { "type": "parameter", "datatype": "string", "defaultValue": "" },
  "displayNameValue": {
    "type": "generated",
    "generator": "coalesce",
    "parameters": { "sourceVariableName": "displayName", "fallbackVariableName": "name" },
    "replaces": "DISPLAY_NAME"
  },
  "normalizedName": {
    "type": "generated",
    "generator": "regex",
    "parameters": {
      "source": "name",
      "steps": [
        { "regex": "[^A-Za-z0-9]+", "replacement": "-" },
        { "regex": "^-+|-+$", "replacement": "" },
        { "regex": "^$", "replacement": "service" }
      ]
    }
  },
  "shortNameValue": {
    "type": "generated",
    "generator": "coalesce",
    "parameters": { "sourceVariableName": "shortName", "fallbackVariableName": "normalizedName" },
    "replaces": "SHORT_NAME"
  }
}
```

For project name `Demo.App`, the prompts display `Demo.App` and `Demo-App`.
Pressing Enter accepts those fallbacks without submitting the override
parameters to the engine. A different answer remains explicit input. An answer
equal to the displayed fallback is also treated as omission. Noninteractive
generation retains native fallback behavior.

This is a display-only host opt-in, not a new template expression language.
Targets must be optional string parameters with an empty default, without a
NuGet `valueSource` or reserved CLI projection. Source graphs may depend only on
the engine's project-name parameter `name`, the omitted override parameter, and
native `regex`/`coalesce` symbols. Templates must not redefine the `name` leaf.
The omitted override may appear only as a coalesce source; `name` may appear as
a regex source or coalesce fallback, but not as a coalesce source. These
restrictions avoid previews that disagree with the engine's parameter-default
and suppliedness semantics. The CLI uses the engine's own macro implementations.
Every reachable reference, including unused coalesce branches, is validated
before prompting for the project name or output path. Invalid references,
cycles, and unsupported dependencies fail before prompting or writing project
files. Bound values, other parameters (which could contain secrets), and
nondeterministic generators such as random ports are not previewed.
Existing literal defaults, choices, conditional prompts, and hidden parameters
are unchanged when no `defaultValueSource` is specified.

### NuGet-computed parameter values

A template can opt an ordinary string parameter into package-specific version
resolution through its selected host file. For example, `template.json` declares:

```json
{
  "symbols": {
    "DotnetIntegrationVersion": {
      "type": "parameter",
      "datatype": "string",
      "replaces": "{{dotnetIntegrationVersion}}"
    }
  }
}
```

The corresponding entry in `aspirecli.host.json` supplies the resolution policy:

```json
{
  "symbolInfo": {
    "DotnetIntegrationVersion": {
      "valueSource": {
        "kind": "nugetPackageVersion",
        "packageId": "Aspire.Hosting.Dotnet",
        "versionRange": "[13.6.0-preview.0,13.7.0)",
        "allowPrerelease": true,
        "workingDirectory": "."
      }
    }
  }
}
```

`kind`, `packageId`, and `versionRange` are required literal strings.
`allowPrerelease` is an optional JSON Boolean, defaulting to `false`.
`workingDirectory` defaults to the output root; an explicit value must identify
an existing generated directory relative to that root. For example, `"AppHost"`
selects that project's nested configuration as well as the root and inherited
configuration. Rooted paths, paths escaping the output, and missing directories
are errors. The usual single-host-file precedence applies; values are not merged
from an unused fallback file.

The CLI chooses the highest available matching NuGet version, honoring both
[NuGet version ranges](https://learn.microsoft.com/nuget/concepts/package-versioning#version-ranges)
and floating-version constraints. Prerelease versions are eligible only when
explicitly allowed. Each package is resolved independently; a stable SDK version
such as `13.6.3` does not imply that an integration publishes `13.6.3`.
Computed values are never prompted, even without `isHidden`, and override the
parameter's default. Disabled parameters are not queried. Ordinary parameters
without this opt-in keep their existing behavior and explicit version pins.
Unknown parameters, non-string parameters, and the reserved `AspireVersion` and
`AspireNuGetSource` parameters cannot be used as computed targets.

Resolution uses the configuration that will govern restore, not just the CLI
channel's first source: template-owned configuration, any explicitly requested
CLI-generated root configuration, ancestor/user/machine settings, disabled
sources, package source mappings, relative local sources, configured credentials,
and NuGet credential providers. Disallowed sources are filtered before querying
them. All eligible feeds contribute versions; search-result ranking, source
order, and search pagination do not decide which version wins. An eligible feed
failure fails resolution rather than silently choosing a version from the
remaining feeds. An empty policy or no compatible version is also an explicit
error; neither the parameter default nor the SDK version is used as a fallback.

Computed templates are rendered privately in a leased sibling staging directory
so they inherit the destination's ancestor settings. The CLI first renders
version-shaped placeholders, establishes missing-root policy, and queries NuGet.
It then clears that disposable render and renders again at the same staging
path with the resolved versions. The generated NuGet configuration paths and
contents must remain identical between renders. This rejects circular policy,
including a computed value selecting its own feeds, credentials, or config-file
location. Computed values are resolved after ordinary answers, so they cannot
control `isEnabled` or `isRequired` parameter conditions; ordinary answers can
still enable or disable computed parameters. Symbolic links in staged output
are rejected.

Lookup, validation, and cancellation failures clean up staging without publishing
destination files. A new output directory is published by moving the completed
staging directory; an existing empty directory is populated without overwriting
files or replacing its identity. Publishing to an existing directory can still
fail partway on I/O errors or concurrent writes. This feature queries package
metadata only: it does not install packages, execute restore, or run template
post-actions. Templates without active computed values retain the ordinary
generation flow.

### Prompting and engine execution

The CLI prompts only for eligible parameter symbols, not derived, computed, or
generated symbols. Name handling uses the normal project-name flow; implicit and
disabled-precedence parameters are not prompted.

| Metadata | Interactive behavior |
| --- | --- |
| Text parameter (including omitted `datatype`) | String prompt with the declared default and requiredness |
| Boolean parameter | Confirmation prompt using the declared default |
| Single choice | Selection using choice display names and the declared default as a literal key |
| Optional single choice without a default | Initial **Skip** option that leaves the value unset |
| Multiple choice | Multiple selection with declared defaults preselected; both `\|` and `,` separate default values |
| `isEnabled` / `isRequired` expressions | Engine-backed condition evaluation; controlling parameters are collected before dependents |

Prompt labels use a nonempty host-file `displayName`, then the parameter display
name, description, and finally symbol name.
Ordinary interactive parameters still prompt when they have defaults. Multiple
choice values use the engine's pipe-delimited representation, such as
`web|worker`.

In noninteractive mode, the CLI injects its well-known and explicitly computed
values but leaves other parameters unset for the engine to apply defaults and
validate requirements.
Missing mandatory inputs are errors. A declared default should not be assumed to
bypass the engine's mandatory-input validation. There are no dynamically
generated command-line switches for supplying arbitrary custom parameter values.

The engine handles replacements, derived and generated symbols, conditional
content, and file renaming. A dry run validates the request before instantiation.
Template post-actions, including restore and scripts, are returned as metadata
and are not executed.

Git metadata, including `.git` directories and worktree/submodule pointer files,
is excluded from the engine's source view without modifying the original source.
Other Git-related files, including `.gitignore`, `.gitattributes`, and `.github`
contents, remain available to the template's normal inclusion rules.

After instantiation, the CLI applies missing-config policy before reporting
successful creation. Successful command completion also waits for required
bundle extraction so a generated .NET AppHost can run without relying on a
later CLI invocation to extract the bundle.

Ordinary generation is not a transaction with rollback: an error after instantiation,
such as failure to create required NuGet configuration, can leave generated
files behind. The command reports failure rather than successful creation.

## CLI identity and well-known parameters

Well-known parameter names are case-insensitive and override template defaults
without prompting. They must be declared as parameter symbols to participate.
Use string-valued parameters rather than closed choice lists so the engine can
accept the CLI's stable, prerelease, and local version or feed identities.

### `AspireVersion`

`AspireVersion` receives `CliExecutionContext.IdentitySdkVersion`: the running
CLI's package version, including its prerelease or PR suffix but excluding SemVer
build metadata (`+...`). CLI identity overrides are honored.

```json
{
  "symbols": {
    "AspireVersion": {
      "type": "parameter",
      "datatype": "string",
      "replaces": "{{aspireVersion}}"
    }
  }
}
```

Version injection does not opt the template into NuGet configuration generation
or require a resolvable package channel. Set `createNuGetConfig` in the host file
when the generated project's restores need the CLI's root NuGet policy.

This is an SDK identity, not a package-version resolver. For integrations with
independent or preview release versions, use a separate string parameter with a
`nugetPackageVersion` host value source instead of assuming the SDK version exists
for every package.

### `AspireNuGetSource`

`AspireNuGetSource` receives the source details of the resolved identity channel.
The current channel projection selects its first Aspire-prefixed mapping, or its
catch-all mapping when no Aspire-prefixed mapping exists. It is a single raw URL
or directory path, not the complete mapping policy.

Typical values are nuget.org for stable, the appropriate daily or staging feed,
and a local package directory for PR/local builds. Package-directory and NuGet
service-index identity overrides are honored.

Source projection and opted-in NuGet policy resolve against the same CLI identity
used for version injection, using `PackagingService.GetChannelsAsync` without
querying package feeds. A missing matching channel or missing channel mappings
causes an error before generation when source projection or policy was requested;
version injection alone does not require channel resolution. The flow does not
silently choose an unrelated channel.

Templates own encoding the raw source value for their output format. For a
double-quoted XML attribute, the engine's `xmlEncode` form must be combined with
quote escaping:

```json
{
  "symbols": {
    "AspireNuGetSource": {
      "type": "parameter",
      "datatype": "string",
      "defaultValue": "https://api.nuget.org/v3/index.json"
    },
    "aspireNuGetSourceXml": {
      "type": "derived",
      "valueSource": "AspireNuGetSource",
      "valueTransform": "xmlAttribute",
      "replaces": "{{aspireNuGetSourceXml}}"
    }
  },
  "forms": {
    "xmlQuotes": {
      "identifier": "replace",
      "pattern": "\"",
      "replacement": "&quot;"
    },
    "xmlAttribute": {
      "identifier": "chain",
      "steps": ["xmlEncode", "xmlQuotes"]
    }
  }
}
```

This parameter remains useful for templates that intentionally generate their
own configuration. It is not required for automatic missing-config creation,
which uses the full resolved channel mappings instead.

## NuGet configuration ownership

There are two distinct ownership models:

- **Template-owned:** the engine emits a root `nuget.config`; the CLI preserves
  the generated file instead of merging its own policy into it.
- **CLI-created:** the selected host file sets `createNuGetConfig: true`, no root
  config exists, and the resolved policy requires custom sources; the CLI creates
  the missing file.

For directory and Git templates, `TemplateNuGetConfigService.CreateNuGetConfigIfMissingAsync`
checks actual resolved sources, not version suffixes or the channel name alone.
It creates a missing config only when a mapping uses a source other than the default nuget.org source.
A stable-shaped version can require a staging feed, and a stable-named identity
can have an explicit local-package or service-index override.

This policy is isolated from built-in scaffolding. Existing `aspire new`, `aspire init`,
and `aspire update` callers retain the channel-based `PackageChannel.ShouldCreateNuGetConfig()`
decision, including stable identities that use custom sources. Both paths reuse the
existing configuration builder and writer.

### Scenario matrix

The automatic-creation rows assume the selected host file sets
`createNuGetConfig: true`. This does not require an `AspireVersion` parameter.

| Scenario | Missing output-root config | Existing template-generated output-root config |
| --- | --- | --- |
| Released stable using normal nuget.org sources | Leave absent; use ambient configuration | Preserve |
| Public preview whose resolved sources are only nuget.org | Leave absent; prerelease status alone is not a reason to create a file | Preserve |
| Daily feed | Create the required source policy | Preserve |
| Staging feed, including stable-shaped versions | Create source policy and applicable channel package-cache settings | Preserve |
| PR/local package directory | Create policy using the resolved directory | Preserve |
| Stable identity with an explicit custom package source | Create policy for the override | Preserve |
| `createNuGetConfig` absent or false, even with `AspireVersion` | Do not automatically create a config | Preserve |
| Opted in without an `AspireVersion` parameter | Apply the same channel-based creation policy | Preserve |
| Required identity channel or mappings unavailable | Fail before generation | Fail before generation when source projection or policy requires that channel |
| Required configuration cannot be written | Fail the command without reporting successful creation | No CLI rewrite is attempted |

Existing root filenames are recognized case-insensitively, and preserved
byte-for-byte, including formatting and package-cache settings. Ambiguous root
files that differ only by case are rejected by the shared discovery helper.
The CLI does not parse, repair, or validate a template-owned config in this
preservation path.

Automatic creation reuses `BundleNuGetService` and the existing NuGet
configuration hierarchy. It does not construct policy by writing a source URL
into a fixed XML string. Inherited configuration is considered when calculating
the local policy; ancestor and user-level files are not edited. Nested configs
are not edited either and retain their normal NuGet precedence.

Only the missing output-root file is created. A nested config can override that
root policy; creating a root file is not a mechanism for forcing every nested
project to use the same sources.

Credential-bearing source URLs are not persisted by automatic creation. The
existing credential-material check conservatively rejects HTTP(S) URLs with
userinfo, query strings, or fragments. Authentication should use NuGet's normal
authentication configuration rather than credentials embedded in a source URL.
Creation uses exclusive create-new semantics rather than overwriting a file
that appeared after the missing-file check.

### Enterprise integrations sharing the Aspire prefix

An enterprise can publish private `Aspire.Hosting.*` packages alongside public
Aspire packages. The prefix alone does not identify which feed owns an
integration. Such a template can intentionally supply its own config with:

- A public fallback mapping, `*`, to nuget.org.
- A conditional `Aspire*` mapping to the CLI-projected daily, staging, or local
  source.
- Exact private-package mappings to an enterprise feed.

For example, private package IDs could include `Aspire.Hosting.Enterprise.Deployment`,
`Aspire.Provisioning.Enterprise.Deployment`, `Aspire.Hosting.Enterprise.Defaults`,
`Azure.Provisioning.Enterprise.Defaults`, and `Aspire.Hosting.Azure.EnterpriseGateway`.
These are representative fixture names, not a built-in package inventory.

Under [NuGet's mapping precedence](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping#package-pattern-precedence),
exact package IDs outrank prefix patterns, and longer prefixes outrank shorter
prefixes and `*`. Exact private mappings therefore continue to select the
enterprise feed even when a broad `Aspire*` mapping selects a different source.

| Package group | Normal stable source | Separate Aspire source enabled |
| --- | --- | --- |
| Exact enterprise package IDs | Enterprise feed | Enterprise feed |
| Other `Aspire*` packages | Public fallback | CLI-projected Aspire source |
| Other dependencies | Public fallback | Public fallback |

The template owns its conditional source emission and any independent enterprise
version constraints. The CLI neither discovers the private feed from package
names nor rewrites enterprise dependencies to match its own package versions.
Keeping the generated config authoritative avoids removing the exact mappings,
duplicating sources, or replacing enterprise package-cache settings.

## Coverage and maintenance

Unit coverage exercises generated output and effective package routing, not just
whether a source URL appears in XML.

| Tests | Coverage |
| --- | --- |
| [`NewCommandLocalTemplateTests`](../../tests/Aspire.Cli.Tests/Commands/NewCommandLocalTemplateTests.cs) | Discovery, metadata-driven prompts, engine defaults and validation, identity projection, missing-config scenarios, enterprise mappings, Git/cache preservation, and failure reporting |
| [`NewCommandLocalTemplateTests.HostConfiguration`](../../tests/Aspire.Cli.Tests/Commands/NewCommandLocalTemplateTests.HostConfiguration.cs) | Host-file precedence without merging, invalid configuration, all prompt types, generated-port fallbacks, hidden requirements and controllers, and curated prompts from the actual built-in template sources |
| [`PackageChannelTests`](../../tests/Aspire.Cli.Tests/Packaging/PackageChannelTests.cs) | Existing channel-based config-creation decisions, including stable custom sources |
| [`TemplateNuGetConfigServiceTests`](../../tests/Aspire.Cli.Tests/Templating/TemplateNuGetConfigServiceTests.cs) | Independent built-in and template-engine policies, source-based creation, byte-preserving ownership, filename casing, inherited/nested configuration, and idempotence |
| `NewCommandTests`, `InitCommandTests`, and template-factory tests | Existing built-in new/init consumers of shared channel policy |
| `GitClientTests`, `GitCommandRunnerTests`, `TemplateEngineFactoryGitTests`, and `TemplateEngineFactoryGitCheckoutTests` | Ref resolution, exact-commit checkouts, environment isolation, deadlines, and classified failures |
| [`CustomTemplateTests`](../../tests/Aspire.Cli.EndToEnd.Tests/CustomTemplateTests.cs) | Installed native CLI generation without a .NET SDK, including directory, Git, and noninteractive-default scenarios |

NuGet routing assertions use `NuGetTestHelper.GetEligiblePackageSources`, which
evaluates the configuration with NuGet's actual mapping resolver. Enterprise
cases check each private package ID and the public/fallback routes separately.
Verify snapshots cover the generated files; byte-level assertions cover config
preservation.

The focused template and shared-policy regression selection is:

```bash
dotnet test --project tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj --no-launch-profile -- \
  --filter-class "*.NewCommandLocalTemplateTests" \
  --filter-class "*.PackageChannelTests" \
  --filter-class "*.TemplateNuGetConfigServiceTests" \
  --filter-class "*.DotNetTemplateFactoryTests" \
  --filter-class "*.CliTemplateFactoryTests" \
  --filter-class "*.NewCommandTests" \
  --filter-class "*.DotNetAppHostNuGetConfigMergerTests" \
  --filter-class "*.InitCommandTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

Use the repository's restore/build prerequisites before running tests. Filters
are Microsoft.Testing.Platform arguments after `--`, not VSTest `--filter`
expressions.

## Current boundaries

Repository catalogs, centralized template configuration, submodule acquisition,
automatic cache eviction, and arbitrary custom-parameter command-line switches
are not implemented by this flow. Template post-actions and dynamically loaded
template-engine extensions are not enabled.

The generic Git runner is available for other consumers, but sharing its
operations does not itself share checkout-cache ownership. A future consumer
must deliberately define cache lifetime, parsing, validation, and size limits.

Likewise, missing-config creation must not be treated as an existing-config
migration mechanism. Any later feature that merges or migrates template-owned
NuGet configuration needs its own ownership contract and tests preserving
enterprise routing. These are boundaries of the current implementation, not
promises that the additional features already exist.
