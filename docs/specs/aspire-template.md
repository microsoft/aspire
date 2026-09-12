# Git-based Aspire project templates

> **Status:** Draft for design and security review.
>
> **Audience:** Aspire CLI and template implementers, and security reviewers of the
> acquisition and provenance design.
>
> This document is not a complete threat model. It records the intended security
> properties, trust decisions, failure behavior, and residual risks so that a threat
> model review can focus on the unresolved questions.

## Decision summary

The next major Aspire release should move `aspire new` from a single
NuGet-distributed template pack to an explicit, machine-global list of Git template
sources.

The design is based on these decisions:

1. Templates use the existing .NET runnable-project format:
   `.template.config/template.json`. Aspire documents and guarantees a smaller
   authoring profile rather than creating another template language.
2. A template source is added explicitly. `aspire new` reads only a local cache and
   never searches or refreshes remote sources as part of normal project creation.
3. The CLI ships with a built-in source pointing at the proposed
   `https://github.com/microsoft/aspire-templates` repository.
4. `aspire template update` downloads, verifies, validates, and atomically activates
   new source content. A failed update leaves the last-known-good cache usable.
5. Attested acquisition is intended primarily for public GitHub templates and uses
   Sigstore's public-good instance. The preferred distribution is a fixed-name release
   archive accompanied by its Sigstore bundle. Once a compatible release selector is
   known, both can be downloaded through direct GitHub release-asset URLs without
   using a rate-limited REST API.
6. The official source has a trust policy compiled into the CLI, including the
   repository and release workflow identity.
7. For a third-party attested source, the first successful verification displays the
   observed identity and asks the user to trust it. That identity becomes the
   machine-global policy for later updates. An update fails closed if the identity
   changes.
8. An arbitrary Git repository can still be used without Sigstore infrastructure.
   The user must explicitly accept that the source is unverified, and the CLI records
   the resolved commit for diagnostics.
9. Source configuration, learned trust, and cached templates are per-user,
   machine-global state. They are not project settings because `aspire new` commonly
   runs before a project or repository exists.
10. The Native AOT CLI does not link the reflection-heavy .NET template engine. It
    invokes a .NET SDK template host against Aspire-owned contexts isolated by source
    and SDK/template-engine version.

Several details remain proposals requiring review: rollback protection, the
supported isolated template-host contract, and version compatibility between a CLI
and an independently updated source.

## Motivation

### Product goal

An Aspire application that already builds and runs should be close to being a
shareable template. A developer should be able to:

1. Put runnable Aspire code in a Git repository.
2. Add a small `.template.config/template.json` that describes project-name and
   optional parameter replacements.
3. Publish or share that repository.
4. Let another developer add the source, inspect its origin, cache it, and create a
   new application from it.

This should make it practical for the ecosystem to share and fork starter
applications without first learning NuGet packaging or converting runnable code into
a separate tokenized source tree.

The .NET template engine calls this a "runnable project" template. The source remains
normal code that can be built, run, debugged, and tested. Template-specific behavior
is declared beside it.

### Why move acquisition away from NuGet

`Aspire.ProjectTemplates` currently combines three concerns:

- template authoring;
- template distribution through NuGet; and
- template selection tied to the Aspire CLI's package channel and version.

Reusing `template.json` is a format choice, not a requirement that template authors
adopt the .NET toolchain. A TypeScript, Python, or .NET developer should be able to
turn runnable Aspire code into a template and share or fork it through Git without
creating a NuGet package or invoking `dotnet new`. NuGet remains useful as a
compatibility distribution channel for Visual Studio and direct `dotnet new` users,
while Git becomes the ecosystem collaboration boundary. Separating acquisition from
project creation also lets the Aspire CLI establish provenance and cache policy
before template content is used.

## Goals

- Make an existing runnable Aspire application easy to turn into a template.
- Reuse the .NET template format and execution behavior.
- Support repositories containing one or many templates.
- Support a first-party source with a non-interactive, compiled trust policy.
- Support third-party attested sources with explicit trust-on-first-use enrollment.
- Support arbitrary Git hosts and repositories that do not publish attestations.
- Avoid a network dependency during normal `aspire new` execution.
- Preserve a usable last-known-good cache when acquisition or verification fails.
- Keep the Aspire CLI Native AOT compatible.
- Preserve current Aspire template short names and project-creation behavior during
  migration.

## Non-goals

- Building a public search service or NuGet-like template marketplace.
- Searching GitHub, NuGet, or configured repositories during every `aspire new`.
- Claiming that provenance makes template content safe.
- Sandboxing generated code, `dotnet restore`, `dotnet build`, or `dotnet run`.
- Reimplementing the complete .NET template engine in the Aspire CLI.
- Requiring Sigstore infrastructure for every source.
- Operating an Aspire-specific TUF repository or private Sigstore trust root.
- Supporting private GitHub release attestations or GitHub Enterprise Server in the
  first version. Generic authenticated Git remains possible through the user's Git
  credential configuration.
- Removing `Aspire.ProjectTemplates` immediately. It remains available during a
  compatibility window for existing `dotnet new` and Visual Studio consumers.

## Terminology

| Term | Meaning |
|---|---|
| **Source** | A configured Git repository plus acquisition, reference, and trust policy. |
| **Source name** | A machine-local alias used by CLI commands, such as `official` or `contoso`. |
| **Template** | A directory containing `.template.config/template.json` and the runnable source copied by the .NET template engine. |
| **Release archive** | A fixed-name `.tar.gz` GitHub release asset containing one or more templates. |
| **Bundle** | A Sigstore bundle whose signed subject digest identifies the release archive. |
| **Trust policy** | Certificate-backed identity claims and signed-statement constraints that later updates must match. |
| **Last-known-good cache** | The most recently verified, validated, and activated content for a source. |
| **Unverified Git source** | Content resolved through Git without an attestation understood by Aspire. HTTPS or SSH transport authentication is not build provenance. |

## User experience

The exact command names are proposed, but the separation between source management,
network update, and offline creation is intentional.

### Built-in source

A new installation contains a source record named `official` for the proposed
`microsoft/aspire-templates` repository. The source record contains the acquisition
mode, fixed release asset names, and compiled trust-policy identifier. It does not
silently fetch on every CLI invocation.

If no official cache exists yet, an interactive `aspire new` may offer to run the
equivalent of `aspire template update official`. A non-interactive invocation fails
with an actionable message instead of starting an implicit network operation.

### Add a source

```bash
aspire template sources add \
  --name contoso \
  --repo https://github.com/contoso/aspire-templates
```

For a public GitHub repository, `auto` acquisition first probes the conventional
release-asset URLs. If the archive and attestation exist, the CLI verifies them and
starts trust enrollment. If no supported release is available, the CLI explains the
fallback and requires explicit consent before configuring an unverified Git source.

The user can request Git acquisition directly:

```bash
aspire template sources add \
  --name local-team \
  --repo https://git.example.com/platform/aspire-templates.git \
  --ref refs/heads/main \
  --allow-unverified
```

An exact commit is also accepted:

```bash
aspire template sources add \
  --name experiment \
  --repo https://github.com/contoso/aspire-templates.git \
  --ref 8f2d8e7c0f6c4f98c391dc718f860f45b20d45d1 \
  --allow-unverified
```

Adding a public GitHub source offers to download and verify a candidate immediately.
After verification, the CLI extracts the certificate identity and signed-statement
constraints, displays the exact observed values and the checks it proposes to retain,
and asks the user whether to trust them. Trust and content are activated only after
confirmation.

`--no-update` skips enrollment and leaves the source configured but unusable until an
interactive update completes it. First-time enrollment of a third-party attested
source is not available non-interactively.

### Inspect sources

```bash
aspire template sources list
aspire template sources show contoso
```

Output includes:

- repository URL and requested ref;
- acquisition and verification mode;
- last attempted and last successful update;
- current archive digest or resolved Git commit;
- trusted builder identity for attested sources;
- whether a usable cache exists; and
- the templates discovered in that cache.

The output must distinguish `verified`, `unverified`, `stale`, and `update failed`.
It must not describe a Git commit hash by itself as verified provenance.

### Update cached templates

```bash
aspire template update
aspire template update contoso
```

An update performs all network activity, provenance verification, safe extraction,
manifest validation, index generation, and activation. It does not mutate trust
policy. If a new identity is observed, update fails and directs the user to an
explicit trust command.

Updating all sources continues past an individual source failure and reports an
aggregate result. A source failure never deletes its last-known-good content.

### Enroll or change trust explicitly

```bash
aspire template sources trust contoso
```

The command downloads and verifies a candidate, displays the old and new identities
side by side, and requires confirmation before replacing policy. It is separate from
`update` so that routine refresh cannot normalize unexpected identity drift.

The same flow runs during the first update of an unenrolled source. It first verifies
the bundle cryptographically and confirms that its source repository matches
`--repo`; only then does it display the extracted values and ask for trust.

There is no user-authored trust-policy file, expected-value argument set, or
non-interactive `--yes` bypass. Automation may update a source only after trust has
already been established interactively on that machine. The built-in official source
is the exception because its expected identity is compiled into the CLI.

### Remove a source

```bash
aspire template sources remove contoso
```

Removal displays the source, trust policy, and cached digest that will be removed.
The official source may be disabled or reset but is not silently replaced with a
different repository.

### Create from the cache

```bash
aspire new contoso-app
```

`aspire new` uses the active local index and content only. It does not check releases,
query attestations, fetch Git refs, or refresh Sigstore trust metadata.

If two enabled sources expose the same `shortName`, Aspire does not choose one by
hidden precedence. The user must qualify it:

```bash
aspire new contoso-app --template-source contoso
```

The existing `aspire new --source` option currently means a NuGet package source.
Migration should either rename that option or introduce the unambiguous
`--template-source` spelling before the Git source feature ships.

### Offline behavior

- `aspire new` works offline whenever its selected source has an active cache.
- `aspire template update` reports a network failure and preserves the cache.
- A source with no cache cannot be used offline.
- An expired or unavailable Sigstore public-good trust-root update does not
  invalidate content that was already verified and activated. It prevents activation
  of new content until verification can complete.

## Machine-global state

Template sources belong to the Aspire user profile rather than a project. The
conceptual root is `<ASPIRE_HOME>/templates`, normally `~/.aspire/templates`.

```text
<ASPIRE_HOME>/
|-- aspire.config.json
`-- templates/
    |-- sources.json
    |-- index.json
    |-- content/
    |   `-- <source-id>/
    |       |-- <archive-digest-or-commit>/
    |       `-- current
    |-- hives/
    |   `-- <source-id>/<sdk-or-engine-version>/
    `-- staging/
```

The exact serialization can change during implementation, but the following
properties are required:

- Source and trust records are updated atomically.
- Cached content is immutable after activation.
- Activation writes a per-file SHA-256 manifest for the extracted regular files.
- The active pointer changes only after every verification and validation succeeds.
- At least the current cache survives garbage collection. Keeping the immediately
  previous cache is recommended for diagnostics and rollback.
- File permissions or ACLs limit writes to the current user.
- No credentials or GitHub tokens are persisted in source records.

A conceptual source record is:

```json
{
  "name": "contoso",
  "repository": "https://github.com/contoso/aspire-templates",
  "requestedRef": null,
  "acquisition": "github-release",
  "verification": "sigstore-tofu",
  "releaseSelector": {
    "kind": "latest"
  },
  "releaseAssets": {
    "archive": "aspire-templates.tar.gz",
    "bundle": "aspire-templates.sigstore.json"
  },
  "trustedIdentity": {
    "sourceRepositoryUri": "https://github.com/contoso/aspire-templates",
    "sourceRepositoryId": "123456789",
    "sourceOwnerUri": "https://github.com/contoso",
    "sourceOwnerId": "987654",
    "certificateIssuer": "https://token.actions.githubusercontent.com",
    "sourceRefPolicy": "refs/tags/v*",
    "buildSigner": {
      "repository": "https://github.com/contoso/aspire-templates",
      "workflowPath": ".github/workflows/release.yml",
      "workflowRefPolicy": "refs/tags/v*"
    },
    "buildConfig": {
      "repository": "https://github.com/contoso/aspire-templates",
      "workflowPath": ".github/workflows/release.yml",
      "workflowRefPolicy": "refs/tags/v*"
    },
    "runnerEnvironment": "github-hosted",
    "predicateType": "https://slsa.dev/provenance/v1"
  }
}
```

This JSON is illustrative, not a committed configuration schema.

## Acquisition modes

| Mode | Content | Provenance | Network characteristics |
|---|---|---|---|
| **GitHub release with sidecar bundle** | Fixed-name release archive at a selected tag or tracking endpoint | Sigstore bundle downloaded beside it | Two ordinary asset downloads; no REST request once the selector is known |
| **GitHub release with attestation API fallback** | Fixed-name release archive | Bundle queried by downloaded archive digest | Asset download plus at least one rate-limited REST request |
| **Git snapshot** | Tree at a branch, tag, or commit | None understood by Aspire | Uses the installed `git` client and its credential helpers |

Acquisition mode is stored explicitly after enrollment. Update does not silently
downgrade an attested source to unverified Git content.

The attested modes are for public `github.com` repositories and use Sigstore's
public-good instance. Private and other authenticated repositories use the explicit
unverified Git mode in the first version.

### Verified GitHub release

The preferred repository publishes these fixed-name assets:

```text
aspire-templates.tar.gz
aspire-templates.sigstore.json
```

For an exact release, the CLI downloads them from:

```text
https://github.com/<owner>/<repo>/releases/download/<tag>/aspire-templates.tar.gz
https://github.com/<owner>/<repo>/releases/download/<tag>/aspire-templates.sigstore.json
```

A source that explicitly tracks the repository's global latest release may instead
use:

```text
https://github.com/<owner>/<repo>/releases/latest/download/<asset>
```

GitHub documents both direct release downloads and this fixed latest-asset URL shape.
The requests redirect to GitHub's release-asset CDN and do not consume the REST API's
unauthenticated 60-request hourly budget. General availability and abuse controls
still apply.

The official source must not use global `latest` until its CLI compatibility policy
is defined. A direct asset URL avoids REST only after Aspire knows which release or
compatibility channel to request. Possible selectors include an exact release tag or
a major-specific asset maintained on a tracking release.

Attested GitHub release mode accepts a canonical
`https://github.com/<owner>/<repository>` URL, not an arbitrary asset URL. Aspire
constructs asset URLs from validated path components, requires TLS, bounds redirect
count, and follows redirects only to GitHub-controlled release-asset hosts. The
public first version sends no credentials and never forwards authentication headers
across a redirect.

The bundle is not trusted because it came from the same release page. It is trusted
only after local Sigstore verification proves that its signed subject digest matches
the downloaded archive and its certificate identity satisfies policy.

The official repository's release workflow should:

1. Build and test templates from a clean checkout.
2. Resolve all build-time version placeholders.
3. Create an archive containing only releasable template content.
4. Generate SLSA build provenance with `actions/attest`.
5. Upload the archive and the action's `bundle-path` output as fixed-name release
   assets.
6. Protect release tags and, when available, use immutable releases.

The bundle asset may contain one Sigstore bundle or a JSON-lines set of bundles for
workflow-identity rotation. The Sigstore bundle format contains the signature,
certificate, transparency-log material, timestamps, and signed in-toto statement
needed to verify an artifact.

### Attestation API fallback

If the conventional bundle asset is absent, the CLI may query GitHub's public
attestations endpoint using the SHA-256 digest of the archive. Public data can be
queried without authentication, but unauthenticated REST requests share a limit of
60 requests per hour per source IP. Corporate NAT and CI environments can therefore
exhaust the budget even if an individual user makes few requests.

The sidecar bundle is the normal path. The compiled official source requires it and
does not query the attestation API. The REST path is a third-party compatibility
fallback. Rate limiting causes update to fail closed and retain the cache; it never
causes an automatic unverified fallback.

The Aspire CLI should not require the GitHub CLI. `gh attestation verify` remains a
useful diagnostic and an interoperability reference, but production verification is
performed in process so that Aspire controls policy and error reporting.

### Git snapshot

Generic Git support uses the installed `git` executable rather than LibGit2Sharp.
LibGit2Sharp introduces platform-native binaries and Native AOT complications.

Git acquisition invokes the executable directly without a shell and passes every
argument separately. It accepts only reviewed transport forms such as HTTPS, SSH, and
an explicitly requested local path. Git remote-helper forms such as `ext::`, URLs
that can be parsed as command options, and caller-supplied upload-pack commands are
rejected. This prevents a repository value from becoming process arguments or a
local command.

The implementation should avoid a working-tree checkout:

1. Initialize a temporary bare repository.
2. Fetch only the requested ref or commit.
3. Resolve and record the full commit ID.
4. Stream `git archive` output through the same safe archive extractor used for
   release assets.

Git processes have cancellation and time limits. Public-source acquisition disables
interactive credential and host-key prompts so `aspire template update` cannot hang
indefinitely in automation.

This avoids remote repository hooks and checkout-time smudge filters. Submodules and
Git LFS pointer expansion are not supported in the first version; template
repositories must contain the actual files in the selected tree.

Ref behavior is explicit:

- A full commit remains pinned until the source configuration changes.
- A branch or default `HEAD` is expected to move and updates to its latest resolved
  commit.
- A tag is treated as immutable. If it resolves to a different commit later, update
  fails and requires an explicit source edit.

The CLI records both the requested ref and resolved commit. This aids diagnostics but
does not turn an unverified source into an attested one.

## Provenance and trust

### What "verified" means

For an attested release, Aspire uses "verified" only when all of these checks pass:

1. The local archive's SHA-256 digest equals a subject digest in the signed
   attestation.
2. The Sigstore signature and DSSE envelope verify.
3. The signing certificate chains to a currently trusted Sigstore root.
4. Required transparency-log and/or trusted timestamp evidence proves signing
   occurred during certificate validity.
5. The OIDC issuer is GitHub Actions.
6. Certificate-backed repository, owner, workflow, and runner claims satisfy the
   source's trust policy.
7. The signed statement uses an accepted predicate type.
8. The template archive passes extraction and manifest validation.

These checks prove that a GitHub Actions identity signed an assertion whose subject
is the exact archive bytes. They do not independently prove that the workflow built
those bytes from the asserted source, or that the workflow, repository content,
dependencies, or generated application are benign.

The verifier must distinguish certificate-backed claims from ordinary predicate
fields. Predicate content is controlled by the signing workflow and is not promoted
to a trust anchor merely because the envelope is signed.

This interpretation follows the
[Fulcio certificate extension directory](https://github.com/sigstore/fulcio/blob/main/docs/oid-info.md).
The [GitHub CLI attestation verification guidance](https://cli.github.com/manual/gh_attestation_verify)
also distinguishes certificate-backed identity and verified timestamps from
workflow-authored statement predicate data.

### Official source policy

The official source does not use TOFU. Its policy is compiled into the CLI and should
pin the following certificate-backed claims separately:

- the source repository URI,
  `https://github.com/microsoft/aspire-templates`;
- the source repository's immutable GitHub ID;
- the `microsoft` owner's URI and immutable GitHub ID;
- the source-ref policy;
- the build-signer repository, workflow path, and workflow-ref policy;
- the top-level build-config repository, workflow path, and workflow-ref policy;
- GitHub-hosted runner environment policy;
- the GitHub Actions OIDC issuer.

It also requires a SLSA provenance predicate type as a signed-statement constraint.
Predicate type is not a certificate identity claim.

If the signer is a reusable workflow from another repository, signer repository and
top-level build-config repository intentionally differ. A cross-repository reusable
workflow must be referenced by an immutable commit, and policy pins that reference or
signer digest. A branch-based reusable-workflow reference is not accepted for the
official source.

The proposed repository does not exist at the time of this draft, so its immutable
IDs and final workflow path must be filled in before implementation ships.

Changing the official workflow identity requires a CLI release. Rotation should use
an overlap period in which a CLI accepts both old and new identities and the release
pipeline emits an acceptable attestation for both old and new clients. Old clients
that cannot verify the new identity keep their last-known-good cache.

### Third-party trust enrollment

Before a third-party enrollment prompt appears, cryptographic verification and the
configured-source repository match must already have succeeded. A prompt must never
ask the user to trust an identity extracted from an unverified bundle.

Example:

```text
The archive is covered by a valid Sigstore attestation.

Observed certificate evidence:
  Source repository:    https://github.com/contoso/aspire-templates
  Repository ID:        123456789
  Owner:                https://github.com/contoso
  Owner ID:             987654
  Source ref:           refs/tags/v2.1.0
  Build signer URI:     https://github.com/contoso/aspire-templates/
                        .github/workflows/release.yml@refs/tags/v2.1.0
  Build config URI:     https://github.com/contoso/aspire-templates/
                        .github/workflows/release.yml@refs/tags/v2.1.0
  Runner environment:   github-hosted

Observed signed statement:
  Predicate type:       https://slsa.dev/provenance/v1
  Subject digest:       sha256:...

Checks retained for later updates:
  Source refs:          refs/tags/v2.*
  Build signer:         contoso/aspire-templates:
                        .github/workflows/release.yml@refs/tags/v2.*
  Build config:         contoso/aspire-templates:
                        .github/workflows/release.yml@refs/tags/v2.*
  Predicate type:       https://slsa.dev/provenance/v1

Trust this identity for future updates from source 'contoso'? [y/N]
```

The source repository URI observed in the certificate must equal the repository
explicitly configured by the user. The prompt is choosing whether to trust that
repository's observed release identity, not choosing which repository the archive
claims to come from.

Observed concrete refs and proposed future rules are separate. The CLI does not
silently turn one observed tag into `refs/tags/*`. It derives at most a narrow
major-specific candidate such as `refs/tags/v2.*`, displays the broadening, and
requires explicit approval. Confirmation accepts exactly the displayed checks;
declining leaves the source unenrolled. Branch wildcards are not proposed
automatically.

### Stable policy and per-release evidence

The trust record pins stable identity, not values expected to change every release.

| Certificate-backed policy | Signed-statement policy | Verify and record, but do not pin |
|---|---|---|
| Source repository URI and immutable repository ID | Predicate type | Artifact digest |
| Owner URI and immutable owner ID | Required subject-name policy, if any | Source commit digest |
| Approved source-ref rule | Required provenance shape | Workflow run ID |
| Build-signer repository, path, and ref rule |  | Signing and integration timestamps |
| Top-level build-config repository, path, and ref rule |  | Certificate serial number |
| Runner environment and OIDC issuer |  | Ordinary same-repository workflow file digest |
| Immutable cross-repository reusable-workflow reference or digest |  | Release asset redirect URL |

Pinning an ordinary workflow file digest would make every legitimate workflow edit a
trust reset. A reusable workflow intentionally referenced by an immutable commit is a
different case; its digest may be pinned as an additional control.

Repository and owner numeric IDs prevent a deleted or transferred name from being
treated as the original identity without review.

### Identity drift

Routine update never broadens or rewrites policy. Any mismatch produces:

- a failed update;
- a structured diff between expected and observed identity;
- preservation of the current cache; and
- instructions for the explicit trust command.

The diagnostic should identify exactly which field changed. "Attestation verification
failed" is insufficient for a legitimate workflow rotation and insufficiently
specific for an attack.

If several valid attestations cover the same archive, update succeeds when at least
one satisfies the complete policy. An unrelated valid attestation must not compensate
for a failure to match the configured repository and workflow.

A URI change with the same immutable repository ID is reported as a rename and still
requires explicit trust-policy update. The same URI appearing with a different
repository ID is treated as deletion/recreation, not a rename, and requires removing
and re-enrolling the source. An owner-ID change is treated as a repository transfer
and also requires explicit review.

### Sigstore public-good trust-root rotation

Aspire relies on the Sigstore verifier's TUF client for the public-good instance's
Fulcio, Rekor, and timestamp-authority trust-root distribution. It does not implement
independent root cycling, operate a TUF repository, or configure a private Sigstore
trust root. Root metadata is refreshed during source update, not during `aspire new`.

The bootstrap root included by the Sigstore client is a security-sensitive
dependency. Upgrading that dependency and its root must receive the same review as
changing the source trust policy.

The Sigstore public-good TUF repository protects the Sigstore trust-root lifecycle.
It does not provide freshness or rollback protection for the Aspire template release
sequence.

### Rollback and replay

A previously valid archive remains cryptographically valid. An authorized repository
operator may also repoint a mutable "latest" release. Signature verification alone
therefore does not prove that a release is newer than the current cache.

The first version should at minimum:

- record every activated archive digest and attested source commit;
- reject a transition back to a previously activated non-current digest unless the
  user explicitly requests rollback;
- show the current and candidate commit and signing time; and
- retain the last-known-good cache.

This does not prevent first-install rollback or a newly rebuilt old tree. Strong
rollback protection requires a signed, monotonically increasing source version or
another trusted freshness mechanism. Whether to add such metadata to release
artifacts is an open security-review question.

### Unverified Git policy

An unverified source is a supported escape hatch, not a degraded verification result.
Enrollment requires `--allow-unverified` or an equivalent interactive confirmation.
Every list, update, and creation diagnostic identifies it as unverified.

Update must never silently change an attested source to this mode. Conversely, moving
an unverified source to attested releases is an explicit source-policy change followed
by normal trust enrollment.

### Residual risks and boundaries

| Risk | Position |
|---|---|
| Legitimate repository or approved workflow is compromised | Not prevented. Provenance attributes the signed digest assertion to that identity. Protected tags, branch rules, review, and hardened release workflows remain necessary. |
| GitHub Actions OIDC, Sigstore, or trusted roots are compromised | Trust-anchor compromise is outside the protection offered by this design. |
| A valid old release is replayed | Partially detected after a machine has observed it; strong freshness remains open. |
| Template archive exploits a .NET template-engine vulnerability | Not sandboxed. Format validation and using a supported SDK reduce but do not remove this risk. |
| Generated project contains malicious MSBuild, package, or application code | Not prevented. Provenance is origin evidence, not code review. |
| Current user account is compromised | Out of scope. An attacker with the same privileges can alter both cache and policy. |
| Local cache is accidentally modified | Digest/index checks can detect corruption; update restores verified content. |
| Network or GitHub API is unavailable or rate limited | Update fails and the last-known-good cache remains available. |

## Safe download and extraction

Release archives are attacker-controlled input until verification and validation
complete. The updater must:

- stream downloads to a newly created staging directory;
- enforce archive, bundle, expanded-content, manifest, file-count, and individual-file
  size limits;
- verify the archive digest before extraction for attested releases;
- reject absolute paths, drive-qualified paths, `..` traversal, NULs, and paths that
  escape the staging root after normalization;
- reject Windows device names, alternate-data-stream paths, and names that cannot be
  represented consistently on supported file systems;
- reject symlinks, hard links, devices, FIFOs, and other non-regular entries;
- reject case-insensitive and Unicode-normalization path collisions that would alias
  on supported file systems;
- normalize file permissions and never preserve setuid, setgid, or platform-special
  bits;
- avoid overwriting files during extraction;
- validate all discovered manifests before activation; and
- delete only the failed staging directory, never the active content directory.

The extractor should use in-box `System.Formats.Tar` and
`System.IO.Compression.GZipStream`, which are compatible with Native AOT. Security
limits must be constants or documented configuration, not inferred from archive
metadata. JSON readers use bounded depth and source-generated serializers; bundle or
manifest fields are not treated as URLs to fetch.

## Template repository contract

### Repository layout

A repository may contain one or many templates. No Aspire-specific catalog file is
required.

```text
aspire-templates/
|-- templates/
|   |-- contoso-starter/
|   |   |-- .template.config/
|   |   |   |-- template.json
|   |   |   `-- dotnetcli.host.json
|   |   |-- ContosoApp.AppHost/
|   |   |-- ContosoApp.ServiceDefaults/
|   |   `-- ContosoApp.slnx
|   `-- worker-starter/
|       |-- .template.config/
|       |   `-- template.json
|       `-- ...
|-- LICENSE
`-- README.md
```

Update recursively discovers `.template.config/template.json`, validates each
template, and generates an Aspire-owned local index. Release artifacts should contain
only template content and required notices, not the repository's `.git` directory,
CI configuration, build outputs, or dependency caches.

### Template names and collisions

Within a source:

- `identity` values must be unique;
- primary `shortName` values must be unique;
- source aliases must be unique on the machine; and
- a template directory cannot contain another template directory.

Across sources, duplicate short names are allowed only because users may intentionally
fork a source. Each source has a separate template-engine context. The Aspire command
tree registers one subcommand for each distinct short name and associates all source
candidates with it. The unqualified command becomes ambiguous and requires
`--template-source`, which selects the source context before `dotnet new` runs.
Aspire does not let a newly added source silently shadow the official template.

## Aspire authoring profile

Aspire delegates rendering to the .NET template engine, but it owns discovery,
command projection, validation, and security policy. The format is divided into:

- **Required Aspire metadata:** update rejects the template if absent.
- **Supported authoring profile:** Aspire commits to stable CLI projection and tests.
- **Pass-through compatibility:** the selected .NET SDK evaluates it, but Aspire does
  not independently implement or guarantee it across SDK versions.
- **Rejected behavior:** update fails before the source is activated.

### Minimal runnable template

Only `identity`, `name`, and `shortName` are mandatory to the .NET engine. Aspire
requires a little more metadata so it can rename, display, and classify the template.

Starting from a runnable application:

```text
ContosoApp/
|-- .template.config/
|   `-- template.json
|-- ContosoApp.AppHost/
|   |-- ContosoApp.AppHost.csproj
|   `-- AppHost.cs
|-- ContosoApp.ServiceDefaults/
|   `-- ContosoApp.ServiceDefaults.csproj
`-- ContosoApp.slnx
```

the entire manifest can be:

```json
{
  "$schema": "https://json.schemastore.org/template",
  "identity": "Contoso.AspireApp.1.0",
  "name": "Contoso Aspire application",
  "shortName": "contoso-aspire",
  "author": "Contoso",
  "description": "A runnable Contoso Aspire starter application.",
  "sourceName": "ContosoApp",
  "preferNameDirectory": true,
  "classifications": ["Aspire", "Cloud"],
  "tags": {
    "language": "C#",
    "type": "solution"
  }
}
```

Running:

```bash
aspire new contoso-aspire --name Inventory
```

causes the .NET template engine to copy the source and replace `ContosoApp` in
processed file contents, file names, and directory names with `Inventory`. No
`symbols` block, custom generator, or post-action is required.

This minimal path is the primary ecosystem scenario. A template repository's own CI
should instantiate the template and build the result before publishing a release.

### Required metadata

| Field | Aspire requirement |
|---|---|
| `$schema` | Must identify the standard .NET template schema. This is an authoring and validation requirement even though the engine does not require it. |
| `identity` | Non-empty and unique within the source. |
| `name` | User-visible display name. |
| `shortName` | One primary command name. The first version does not project multiple aliases. |
| `author` | Displayed during source and template inspection. |
| `description` | Used for `aspire new` help and selection prompts. |
| `sourceName` | Required for the runnable-app rename scenario. |
| `tags.type` | `project` or `solution`. |

### Supported authoring profile

| Feature | Supported use |
|---|---|
| `sourceName` | Project-name replacement in processed paths and text. |
| `preferNameDirectory`, `defaultName` | Output-directory defaults. |
| `classifications`, `tags.language` | Listing, filtering, and display. |
| `generatorVersions`, `constraints` | Template-engine and host compatibility. |
| `groupIdentity`, `precedence` | Grouping and ordering variants within one source. |
| `thirdPartyNotices` | Attribution and legal-notice link surfaced by source inspection. |
| Additional `tags`, including `editorTreatAs` | Passed to the .NET host and retained in the index when understood. |
| `sources` | Relative source/target mappings, includes, excludes, copy-only patterns, renames, and conditional modifiers contained within template and output roots. |
| `symbols.parameter` | String/text, bool, integer, and single-choice parameters with descriptions, defaults, required state, `replaces`, and `fileRename`. |
| Conditional content | Standard .NET template conditional syntax driven by supported parameters. |
| `guids` | Replacement of solution and project GUID placeholders. |
| `primaryOutputs` | Identification of generated projects and solutions. |
| `dotnetcli.host.json` | Long and short option names, hidden internal parameters, and usage examples. |

Parameter symbols are projected into the `aspire new <template>` command. The local
index stores their names, types, descriptions, choices, and defaults so command
construction remains synchronous and offline. Values are forwarded to `dotnet new`;
Aspire does not perform a second rendering pass.

### Pass-through compatibility

The current nine Aspire templates use substantially more of the engine than the
ecosystem minimum:

- generated `port`, `coalesce`, `regex`, and `switch` symbols;
- computed, derived, and bind symbols;
- custom `forms` chains;
- source modifiers based on host and parameter expressions;
- conditional primary outputs;
- host constraints;
- Visual Studio host metadata; and
- localized template strings.

These features, plus Visual Studio host files and template localization files, are
accepted for migration and evaluated by the selected .NET SDK. Aspire validates
their structural shape and extracts user-facing parameter metadata, but does not
reimplement them. New ecosystem templates should prefer direct `sourceName` and
parameter replacement unless an advanced feature materially improves the generated
result.

Unknown future top-level properties produce a compatibility diagnostic rather than
being silently interpreted by Aspire. Whether they are accepted depends on the
source's declared minimum template-engine version and the installed SDK.

### Post-actions

.NET post-actions can run an arbitrary executable. Provenance does not make that
safe or make it reasonable to execute without notice.

Stock `dotnet new` executes post-actions before returning to Aspire, and
`--no-restore` is only effective when a template declares and maps a compatible
`skipRestore` parameter. Aspire cannot apply a security decision after invoking that
host.

The proposed first-version policy is therefore source-tiered:

| Action | Action ID | Policy |
|---|---|---|
| NuGet restore | `210D431B-A78B-4D2F-B762-4ED3E3EA9025` | Allowed only for the compiled-policy official source in v1. |
| Set Visual Studio startup project | `5BECCC32-4D5A-4476-A0F9-BD2E81AF0689` | Allowed only for the compiled-policy official source in v1; inert under the CLI host. |
| Run script | `3A7C4B45-1F5D-4A30-959A-51B88E82B5D2` | Rejected for every Git source. |
| Any action in a third-party template | Any ID | Rejected in v1. |
| Any other action in the official source | Any other ID | Rejected until explicitly reviewed and allowlisted in the compiled policy. |

Third-party templates must be post-action-free. After generation, Aspire explains
that no restore was performed and the user can review the project before running
`dotnet restore`. Trusting an artifact's builder identity is not consent to execute
the generated project.

This is not a sandbox boundary. `dotnet restore` evaluates project files, accesses
configured package sources, and can participate in broader build-time behavior.
Rejecting third-party post-actions prevents surprise execution during generation; it
does not make later use of unreviewed generated code safe.

A future template-host contract that can enumerate and suppress actions before they
run could permit a controlled restore opt-in. The first version does not depend on
that capability.

The official allowlist also constrains each action's condition and arguments to the
known current shape. Matching an action ID alone is insufficient. A restore action
with unexpected file patterns, a startup action not limited to the Visual Studio
host, or any configuration drift fails source validation.

### Validation

Source update validates the whole source before activation:

- JSON parses and conforms to the supported structural schema.
- Required Aspire metadata is present.
- Identities and short names are unique within the source.
- All template roots and source mappings remain inside extracted content.
- Output mappings cannot escape the selected generation directory.
- Parameter types can be projected by the CLI.
- Post-actions satisfy the allowlist.
- Referenced files and primary outputs use normalized relative paths.
- Templates are compatible with the available .NET SDK host.

Validation reports every manifest error in one pass where practical. A single invalid
template prevents source activation so the local index and installed template context
cannot represent different subsets of a release.

## Execution architecture

### Keep the template engine out of the Native AOT CLI

The .NET template engine uses reflection, component discovery, dynamic assembly
loading, and an extensibility model that is a poor fit for Native AOT. Aspire should
not reference `Microsoft.TemplateEngine.*` from its native CLI executable.

The current CLI already shells out to `dotnet new install` and `dotnet new`. Git
acquisition can continue using that execution boundary. A cached folder is a
supported input to `dotnet new install`, and a folder can contain multiple recursively
discovered templates.

The chosen Sigstore implementation must also pass Native AOT publish and end-to-end
bundle verification tests. Trust-root refresh, certificate parsing, and JSON
serialization cannot be assumed trim-safe merely because the API is managed.

### Isolated template context

Git templates must not be registered in the user's ordinary .NET template context.
The old NuGet pack and new Git source preserve the same short names, so global
registration would make `dotnet new aspire` ambiguous and would couple cache cleanup
to unrelated user state.

The Aspire CLI therefore needs a separate Aspire-owned template context for each
source and SDK/template-engine version under `<ASPIRE_HOME>/templates/hives`.
Per-source isolation permits intentional short-name collisions. Versioning the
context prevents a cache created by one SDK engine from being assumed valid by
another.

The implementation options are:

1. invoke `dotnet new` with an isolated custom hive;
2. add a supported SDK host contract for selecting an alternate hive; or
3. ship a small managed companion host that uses a virtualized template-engine
   configuration while keeping those assemblies out of the Native AOT process.

`--debug:custom-hive` demonstrates the required SDK behavior and is already used by
Aspire template tests, but it is not a documented public contract. Depending on an
unsupported debug switch in the shipped product requires an explicit .NET SDK
agreement. Selecting the supported mechanism is an implementation blocker, not a
reason to modify the user's global hive.

When the selected SDK changes, Aspire rebuilds the matching source context offline
from immutable cached content. A companion host must preserve the `dotnetcli` host
identity, bind symbols such as `HostIdentifier`, host constraints, post-action
policy, and `dotnetcli.host.json` option mappings. Otherwise current templates can
produce different output even when their source bytes are unchanged.

### Local template index

`NewCommand` currently registers template subcommands synchronously during command
construction. Update therefore creates an Aspire-owned index containing:

- source and content identifier;
- template identity, short name, display name, description, author, and language;
- projected parameter definitions;
- template root path;
- trust status; and
- compatibility diagnostics.

The index groups templates by short name. CLI startup registers one command per
distinct short name without network or asynchronous SDK discovery. At execution,
`--template-source` or an unambiguous candidate selects the per-source context.
Before generation, the CLI confirms that the indexed content still exists and that
the selected template is available in the context for the active SDK.

This requires a source-aware, two-phase command parser rather than the current model
of one `TemplateCommand` per `ITemplate`. The first phase parses source-neutral
options and chooses the source candidate. The second phase uses that candidate's
indexed parameters for prompting, help, validation, and forwarding to `dotnet new`.
When duplicate short names have incompatible option schemas, no union of conflicting
`System.CommandLine.Option` instances is created.

### Update sequence

```text
resolve source
    |
    v
download release assets or fetch Git ref into staging
    |
    v
verify provenance policy (or enforce explicit unverified mode)
    |
    v
safe extraction and filesystem validation
    |
    v
discover and validate all template manifests
    |
    v
prepare versioned per-source template context and local index
    |
    v
atomically activate content and index
    |
    v
garbage-collect inactive content later
```

Cancellation and failure at any step before activation remove only staging state.
Activation of content, isolated-host registration, and index must be recoverable as a
single logical transaction.

### Project-creation sequence

```text
read local index
    |
    v
resolve source/template and collect parameters
    |
    v
verify cached file manifest
    |
    v
invoke selected source's isolated dotnet template host
against immutable cached content
    |
    v
write normal Aspire project configuration and report source provenance
```

The generated project may record source name, template identity, archive digest or
Git commit, and generation time for auditability. This metadata is informational; it
does not participate in future source trust or make project configuration
machine-global.

## Migration from `Aspire.ProjectTemplates`

### Dual publishing, not a flag day

The nine templates currently distributed by `Aspire.ProjectTemplates` move to the
proposed template repository, but the release pipeline can continue producing that
NuGet package from the same content during a compatibility window.

| Consumer | During migration |
|---|---|
| New Aspire CLI | Verified Git release in the Aspire-owned template context |
| Older Aspire CLI | Existing NuGet resolution path |
| `dotnet new` | NuGet-installed `Aspire.ProjectTemplates` |
| Visual Studio | NuGet/workload template package until it supports the new source model |

This preserves existing short names and avoids forcing every consumer to migrate at
once.

### Scope of the migration

This work does not automatically migrate every template-like path in `aspire new`.

- `DotNetTemplateFactory` exposes the nine .NET runnable-project templates from
  `Aspire.ProjectTemplates`; those are the initial Git migration set.
- `CliTemplateFactory` exposes ten procedural or embedded-resource templates,
  including TypeScript, Python, Go, Java, and Rust AppHosts/starters. They remain
  compiled CLI templates in v1 unless each is separately converted to the declared
  profile.
- `aspire-test` is a CLI-composed chooser over the xUnit, MSTest, and NUnit
  templates. It remains a CLI command projection rather than a repository manifest.
- `aspire-empty` is the procedural language-selecting CLI template, while `aspire`
  is the current .NET empty-solution template. Their names and prompt behavior must
  not be conflated during migration.

### Preserve names and behavior

The nine Git copies initially preserve:

- `shortName` and `groupIdentity`;
- user-visible parameters;
- output layout and `sourceName`;
- GUID replacement;
- conditional content;
- primary outputs;
- host metadata and localization; and
- the two currently used post-action declarations.

The isolated template context prevents the same short names in the NuGet and Git
distributions from conflicting.

Migration validation must instantiate every current template across its supported
framework and significant option combinations. Existing snapshot and build/run
coverage should execute once against the NuGet artifact and once against the Git
release artifact until output parity is established.

### Resolve placeholders before attestation

`Aspire.ProjectTemplates.csproj` currently replaces version placeholders while
packing. A Git release artifact must perform the equivalent substitutions in the
release workflow before creating and attesting the archive.

The CLI must not rewrite attested template content after verification. Doing so would
make the bytes executed by the template engine differ from the bytes covered by
provenance and would make cache diagnostics misleading.

### Compatibility between CLI and template releases

Independent template updates create a new compatibility problem that NuGet package
version coupling previously avoided. A newer archive may use an SDK feature or Aspire
package version unknown to an older CLI.

The migration needs an explicit policy combining:

- .NET template `generatorVersions` and host `constraints`;
- the CLI's available/private .NET SDK;
- Aspire package/channel version written into generated projects; and
- a source-release compatibility range or channel.

The exact representation remains open. The official CLI must not blindly consume a
global latest release if that release can become incompatible with supported older
CLIs. Options include major-specific assets maintained on a tracking release, a
signed compatibility manifest, or major-specific source endpoints. Until this is
decided, the official source URL is not fixed to `/releases/latest/download`.

### Proposed rollout

1. Create `microsoft/aspire-templates` and move current authoring sources without
   changing output.
2. Add release-time placeholder resolution, template instantiation/build tests, a
   fixed archive, and `actions/attest` bundle publication.
3. Implement source state, safe acquisition, verification, cache activation, and
   inspection commands behind a feature flag.
4. Implement the isolated template-host contract and generate the local command
   index.
5. Add the built-in official source while retaining NuGet as the default.
6. Switch `aspire new` to the verified Git cache for preview users.
7. Make Git the default in the next major release while continuing the NuGet pack for
   older CLI, Visual Studio, and direct `dotnet new` consumers.
8. Remove dual publishing only after those consumers have a replacement and
   supported older CLIs are outside servicing.

## Alternatives considered

### Continue using only NuGet

This preserves existing infrastructure but does not meet the ecosystem authoring and
forking goal. It also keeps template discovery coupled to package feeds and CLI
channel resolution.

### Query remote sources during every `aspire new`

This makes project creation dependent on network availability, rate limits, and
remote compromise at the moment of use. The explicit update and last-known-good model
is more predictable and auditable.

### Download GitHub-generated source archives

Commit archives are convenient but their compressed bytes are not guaranteed to
remain stable. GitHub recommends release assets for stable security-sensitive
artifacts. A purpose-built release archive can also exclude repository-only files and
be directly attested.

### Require attestations for every source

This would exclude internal Git servers and simple community repositories. Explicit
unverified Git mode preserves flexibility without presenting transport or commit
identity as build provenance.

### Operate an Aspire TUF repository

This is rejected. It would make Aspire responsible for an additional metadata
service, key ceremony, and trust-root lifecycle. Attested public templates use the
Sigstore public-good instance and its client-managed TUF roots. Any stronger
template-release freshness mechanism must fit the release contract or existing
distribution systems; it will not introduce an Aspire-operated TUF service.

### Implement a custom template engine

Earlier Aspire spikes explored a custom format and renderer. Reimplementation would
either support too little of the current templates or grow into another template
language. The subprocess boundary keeps the complete .NET engine out of Native AOT
without forking its behavior.

### Link the .NET template engine into the CLI

This provides direct APIs but conflicts with Native AOT because of reflection and
dynamic component loading. A managed companion process is preferable if the
documented `dotnet new` surface cannot provide isolation.

## Open questions

1. **Isolated host:** Which supported .NET SDK contract will give Aspire private,
   source- and SDK-versioned template contexts without relying indefinitely on
   `--debug:custom-hive`?
2. **Controlled restore:** Is automatic restore for the compiled official source
   acceptable, and should a future host that can suppress post-actions offer an
   explicit third-party restore opt-in?
3. **Rollback:** Is local seen-digest protection sufficient for the first version, or
   must the release contract include signed monotonic version metadata?
4. **CLI compatibility:** How does the official source publish updates compatible
   with more than one supported CLI major without making every old CLI track an
   incompatible global latest release?
5. **Private repositories:** Is generic authenticated Git sufficient, given that
   attested acquisition is scoped to public templates using Sigstore's public-good
   instance?
6. **Official identity:** What are the final repository ID, owner ID, source-ref rule,
   build-signer identity, build-config identity, runner policy, and ref policies?
7. **Size limits:** What compressed bytes, expanded bytes, file count, path length,
   and per-file limits support realistic templates without enabling archive abuse?
8. **Template SDK floor:** Which .NET SDK versions must Git-sourced templates support
   in the next major Aspire release?
9. **Source metadata:** Can standard template constraints cover compatibility, or is
   a small signed source-level manifest required?

## Security review guide

The provenance and acquisition review should answer at least these questions:

- Are the official certificate-backed identity claims sufficient and stable across
  the intended release workflow?
- Does third-party TOFU establish the right baseline before any content is activated?
- Can a repository rename, transfer, deletion, or recreation bypass the stored
  identity policy?
- Does every verification failure preserve last-known-good content and avoid a
  verified-to-unverified downgrade?
- Is workflow rotation possible without teaching users to accept unexplained drift?
- Are predicate fields being confused with certificate-backed identity?
- Is rollback protection adequate for first release?
- Can archive parsing, path normalization, file-system aliasing, or extraction limits
  write outside staging or replace active content?
- Can any accepted post-action or template-host feature execute unexpected code
  before the user sees generated output?
- Does the per-source, SDK-versioned template host prevent collisions with and
  modification of the user's normal `dotnet new` state?
- Are Sigstore public-good TUF bootstrap and root updates handled entirely by the
  library, with clear failure behavior?
- Do logs and diagnostics avoid credentials while still exposing source, digest,
  commit, and builder identity needed for incident response?

## References

- [.NET project template tutorial](https://learn.microsoft.com/dotnet/core/tutorials/cli-templates-create-project-template)
- [.NET `template.json` reference](https://github.com/dotnet/templating/blob/main/docs/Reference-for-template.json.md)
- [.NET post-action registry](https://github.com/dotnet/templating/blob/main/docs/Post-Action-Registry.md)
- [GitHub fixed links to latest release assets](https://docs.github.com/repositories/releasing-projects-on-github/linking-to-releases)
- [GitHub REST API rate limits](https://docs.github.com/rest/using-the-rest-api/rate-limits-for-the-rest-api)
- [GitHub artifact attestation REST API](https://docs.github.com/rest/users/attestations)
- [`actions/attest`](https://github.com/actions/attest)
- [`gh attestation verify`](https://cli.github.com/manual/gh_attestation_verify)
- [Sigstore bundle format](https://docs.sigstore.dev/about/bundle/)
- [Fulcio certificate extension directory](https://github.com/sigstore/fulcio/blob/main/docs/oid-info.md)
- [Safe npm global tool installation](safe-npm-tool-install.md)
- [Previous Git template spike, PR #14763](https://github.com/dotnet/aspire/pull/14763)
- [Previous template catalog design, PR #16927](https://github.com/dotnet/aspire/pull/16927)
- [Current template renderer refactor, PR #17788](https://github.com/dotnet/aspire/pull/17788)
