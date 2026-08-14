# Rust AppHost Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Rust AppHosts the same applicable editor, runtime, scaffold, and telemetry behavior as C# and TypeScript AppHosts.

**Architecture:** Add one Rust implementation of the existing `AppHostResourceParser` contract so CodeLens, gutter decorations, and open-file tracking inherit support together. Make generated `apphost.rs` the canonical Cargo binary, forward Aspire arguments after Cargo's `--` separator, and extend existing coarse telemetry classifiers with a Rust family.

**Tech Stack:** TypeScript, VS Code extension API, Mocha, C# 13, xUnit v3, Microsoft.Testing.Platform, Cargo.

---

### Task 1: Rust AppHost editor parser

**Consumed by:** Task 2 — CodeLens, gutter, and open-file tests consume the registered parser.

**Files:**
- Create: `extension/src/editor/parsers/rustAppHostParser.ts`
- Modify: `extension/src/editor/parsers/AppHostResourceParser.ts`
- Modify: `extension/src/editor/AspireCodeLensProvider.ts`
- Modify: `extension/src/editor/AspireGutterDecorationProvider.ts`
- Modify: `extension/src/test/parsers.test.ts`

- [ ] **Step 1: Add failing registry and parser tests**

Add the Rust parser import, map `.rs` mock documents to `languageId: 'rust'`, assert `getSupportedLanguageIds()` contains `rust`, and add a `RustAppHostParser` suite covering:

```ts
const appHost = [
    '#[path = ".aspire/modules/mod.rs"]',
    'mod aspire;',
    'use aspire::*;',
    'fn main() -> Result<(), Box<dyn std::error::Error>> {',
    '    let builder = create_builder(None)?;',
    '    let cache = builder.add_redis("cache")?',
    '        .with_data_volume(None)?;',
    '    builder.add_step("publish")?;',
    '    Ok(())',
    '}',
].join('\n');
```

Assert detection, builder line, resource name/method/range/statement start, `add_step` classification, multiple calls, raw and escaped string decoding, and rejection of calls found only in line comments, nested block comments, normal strings, raw strings, or malformed input.

- [ ] **Step 2: Run the parser tests and verify they fail**

Run:

```bash
cd extension
yarn run compile-tests
yarn run unit-test --grep "AppHostResourceParser registry|RustAppHostParser"
```

Expected: failures because no Rust parser or Rust language ID is registered.

- [ ] **Step 3: Implement the Rust parser**

Create a self-registering parser with this public shape:

```ts
class RustAppHostParser implements AppHostResourceParser {
    getSupportedExtensions(): string[] {
        return ['.rs'];
    }

    async isAppHostFile(document: vscode.TextDocument): Promise<boolean> {
        return scanRust(document.getText()).calls.some(call => call.methodName === 'create_builder');
    }

    async parseResources(document: vscode.TextDocument): Promise<ParsedResource[]> {
        return scanRust(document.getText()).calls
            .filter(call => /^add_[a-zA-Z0-9_]+$/.test(call.methodName) && call.stringArgument !== undefined)
            .map(call => toParsedResource(document, call));
    }

    async findBuilderStatementLine(document: vscode.TextDocument): Promise<number | undefined> {
        const call = scanRust(document.getText()).calls.find(candidate => candidate.methodName === 'create_builder');
        return call === undefined ? undefined : document.positionAt(call.statementStart).line;
    }
}

registerParser(new RustAppHostParser());
```

Implement a single-pass scanner that skips whitespace, `//` comments, nested `/* */` comments, character literals, byte literals, normal/byte strings, and raw strings; emits identifiers and punctuation; recognizes identifier call expressions; parses only a closed first string argument; and tracks the start offset after the previous semicolon or opening brace. Preserve the call start through the first string argument as the `vscode.Range`. Decode normal Rust string escapes and retain raw-string contents.

Add `.rs -> rust` in `extensionToLanguageId`, and statically import the parser beside the C# and JS/TS parser imports in both editor providers.

- [ ] **Step 4: Run parser tests and lint**

Run:

```bash
cd extension
yarn run compile-tests
yarn run unit-test --grep "AppHostResourceParser registry|RustAppHostParser"
yarn run lint
```

Expected: focused tests pass and ESLint reports no errors.

- [ ] **Step 5: Commit**

```bash
git add extension/src/editor/parsers/rustAppHostParser.ts extension/src/editor/parsers/AppHostResourceParser.ts extension/src/editor/AspireCodeLensProvider.ts extension/src/editor/AspireGutterDecorationProvider.ts extension/src/test/parsers.test.ts
git commit -m "Add Rust AppHost editor parser"
```

### Task 2: Rust CodeLens, gutter, and open-file behavior

**Consumed by:** nothing

**Files:**
- Modify: `extension/src/test/aspireCodeLensProvider.test.ts`
- Modify: `extension/src/test/aspireGutterDecorationProvider.test.ts`
- Modify: `extension/src/test/appHostFilePresenceWatcher.test.ts`

- [ ] **Step 1: Add focused Rust consumer tests**

Use an `apphost.rs` document containing `create_builder(None)?`, `builder.add_redis("cache")?`, and `builder.add_step("publish")?`.

Assert:

```ts
assert.ok(lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensResourceAction'));
assert.ok(lenses.some(lens => lens.command?.command === 'aspire-vscode.codeLensDebugPipelineStep'));
assert.deepStrictEqual(reportedPaths(setOpenSpy.firstCall), [fsPath('/test/apphost.rs')]);
```

For gutter decorations, provide a running `cache` resource and assert the decoration range starts on the Rust `add_redis` line.

- [ ] **Step 2: Run focused consumer tests**

Run:

```bash
cd extension
yarn run compile-tests
yarn run unit-test --grep "AspireCodeLensProvider|AspireGutterDecorationProvider|AppHostFilePresenceWatcher"
```

Expected: all focused consumer tests pass through the parser added in Task 1.

- [ ] **Step 3: Commit**

```bash
git add extension/src/test/aspireCodeLensProvider.test.ts extension/src/test/aspireGutterDecorationProvider.test.ts extension/src/test/appHostFilePresenceWatcher.test.ts
git commit -m "Test Rust AppHost editor features"
```

### Task 3: Canonical Rust scaffold and runtime arguments

**Consumed by:** nothing

**Files:**
- Modify: `src/Aspire.Hosting.CodeGeneration.Rust/RustLanguageSupport.cs`
- Modify: `tests/Aspire.Hosting.CodeGeneration.Rust.Tests/RustLanguageSupportTests.cs`

- [ ] **Step 1: Update tests for the canonical source and Cargo separator**

Change the scaffold file assertion to exactly:

```csharp
Assert.Collection(
    files.Keys.Order(StringComparer.Ordinal),
    key => Assert.Equal("Cargo.toml", key),
    key => Assert.Equal("apphost.rs", key),
    key => Assert.Equal("apphost.run.json", key));
```

Add assertions that `apphost.rs` contains `#[path = ".aspire/modules/mod.rs"]`, `create_builder(None)?`, and `app.run(None)?`; `Cargo.toml` contains a `[[bin]]` named `apphost` with `path = "apphost.rs"`; and `runtimeSpec.Execute.Args` equals `['run', '--']`.

- [ ] **Step 2: Run the Rust language-support tests and verify they fail**

Run:

```bash
dotnet test --project tests/Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.Rust.Tests.csproj --no-launch-profile -- --filter-class "*.RustLanguageSupportTests" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Expected: scaffold shape and runtime argument assertions fail.

- [ ] **Step 3: Move executable scaffold code to `apphost.rs`**

Write the generated source directly to `files[AppHostFileName]`, change the module path to `.aspire/modules/mod.rs`, remove `src/main.rs`, and add this Cargo binary declaration:

```toml
[[bin]]
name = "apphost"
path = "apphost.rs"
```

Change runtime execution to:

```csharp
Execute = new CommandSpec
{
    Command = "cargo",
    Args = ["run", "--"]
}
```

- [ ] **Step 4: Run Rust language-support tests**

Run the same filtered `dotnet test` command from Step 2.

Expected: all `RustLanguageSupportTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.CodeGeneration.Rust/RustLanguageSupport.cs tests/Aspire.Hosting.CodeGeneration.Rust.Tests/RustLanguageSupportTests.cs
git commit -m "Align Rust AppHost scaffold and runtime"
```

### Task 4: Rust telemetry classification

**Consumed by:** nothing

**Files:**
- Modify: `extension/src/utils/appHostLanguage.ts`
- Modify: `extension/src/utils/appHostTargetVersion.ts`
- Modify: `extension/src/debugger/AspireDebugSession.ts`
- Modify: `extension/src/test/appHostLanguage.test.ts`
- Modify: `extension/src/test/appHostTargetVersion.test.ts`
- Modify: `extension/src/test/aspireDebugSession.test.ts`

- [ ] **Step 1: Add failing Rust classification tests**

Assert Rust candidates summarize to `rust`, C#/Rust and TypeScript/Rust summarize to `polyglot`, `.rs` paths classify as `rust`, a directory containing `apphost.rs` classifies as `rust`, and classification is case-insensitive.

Create `apphost.rs` beside this config and assert the target version is `13.6.0`:

```json
{
  // Guest AppHost SDK selected by the CLI.
  "sdk": { "version": "13.6.0" }
}
```

Extend debug telemetry tests so direct-file and directory Rust launches report `apphost_language: 'rust'`.

- [ ] **Step 2: Run classification tests and verify they fail**

Run:

```bash
cd extension
yarn run compile-tests
yarn run unit-test --grep "appHostLanguage|appHostTargetVersion|Rust.*telemetry"
```

Expected: Rust values are currently `unknown` and direct Rust target version is missing.

- [ ] **Step 3: Implement Rust classification**

Introduce one shared exported type:

```ts
export type AppHostLanguage = 'csharp' | 'typescript' | 'rust' | 'unknown';
export type AppHostLanguageSummary = Exclude<AppHostLanguage, 'unknown'> | 'polyglot' | 'unknown' | 'none';
```

Add `rust` to `languageFamily`, track `sawRust` in summaries, classify `.rs` paths and `apphost.rs` directory markers, and return `polyglot` whenever more than one recognized/other family is present. Use `AppHostLanguage` as the return type of path/directory classification and the debug-session language promise.

Add `.rs` to `isPolyglotAppHostFile` so direct guest AppHosts use adjacent `aspire.config.json`.

- [ ] **Step 4: Run focused tests and lint**

Run:

```bash
cd extension
yarn run compile-tests
yarn run unit-test --grep "appHostLanguage|appHostTargetVersion|Rust.*telemetry"
yarn run lint
```

Expected: focused tests pass and ESLint reports no errors.

- [ ] **Step 5: Commit**

```bash
git add extension/src/utils/appHostLanguage.ts extension/src/utils/appHostTargetVersion.ts extension/src/debugger/AspireDebugSession.ts extension/src/test/appHostLanguage.test.ts extension/src/test/appHostTargetVersion.test.ts extension/src/test/aspireDebugSession.test.ts
git commit -m "Classify Rust AppHosts in telemetry"
```

### Task 5: Integrated verification

**Consumed by:** nothing

**Files:**
- Modify only if verification exposes a defect in the touched scope.

- [ ] **Step 1: Run the full extension test pipeline**

```bash
cd extension
yarn run test
```

Expected: extension compile, lint, and unit tests pass.

- [ ] **Step 2: Run Rust code-generation and hosting tests**

```bash
dotnet test --project tests/Aspire.Hosting.CodeGeneration.Rust.Tests/Aspire.Hosting.CodeGeneration.Rust.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
dotnet test --project tests/Aspire.Hosting.Rust.Tests/Aspire.Hosting.Rust.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Expected: both projects pass.

- [ ] **Step 3: Build the extension and local CLI together**

```bash
cd extension
./build.sh
```

Expected: the Aspire CLI and extension build successfully.

- [ ] **Step 4: Verify the final diff**

```bash
git diff --check
git status --short
git --no-pager diff HEAD~4 --stat
```

Expected: no whitespace errors; only the parity changes plus the pre-existing local XLF/settings files remain uncommitted.
