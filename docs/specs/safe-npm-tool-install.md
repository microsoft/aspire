# Safe npm Global Tool Installation

## Overview

The Aspire CLI installs the `@playwright/cli` npm package as a global tool during `aspire agent init`. Because this tool runs with the user's full privileges, we must verify its authenticity and provenance before installation. This document describes the verification process, the threat model, and the reasoning behind each step.

## Threat Model

### What we're protecting against

1. **Registry compromise** — An attacker gains write access to the npm registry and publishes a malicious version of `@playwright/cli`
2. **Publish token theft** — An attacker steals a maintainer's npm publish token and publishes a tampered package
3. **Man-in-the-middle** — An attacker intercepts the network request and substitutes a different tarball
4. **Dependency confusion** — A malicious package with a similar name is installed instead of the intended one

### What we're NOT protecting against

- Compromise of the legitimate source repository (`microsoft/playwright-cli`) itself
- Compromise of the GitHub Actions build infrastructure (Sigstore OIDC provider)
- Compromise of the Sigstore transparency log infrastructure
- Malicious code introduced through legitimate dependencies of `@playwright/cli`

### Trust anchors

Our verification chain relies on these trust anchors:

| Trust anchor | What it provides | How it's protected |
|---|---|---|
| **npm registry** | Package metadata, tarball hosting | HTTPS/TLS, npm's infrastructure security |
| **Sigstore (Fulcio + Rekor)** | Cryptographic attestation signatures | Public CA with OIDC federation, append-only transparency log, verified in-process via Sigstore .NET library with TUF trust root |
| **GitHub Actions OIDC** | Builder identity claims in Sigstore certificates | GitHub's infrastructure security |
| **Hardcoded expected values** | Package name, version range, expected source repository | Code review, our own release process |

## Verification Process

### Step 1: Resolve package version and metadata

**Action:** Run `npm view @playwright/cli@{versionRange} version` and `npm view @playwright/cli@{version} dist.integrity` to get the resolved version and the registry's SRI integrity hash. The default version range is `>=0.1.1`, which resolves to the latest published version at or above 0.1.1. This can be overridden to a specific version via the `playwrightCliVersion` configuration key.

**What this establishes:** We know the exact version we intend to install and the hash the registry claims for its tarball.

**Trust basis:** npm registry over HTTPS/TLS.

**Limitations:** If the registry is compromised, both the version and hash could be attacker-controlled. This step alone is insufficient — it only establishes what the registry *claims*.

### Step 2: Check if already installed at a suitable version

**Action:** Run `playwright-cli --version` and compare against the resolved version.

**What this establishes:** Whether installation can be skipped entirely (already up-to-date or newer).

**Trust basis:** The previously-installed binary. If the user's system is compromised, this could be spoofed, but that's outside our threat model.

### Step 3: Verify Sigstore attestation and provenance metadata

**Action:**
1. Fetch the attestation bundle from `https://registry.npmjs.org/-/npm/v1/attestations/@playwright/cli@{version}`
2. Find the attestation with `predicateType: "https://slsa.dev/provenance/v1"` (SLSA Build L3 provenance)
3. Extract the Sigstore bundle from the `bundle` field of the attestation
4. Cryptographically verify the Sigstore bundle using the `SigstoreVerifier` from the [Sigstore .NET library](https://github.com/mitchdenny/sigstore-dotnet), with a `VerificationPolicy` configured for `CertificateIdentity.ForGitHubActions("microsoft", "playwright-cli")`
5. Base64-decode the DSSE envelope payload to extract the in-toto statement
6. Verify the following fields from the provenance predicate:

| Field | Location in payload | Expected value | What it proves |
|---|---|---|---|
| **Source repository** | `predicate.buildDefinition.externalParameters.workflow.repository` | `https://github.com/microsoft/playwright-cli` | The package was built from the legitimate source code |
| **Workflow path** | `predicate.buildDefinition.externalParameters.workflow.path` | `.github/workflows/publish.yml` | The build used the expected CI pipeline, not an ad-hoc or attacker-injected workflow |
| **Build type** | `predicate.buildDefinition.buildType` | `https://slsa-framework.github.io/github-actions-buildtypes/workflow/v1` | The build ran on GitHub Actions, which implicitly confirms the OIDC token issuer is `https://token.actions.githubusercontent.com` |
| **Workflow ref** | `predicate.buildDefinition.externalParameters.workflow.ref` | Validated via caller-provided callback (for `@playwright/cli`: kind=`tags`, name=`v{version}`) | The build was triggered from a version tag matching the package version, not an arbitrary branch or commit. The tag format is package-specific — different packages may use different conventions (e.g., `v0.1.1`, `0.1.1`, `@scope/pkg@0.1.1`). The ref is parsed into structured components (`WorkflowRefInfo`) and the caller provides a validation callback. |

**What this establishes:** That the Sigstore bundle is cryptographically authentic — the signing certificate was issued by Sigstore's Fulcio CA, the signature is recorded in the Rekor transparency log, and the OIDC identity in the certificate matches the `microsoft/playwright-cli` GitHub Actions workflow. Additionally, the provenance metadata confirms the package was built from the expected repository, workflow, CI system, and version tag.

**Trust basis:** Sigstore's public key infrastructure via the `Sigstore` and `Tuf` .NET libraries. The TUF trust root is automatically downloaded and verified. Even if the npm registry is compromised, an attacker cannot forge valid Sigstore signatures — they would need to compromise Fulcio (the Sigstore CA) or obtain a valid OIDC token from GitHub Actions for the legitimate repository's workflow. Since the Sigstore verification and provenance field checking happen on the same attestation bundle in a single operation, there is no TOCTOU gap between signature verification and content inspection.

**Why we verify all provenance fields:** Checking only the Sigstore certificate identity (GitHub Actions + repository) is necessary but not sufficient. An attacker with write access to the repo could introduce a malicious workflow (e.g., `.github/workflows/evil.yml`). By also verifying the workflow path, build type, and workflow ref, we ensure the package was built by the specific expected CI pipeline from a release tag.

**Additional fields extracted but not directly verified:** The provenance parser also extracts `runDetails.builder.id` from the attestation. This is available in the `NpmProvenanceData` result for logging and diagnostics but is not currently used as a verification gate.

### Step 4: Download and verify tarball integrity

**Action:**
1. Run `npm pack @playwright/cli@{version}` to download the tarball
2. Compute SHA-512 hash of the downloaded tarball
3. Compare against the SRI integrity hash obtained in Step 1

**What this establishes:** That the tarball we have on disk is bit-for-bit identical to what the npm registry published for this version.

**Trust basis:** Cryptographic hash comparison (SHA-512). If the hash matches, the content is the same regardless of how it was delivered.

**Relationship to Step 3:** The Sigstore attestations verified in Step 3 are bound to the package version and its published content. The integrity hash in the registry packument is the canonical identifier for the tarball content. By verifying our tarball matches this hash, we establish that our tarball is the same artifact that the Sigstore attestations cover.

### Step 5: Install globally from verified tarball

**Action:** Run `npm install -g {tarballPath}` to install the verified tarball as a global tool.

**What this establishes:** The tool is installed and available on the user's PATH.

**Trust basis:** All preceding verification steps have passed. The tarball content has been verified against the registry's published hash (Step 4), the Sigstore attestations for that content are cryptographically valid (Step 3), and the attestations confirm the correct source repository, workflow, and build system (Step 3).

### Step 6: Generate and mirror skill files

**Action:** Run `playwright-cli install --skills` to generate agent skill files in the primary skill directory (`.claude/skills/playwright-cli/`), then mirror the skill directory to all other detected agent environment skill directories (e.g., `.github/skills/playwright-cli/`, `.opencode/skill/playwright-cli/`). The mirror is a full sync — files are created, updated, and stale files are removed so all environments have identical skill content.

**What this establishes:** The Playwright CLI skill files are available for all configured agent environments.

## Verification Chain Summary

```text
                    ┌──────────────────────────────┐
                    │   Hardcoded expectations      │
                    │   • Package: @playwright/cli  │
                    │   • Version range: >=0.1.1    │
                    │   • Source: microsoft/         │
                    │     playwright-cli             │
                    │   • Workflow: .github/         │
                    │     workflows/publish.yml      │
                    │   • Build type: GitHub Actions │
                    │     workflow/v1                │
                    └──────────────┬────────────────┘
                                   │
                    ┌──────────────▼────────────────┐
                    │  Step 1: Resolve version +     │
                    │  integrity hash from registry  │
                    └──────────────┬────────────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              │                                         │
   ┌──────────▼──────────────┐               ┌─────────▼─────────┐
   │ Step 3: Sigstore verify  │               │ Step 4: npm pack  │
   │ + provenance checks      │               │ + SHA-512 check   │
   │ (in-process via Sigstore │               │ (tarball          │
   │  .NET library + TUF)     │               │  integrity)       │
   └──────────┬───────────────┘               └─────────┬─────────┘
              │                                         │
              │  Attestation is authentic +              │  Tarball matches
              │  built from expected repo +              │  published hash
              │  expected pipeline                       │
              └────────────────────┬────────────────────┘
                                   │
                    ┌──────────────▼────────────────┐
                    │  Step 5: npm install -g        │
                    │  (from verified tarball)        │
                    └───────────────────────────────┘
```

## Residual Risks

### 1. Time-of-check-to-time-of-use (TOCTOU)

**Risk:** The package could be replaced on the registry between our verification steps and the global install.

**Mitigation:** We verify the SHA-512 hash of the tarball we actually install (Step 4), and we install from the local tarball file (not from the registry again). The verified tarball is the same file that gets installed.

### 2. Transitive dependency attacks

**Risk:** `@playwright/cli` has dependencies that could be compromised.

**Mitigation:** The `--ignore-scripts` flag prevents execution of install scripts. However, the dependencies' code runs when the tool is invoked. This is partially mitigated by Sigstore attestations covering the dependency tree, but comprehensive supply chain verification of all transitive dependencies is out of scope.

## Implementation Constants

```csharp
internal const string PackageName = "@playwright/cli";
internal const string VersionRange = ">=0.1.1";
internal const string ExpectedSourceRepository = "https://github.com/microsoft/playwright-cli";
internal const string ExpectedWorkflowPath = ".github/workflows/publish.yml";
internal const string ExpectedBuildType = "https://slsa-framework.github.io/github-actions-buildtypes/workflow/v1";
internal const string NpmRegistryAttestationsBaseUrl = "https://registry.npmjs.org/-/npm/v1/attestations";
internal const string SlsaProvenancePredicateType = "https://slsa.dev/provenance/v1";
```

## Configuration

Two break-glass configuration keys are available via `aspire config set`:

| Key | Effect |
|---|---|
| `disablePlaywrightCliPackageValidation` | When `"true"`, skips all Sigstore, provenance, and integrity checks. Use only for debugging npm service issues. |
| `playwrightCliVersion` | When set, overrides the version range and pins to the specified exact version. |

## Future Improvements

1. **Pinned tarball hash** — Ship a known-good SRI hash with each Aspire release, eliminating the need to trust the registry for the hash at all.
