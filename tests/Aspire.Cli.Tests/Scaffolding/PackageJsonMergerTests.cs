// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Scaffolding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Scaffolding;

public class PackageJsonMergerTests
{
    private static string MergeJson(string existing, string scaffold, string toolchainCommand = "npm") =>
        PackageJsonMerger.Merge(existing, scaffold, NullLogger.Instance, toolchainCommand);

    private static JsonObject ParseJson(string json) =>
        JsonNode.Parse(json)!.AsObject();

    private static string GetScript(string mergedJson, string scriptName) =>
        ParseJson(mergedJson)["scripts"]![scriptName]?.GetValue<string>()!;

    private static JsonObject GetScripts(string mergedJson) =>
        ParseJson(mergedJson)["scripts"]!.AsObject();

    private static string? GetDep(string mergedJson, string section, string packageName) =>
        ParseJson(mergedJson)[section]?[packageName]?.GetValue<string>();

    [Fact]
    public void ConflictingScripts_AddedWithAspirePrefix()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite",
                "build": "vite build"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "dev": "aspire run",
                "build": "tsc -p tsconfig.apphost.json",
                "lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // Existing scripts preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());

        // Conflicting scaffold scripts get aspire: prefix
        Assert.Equal("aspire run", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());

        // Non-conflicting scaffold script added directly
        Assert.Equal("eslint apphost.ts", scripts["lint"]?.GetValue<string>());
    }

    [Fact]
    public void NonConflictingScripts_AddedDirectly()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "test": "jest"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "dev": "aspire run",
                "build": "tsc -p tsconfig.apphost.json",
                "lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // Existing preserved
        Assert.Equal("jest", scripts["test"]?.GetValue<string>());

        // All scaffold scripts added directly (no conflicts)
        Assert.Equal("aspire run", scripts["dev"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["build"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["lint"]?.GetValue<string>());
    }

    [Fact]
    public void PrefixedScripts_PreserveExistingValues()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite",
                "build": "vite build",
                "aspire:start": "custom start"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:dev": "tsc --watch -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // Existing preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());

        // Existing aspire: scripts are preserved; missing ones are added
        Assert.Equal("custom start", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());
    }

    [Fact]
    public void ConvenienceAliases_AddedForFreeNames()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite",
                "build": "vite build"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:dev": "tsc --watch -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // "start" and "lint" are not taken — convenience aliases added
        Assert.Equal("npm run aspire:start", scripts["start"]?.GetValue<string>());
        Assert.Equal("npm run aspire:lint", scripts["lint"]?.GetValue<string>());

        // "dev" and "build" are taken — no alias
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());
    }

    [Fact]
    public void ConvenienceAliases_UseConfiguredToolchainCommand()
    {
        var existing = """
            {
              "name": "my-app",
              "packageManager": "yarn@4.9.0",
              "scripts": {
                "dev": "vite"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run"
              }
            }
            """;

        var result = MergeJson(existing, scaffold, toolchainCommand: "yarn");
        var scripts = GetScripts(result);

        Assert.Equal("yarn run aspire:start", scripts["start"]?.GetValue<string>());
    }

    [Fact]
    public void NoAliasWhenNameTaken()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "start": "node server.js",
                "lint": "prettier --check .",
                "dev": "vite",
                "build": "vite build"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:lint": "eslint apphost.ts",
                "aspire:build": "tsc -p tsconfig.apphost.json"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // All existing scripts preserved
        Assert.Equal("node server.js", scripts["start"]?.GetValue<string>());
        Assert.Equal("prettier --check .", scripts["lint"]?.GetValue<string>());
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());

        // Aspire scripts added
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());

        // No convenience aliases — all unprefixed names are taken
        // Verify the existing values weren't overwritten with aliases
        Assert.Equal("node server.js", scripts["start"]?.GetValue<string>());
        Assert.Equal("prettier --check .", scripts["lint"]?.GetValue<string>());
    }

    [Fact]
    public void MixedConflicts_SomeScriptsPrefixedSomeNot()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite",
                "test": "jest"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "dev": "aspire run",
                "build": "tsc -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // Existing preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("jest", scripts["test"]?.GetValue<string>());

        // "dev" conflicted → prefixed
        Assert.Equal("aspire run", scripts["aspire:dev"]?.GetValue<string>());

        // "build" didn't conflict → added directly
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["build"]?.GetValue<string>());

        // "aspire:lint" always added + alias since "lint" is free
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());
        Assert.Equal("npm run aspire:lint", scripts["lint"]?.GetValue<string>());
    }

    [Fact]
    public void Dependencies_SemverAwareMerge()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "express": "^4.18.0"
              },
              "devDependencies": {
                "typescript": "^5.0.0",
                "vite": "^5.0.0"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0",
                "express": "^5.0.0"
              },
              "devDependencies": {
                "typescript": "^5.9.3",
                "@types/node": "^22.0.0",
                "tsx": "^4.21.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Scaffold is newer — upgraded
        Assert.Equal("^5.0.0", GetDep(result, "dependencies", "express"));
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));

        // Not in scaffold — preserved
        Assert.Equal("^5.0.0", GetDep(result, "devDependencies", "vite"));

        // New deps added
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
        Assert.Equal("^22.0.0", GetDep(result, "devDependencies", "@types/node"));
        Assert.Equal("^4.21.0", GetDep(result, "devDependencies", "tsx"));
    }

    [Fact]
    public void PreservesNonScriptProperties()
    {
        var existing = """
            {
              "name": "my-existing-app",
              "version": "3.0.0",
              "description": "My cool app",
              "private": true,
              "type": "module",
              "engines": {
                "node": ">=18"
              }
            }
            """;

        var scaffold = """
            {
              "name": "aspire-apphost",
              "version": "1.0.0",
              "type": "commonjs",
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              },
              "scripts": {
                "aspire:build": "tsc -p tsconfig.apphost.json"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var json = ParseJson(result);

        // Existing scalars preserved
        Assert.Equal("my-existing-app", json["name"]?.GetValue<string>());
        Assert.Equal("3.0.0", json["version"]?.GetValue<string>());
        Assert.Equal("My cool app", json["description"]?.GetValue<string>());
        Assert.True(json["private"]?.GetValue<bool>());
        Assert.Equal("module", json["type"]?.GetValue<string>());

        // engines.node overwritten by scaffold (Aspire requires specific Node versions)
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", json["engines"]?["node"]?.GetValue<string>());

        // Script from scaffold is added
        Assert.Equal("tsc -p tsconfig.apphost.json", GetScript(result, "aspire:build"));
    }

    [Fact]
    public void EmptyExistingContent_ReturnsScaffold()
    {
        var scaffold = """
            {
              "name": "aspire-apphost",
              "scripts": { "dev": "aspire run" }
            }
            """;

        var result = MergeJson("", scaffold);
        Assert.Equal(scaffold, result);

        result = MergeJson("   ", scaffold);
        Assert.Equal(scaffold, result);
    }

    [Fact]
    public void MalformedExistingJson_ReturnsScaffold()
    {
        var scaffold = """
            {
              "name": "aspire-apphost",
              "scripts": { "dev": "aspire run" }
            }
            """;

        var result = MergeJson("not valid json {{{", scaffold);
        Assert.Equal(scaffold, result);
    }

    [Fact]
    public void ExistingJsonWithCommentsAndTrailingCommas_MergesSuccessfully()
    {
        // Real-world package.json files may contain comments and trailing commas
        // even though they're not valid per the JSON spec. We should tolerate them.
        var existing = """
            {
              // This is a comment
              "name": "my-app",
              "version": "1.0.0",
              "scripts": {
                "dev": "vite",
                "build": "vite build", // trailing comma
              },
              "dependencies": {
                "express": "^4.18.0",
              }
            }
            """;

        var scaffold = """
            {
              "scripts": { "aspire:start": "aspire run" },
              "dependencies": { "vscode-jsonrpc": "^8.2.0" }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var json = ParseJson(result);

        // Existing properties preserved (comments and trailing commas are stripped in output)
        Assert.Equal("my-app", json["name"]?.GetValue<string>());
        Assert.Equal("vite", GetScript(result, "dev"));
        Assert.Equal("^4.18.0", GetDep(result, "dependencies", "express"));

        // Scaffold content merged in
        Assert.Equal("aspire run", GetScript(result, "aspire:start"));
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
    }

    [Fact]
    public void Idempotent_MergingTwiceProducesSameResult()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite",
                "build": "vite build"
              },
              "dependencies": {
                "express": "^4.18.0"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var firstMerge = MergeJson(existing, scaffold);
        var secondMerge = MergeJson(firstMerge, scaffold);

        // Parsing both to compare structurally (avoid whitespace differences)
        var first = ParseJson(firstMerge);
        var second = ParseJson(secondMerge);

        Assert.Equal(
            first.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            second.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void NoExistingScripts_ScaffoldScriptsAddedDirectly()
    {
        var existing = """
            {
              "name": "my-app",
              "version": "1.0.0"
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "dev": "aspire run",
                "build": "tsc -p tsconfig.apphost.json",
                "lint": "eslint apphost.ts"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var scripts = GetScripts(result);

        // All scripts added directly (no existing scripts to conflict)
        Assert.Equal("aspire run", scripts["dev"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["build"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["lint"]?.GetValue<string>());
    }

    [Fact]
    public void StaleServer_AllScriptsPreservedUnderAspirePrefix()
    {
        // Simulates the exact scenario: stale server sends non-prefixed scripts,
        // brownfield project already has dev/build/lint
        var existing = """
            {
              "name": "vite-project",
              "version": "1.0.0",
              "type": "module",
              "scripts": {
                "dev": "vite",
                "build": "vite build",
                "lint": "eslint . --ext .ts,.tsx",
                "preview": "vite preview"
              },
              "dependencies": {
                "react": "^18.2.0"
              },
              "devDependencies": {
                "typescript": "^5.2.0",
                "vite": "^5.0.0"
              }
            }
            """;

        var staleScaffold = """
            {
              "name": "aspire-apphost",
              "version": "1.0.0",
              "type": "module",
              "scripts": {
                "lint": "eslint apphost.ts",
                "dev": "aspire run",
                "build": "tsc -p tsconfig.apphost.json",
                "watch": "tsc --watch -p tsconfig.apphost.json"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "devDependencies": {
                "@types/node": "^22.0.0",
                "tsx": "^4.21.0",
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, staleScaffold);
        var json = ParseJson(result);
        var scripts = json["scripts"]!.AsObject();

        // Existing project identity preserved
        Assert.Equal("vite-project", json["name"]?.GetValue<string>());
        Assert.Equal("1.0.0", json["version"]?.GetValue<string>());

        // Existing scripts preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());
        Assert.Equal("eslint . --ext .ts,.tsx", scripts["lint"]?.GetValue<string>());
        Assert.Equal("vite preview", scripts["preview"]?.GetValue<string>());

        // Conflicting scaffold scripts added under aspire: prefix
        Assert.Equal("aspire run", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());

        // Non-conflicting scaffold script added directly
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["watch"]?.GetValue<string>());

        // Existing deps preserved, new deps added, older deps upgraded
        Assert.Equal("^18.2.0", GetDep(result, "dependencies", "react"));
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript")); // upgraded from ^5.2.0
        Assert.Equal("^5.0.0", GetDep(result, "devDependencies", "vite"));
        Assert.Equal("^22.0.0", GetDep(result, "devDependencies", "@types/node"));
        Assert.Equal("^4.21.0", GetDep(result, "devDependencies", "tsx"));
    }

    [Fact]
    public void UpdatedServer_AllScriptsAndAliasesPresent()
    {
        // Simulates the updated server which already sends aspire: prefixed scripts
        var existing = """
            {
              "name": "vite-project",
              "scripts": {
                "dev": "vite",
                "build": "vite build"
              }
            }
            """;

        var updatedScaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:dev": "tsc --watch -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var result = MergeJson(existing, updatedScaffold);
        var scripts = GetScripts(result);

        // Existing preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());

        // All aspire: scripts present
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());

        // Convenience aliases for free names (start, lint not taken)
        Assert.Equal("npm run aspire:start", scripts["start"]?.GetValue<string>());
        Assert.Equal("npm run aspire:lint", scripts["lint"]?.GetValue<string>());

        // No aliases for taken names (dev, build already exist)
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());
    }

    [Fact]
    public void ScriptCommands_PreservedWithFullFidelity()
    {
        // npm scripts commonly use &&, quotes, pipes, and other shell characters.
        // The merger must write them back exactly as they were — no unicode escaping.
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "build": "tsc && vite build",
                "dev": "concurrently \"tsc -w\" \"vite\"",
                "test": "vitest run && echo 'done'",
                "lint": "eslint . --ext .ts,.tsx && prettier --check .",
                "clean": "rm -rf dist && rm -rf node_modules/.cache",
                "start": "node server.js | tee output.log"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:start": "aspire run"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Verify the raw JSON string contains literal &&, ', and | — not unicode escapes
        Assert.Contains("tsc && vite build", result);
        Assert.Contains("vitest run && echo 'done'", result);
        Assert.Contains("eslint . --ext .ts,.tsx && prettier --check .", result);
        Assert.Contains("rm -rf dist && rm -rf node_modules/.cache", result);
        Assert.Contains("node server.js | tee output.log", result);

        // Quotes inside JSON string values are written as \" (valid JSON) — verify via raw string
        Assert.Contains("concurrently \\\"tsc -w\\\" \\\"vite\\\"", result);

        // Must not contain unicode-escaped ampersands or single quotes
        Assert.DoesNotContain("\\u0026", result);
        Assert.DoesNotContain("\\u0027", result);

        // Also verify the parsed values round-trip correctly
        var scripts = GetScripts(result);
        Assert.Equal("tsc && vite build", scripts["build"]?.GetValue<string>());
        Assert.Equal("concurrently \"tsc -w\" \"vite\"", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vitest run && echo 'done'", scripts["test"]?.GetValue<string>());
        Assert.Equal("eslint . --ext .ts,.tsx && prettier --check .", scripts["lint"]?.GetValue<string>());
        Assert.Equal("rm -rf dist && rm -rf node_modules/.cache", scripts["clean"]?.GetValue<string>());
        Assert.Equal("node server.js | tee output.log", scripts["start"]?.GetValue<string>());
    }

    [Fact]
    public void ScaffoldScriptCommands_AlsoPreservedWithFullFidelity()
    {
        // Even scaffold-generated commands with special chars must be written faithfully
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "next dev"
              }
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:lint": "eslint apphost.ts && echo 'lint complete'",
                "aspire:build": "tsc -p tsconfig.apphost.json && echo 'build done'"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        Assert.Contains("eslint apphost.ts && echo 'lint complete'", result);
        Assert.Contains("tsc -p tsconfig.apphost.json && echo 'build done'", result);
        Assert.DoesNotContain("\\u0026", result);
        Assert.DoesNotContain("\\u0027", result);
    }

    [Fact]
    public void Dependencies_ScaffoldNewerVersion_Upgrades()
    {
        var existing = """
            {
              "name": "my-app",
              "devDependencies": {
                "typescript": "^4.0.0"
              }
            }
            """;

        var scaffold = """
            {
              "devDependencies": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));
    }

    [Fact]
    public void Dependencies_ExistingNewerVersion_Preserved()
    {
        var existing = """
            {
              "name": "my-app",
              "devDependencies": {
                "typescript": "^6.0.0"
              }
            }
            """;

        var scaffold = """
            {
              "devDependencies": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        Assert.Equal("^6.0.0", GetDep(result, "devDependencies", "typescript"));
    }

    [Fact]
    public void Dependencies_TildeRange_Compared()
    {
        var existing = """
            {
              "name": "my-app",
              "devDependencies": {
                "typescript": "~5.0.0"
              }
            }
            """;

        var scaffold = """
            {
              "devDependencies": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Scaffold is newer (5.9.3 > 5.0.0), upgrades — entire value replaced including range operator
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));
    }

    [Fact]
    public void Dependencies_UnionRange_Preserved()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "some-pkg": "^1.0.0 || ^2.0.0"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "some-pkg": "^3.0.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Union ranges are unparseable — existing preserved
        Assert.Equal("^1.0.0 || ^2.0.0", GetDep(result, "dependencies", "some-pkg"));
    }

    [Fact]
    public void Dependencies_WorkspaceRef_Preserved()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "shared-lib": "workspace:*"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "shared-lib": "^1.0.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Workspace refs are not parseable as semver — existing preserved
        Assert.Equal("workspace:*", GetDep(result, "dependencies", "shared-lib"));
    }

    [Fact]
    public void Dependencies_NewDependency_Added()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "express": "^4.18.0"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        Assert.Equal("^4.18.0", GetDep(result, "dependencies", "express"));
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
    }

    [Fact]
    public void NonStringScriptValue_SkippedGracefully()
    {
        var existing = """
            {
              "name": "my-app",
              "scripts": {
                "dev": "vite"
              }
            }
            """;

        // Scaffold has an array value for a script (unusual but should not crash)
        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "bad-script": [1, 2, 3]
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Valid scripts still merged, invalid ones skipped
        Assert.Equal("vite", GetScript(result, "dev"));
        Assert.Equal("aspire run", GetScript(result, "aspire:start"));
        Assert.Null(ParseJson(result)["scripts"]!["bad-script"]);
    }

    [Fact]
    public void NonStringDependencyValue_SkippedGracefully()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "express": "^4.18.0"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0",
                "bad-dep": ["1.0.0"]
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Valid deps merged, non-string ones skipped
        Assert.Equal("^4.18.0", GetDep(result, "dependencies", "express"));
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
        Assert.Null(GetDep(result, "dependencies", "bad-dep"));
    }

    [Fact]
    public void NonStringExistingDependency_PreservedNotCrashed()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "weird-pkg": { "version": "1.0.0", "optional": true }
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "weird-pkg": "^2.0.0",
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Non-string existing dep preserved (upgrade skipped due to type mismatch)
        var weirdPkg = ParseJson(result)["dependencies"]!["weird-pkg"];
        Assert.NotNull(weirdPkg);
        Assert.True(weirdPkg is JsonObject);

        // New deps still added
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
    }

    [Fact]
    public void DependenciesSectionIsArray_HandledGracefully()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": ["express", "react"]
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // EnsureObject replaces the array with a proper object containing scaffold deps
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
    }

    [Fact]
    public void JsonRootIsArray_ReturnsScaffold()
    {
        var existing = """["not", "an", "object"]""";

        var scaffold = """
            {
              "name": "scaffold",
              "scripts": { "dev": "aspire run" }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // Can't merge into an array — returns scaffold as-is
        Assert.Equal("scaffold", ParseJson(result)["name"]?.GetValue<string>());
    }

    [Fact]
    public void WildcardVersion_Preserved()
    {
        var existing = """
            {
              "dependencies": {
                "some-pkg": "*"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "some-pkg": "^2.0.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // "*" is unparseable — existing preserved
        Assert.Equal("*", GetDep(result, "dependencies", "some-pkg"));
    }

    [Fact]
    public void LatestTag_Preserved()
    {
        var existing = """
            {
              "dependencies": {
                "some-pkg": "latest"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "some-pkg": "^2.0.0"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // "latest" is unparseable — existing preserved
        Assert.Equal("latest", GetDep(result, "dependencies", "some-pkg"));
    }

    [Fact]
    public void PreReleaseVersion_ComparedCorrectly()
    {
        var existing = """
            {
              "devDependencies": {
                "typescript": "^5.9.3-beta.1"
              }
            }
            """;

        var scaffold = """
            {
              "devDependencies": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // 5.9.3 release is newer than 5.9.3-beta.1 pre-release
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));
    }

    [Fact]
    public void DevDependencyAlreadyInDependencies_IsNotDuplicated()
    {
        var existing = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.1.0"
              }
            }
            """;

        var scaffold = """
            {
              "devDependencies": {
                "vscode-jsonrpc": "^8.2.0",
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
        Assert.Null(GetDep(result, "devDependencies", "vscode-jsonrpc"));
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));
    }

    [Fact]
    public void Engines_NodeConstraint_OverwrittenByScaffold()
    {
        var existing = """
            {
              "name": "my-app",
              "engines": {
                "node": ">=16"
              }
            }
            """;

        var scaffold = """
            {
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        // engines.node is always overwritten — aspire init enforces Node version for ESLint 10
        var engines = ParseJson(result)["engines"]!.AsObject();
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", engines["node"]?.GetValue<string>());
    }

    [Fact]
    public void Engines_OtherKeys_Preserved()
    {
        var existing = """
            {
              "name": "my-app",
              "engines": {
                "node": ">=16",
                "npm": ">=8"
              }
            }
            """;

        var scaffold = """
            {
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        var engines = ParseJson(result)["engines"]!.AsObject();
        // node overwritten by scaffold
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", engines["node"]?.GetValue<string>());
        // npm preserved from existing
        Assert.Equal(">=8", engines["npm"]?.GetValue<string>());
    }

    [Fact]
    public void Engines_AddedWhenMissing()
    {
        var existing = """
            {
              "name": "my-app"
            }
            """;

        var scaffold = """
            {
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);

        var engines = ParseJson(result)["engines"]!.AsObject();
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", engines["node"]?.GetValue<string>());
    }

    [Fact]
    public void ScaffoldWithArrayProperty_PreservesExistingArray()
    {
        var existing = """
            {
              "name": "my-app",
              "keywords": ["web", "api"]
            }
            """;

        var scaffold = """
            {
              "keywords": ["web", "api"],
              "files": ["dist/**", "README.md"]
            }
            """;

        // When both have an array, existing wins (preserved). Scaffold-only arrays are added.
        var result = MergeJson(existing, scaffold);
        var doc = JsonNode.Parse(result)!.AsObject();

        var keywords = doc["keywords"]!.AsArray();
        Assert.Equal(2, keywords.Count);
        Assert.Equal("web", keywords[0]!.GetValue<string>());
        Assert.Equal("api", keywords[1]!.GetValue<string>());

        var files = doc["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal("dist/**", files[0]!.GetValue<string>());
    }

    [Fact]
    public void BrownfieldNpmInit_MergesSuccessfully()
    {
        // Reproduces the real-world scenario where npm init creates a package.json
        // and the scaffold produces only Aspire-desired entries (no echo of existing content).
        var existing = """
            {
              "name": "my-project",
              "version": "1.0.0",
              "main": "index.js",
              "scripts": {
                "test": "echo \"Error: no test specified\" && exit 1"
              },
              "keywords": [],
              "author": "",
              "license": "ISC",
              "description": ""
            }
            """;

        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "devDependencies": {
                "typescript": "^5.9.3",
                "tsx": "^4.21.0"
              },
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var doc = JsonNode.Parse(result)!.AsObject();

        // Original fields preserved
        Assert.Equal("my-project", doc["name"]!.GetValue<string>());
        Assert.Equal("ISC", doc["license"]!.GetValue<string>());

        // Array preserved (empty keywords from npm init)
        Assert.NotNull(doc["keywords"]);
        Assert.IsAssignableFrom<JsonArray>(doc["keywords"]);

        // Aspire scripts added, existing test script preserved
        var scripts = doc["scripts"]!.AsObject();
        Assert.Contains("test", scripts.Select(p => p.Key));
        Assert.Contains("aspire:start", scripts.Select(p => p.Key));
        Assert.Contains("aspire:build", scripts.Select(p => p.Key));

        // Dependencies merged
        Assert.NotNull(doc["dependencies"]?["vscode-jsonrpc"]);
        Assert.NotNull(doc["devDependencies"]?["typescript"]);

        // Engines set
        Assert.Contains(">=24", doc["engines"]?["node"]?.GetValue<string>());
    }

    [Fact]
    public void EnsureObject_LogsWarning_WhenReplacingArrayWithObject()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": ["express", "react"]
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var sink = new TestSink();
        var logger = new TestLogger("test", sink, enabled: true);

        PackageJsonMerger.Merge(existing, scaffold, logger);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains("dependencies", warning.Formatter!(warning.State, null)!);
    }

    [Fact]
    public void EnsureObject_LogsWarning_WhenReplacingScalarWithObject()
    {
        var existing = """
            {
              "name": "my-app",
              "engines": "node >= 16"
            }
            """;

        var scaffold = """
            {
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var sink = new TestSink();
        var logger = new TestLogger("test", sink, enabled: true);

        PackageJsonMerger.Merge(existing, scaffold, logger);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains("engines", warning.Formatter!(warning.State, null)!);
    }

    [Fact]
    public void EnsureObject_DoesNotLogWarning_WhenPropertyIsAlreadyObject()
    {
        var existing = """
            {
              "name": "my-app",
              "dependencies": {
                "express": "^4.18.0"
              }
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var sink = new TestSink();
        var logger = new TestLogger("test", sink, enabled: true);

        PackageJsonMerger.Merge(existing, scaffold, logger);

        Assert.DoesNotContain(sink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void EnsureObject_DoesNotLogWarning_WhenPropertyIsMissing()
    {
        var existing = """
            {
              "name": "my-app"
            }
            """;

        var scaffold = """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              }
            }
            """;

        var sink = new TestSink();
        var logger = new TestLogger("test", sink, enabled: true);

        PackageJsonMerger.Merge(existing, scaffold, logger);

        Assert.DoesNotContain(sink.Writes, w => w.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void BrownfieldViteProject_AspireOnlyScaffold_MergesCorrectly()
    {
        // Simulates the full brownfield flow where the scaffold only contains
        // Aspire-desired content (no echo of existing). This verifies the
        // double-merge ordering dependency (item 3) is resolved: the merger
        // does not produce incorrect aspire:-prefixed scripts from existing content.
        var existing = """
            {
              "name": "vite-brownfield",
              "version": "2.0.0",
              "type": "module",
              "scripts": {
                "dev": "vite",
                "build": "vite build",
                "preview": "vite preview"
              },
              "dependencies": {
                "vue": "^3.5.0"
              },
              "devDependencies": {
                "vite": "^7.0.0",
                "typescript": "^5.0.0"
              }
            }
            """;

        // Scaffold only has Aspire entries — no echo of existing content
        var scaffold = """
            {
              "scripts": {
                "aspire:start": "aspire run",
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:dev": "tsc --watch -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "devDependencies": {
                "@types/node": "^22.0.0",
                "eslint": "^10.0.3",
                "nodemon": "^3.1.14",
                "tsx": "^4.21.0",
                "typescript": "^5.9.3",
                "typescript-eslint": "^8.57.1"
              },
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              }
            }
            """;

        var result = MergeJson(existing, scaffold);
        var doc = JsonNode.Parse(result)!.AsObject();

        // Existing metadata preserved
        Assert.Equal("vite-brownfield", doc["name"]!.GetValue<string>());
        Assert.Equal("2.0.0", doc["version"]!.GetValue<string>());
        Assert.Equal("module", doc["type"]!.GetValue<string>());

        // Existing scripts preserved
        var scripts = doc["scripts"]!.AsObject();
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());
        Assert.Equal("vite preview", scripts["preview"]?.GetValue<string>());

        // Aspire scripts added (no incorrect aspire:dev duplicate from old "dev":"vite")
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());

        // No spurious aspire-prefixed duplicates of existing scripts
        Assert.False(scripts.ContainsKey("aspire:preview"));

        // Existing deps preserved, Aspire deps added
        Assert.Equal("^3.5.0", GetDep(result, "dependencies", "vue"));
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));

        // Existing devDeps: vite preserved, typescript upgraded to Aspire's version (newer)
        Assert.Equal("^7.0.0", GetDep(result, "devDependencies", "vite"));
        Assert.Equal("^5.9.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("^4.21.0", GetDep(result, "devDependencies", "tsx"));

        // Engines set
        Assert.Contains(">=24", doc["engines"]?["node"]?.GetValue<string>());
    }

    /// <summary>
    /// The scaffold pins typescript-eslint 8.58.0, whose peer range is
    /// typescript ">=4.8.4 &lt;6.1.0". Dependency merging keeps the newer version, so a project
    /// already on TypeScript 7 keeps it and would receive an unsatisfiable pair. Reproduced against
    /// the real resolver: npm reports ERESOLVE "Found: typescript@7.0.2".
    /// </summary>
    [Theory]
    [InlineData("^7.0.2")]
    [InlineData("~7.1.0")]
    [InlineData("6.1.0")]
    [InlineData("6.1.0-beta.1")]
    [InlineData("next")]
    [InlineData("latest")]
    [InlineData("npm:typescript@7.0.2")]
    [InlineData("workspace:*")]
    [InlineData("^6.0.0 || ^7.0.0")]
    [InlineData(">=6.0.3")]
    // A caret range is decided by the version it can reach, not the one it names: `^6.0.3` resolves
    // to the newest 6.x, so it installs 6.1.0 or later the moment one is published and the peer
    // range stops being satisfied.
    [InlineData("^6.0.3")]
    [InlineData("6.x")]
    public void Merge_BrownfieldOnUnsupportedTypeScript_DropsLintToolchain(string existingTypeScript)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": { "typescript": "{{existingTypeScript}}" }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);
        var merged = ParseJson(result);

        Assert.Equal(existingTypeScript, GetDep(result, "devDependencies", "typescript"));
        Assert.Null(merged["devDependencies"]?["typescript-eslint"]);
        Assert.DoesNotContain("aspire:lint", GetScripts(result).Select(script => script.Key));

        // The rest of the AppHost toolchain is untouched: only the lint rules need typescript-eslint.
        Assert.Equal("^8.2.0", GetDep(result, "dependencies", "vscode-jsonrpc"));
        Assert.Equal("tsc -p tsconfig.apphost.json", GetScript(result, "aspire:build"));
    }

    /// <summary>
    /// The counterpart: every version these specs can resolve to is inside typescript-eslint's
    /// <c>&lt;6.1.0</c> peer range, so the lint toolchain stays. `^5.9.3` stops below 6.0.0 and
    /// `~6.0.3` stops below 6.1.0, which is why a tilde on the same base version is accepted where
    /// the caret is not.
    /// </summary>
    [Theory]
    [InlineData("^5.9.3")]
    [InlineData("~6.0.3")]
    [InlineData("6.0.3")]
    [InlineData("=6.0.3")]
    public void Merge_BrownfieldOnSupportedTypeScript_KeepsLintToolchain(string existingTypeScript)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": { "typescript": "{{existingTypeScript}}" }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);

        Assert.Equal("8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
        Assert.Equal("eslint apphost.mts", GetScript(result, "aspire:lint"));
    }

    /// <summary>
    /// A project that already depends on typescript-eslint owns that choice, so `aspire init` leaves
    /// it in place rather than making a destructive edit to a dependency it did not introduce.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldAlreadyUsingTypeScriptEslint_LeavesItInPlace()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "^7.0.2",
                "typescript-eslint": "^8.58.0"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("^8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
        Assert.Equal("^7.0.2", GetDep(result, "devDependencies", "typescript"));
    }

    /// <summary>
    /// Owning typescript-eslint means owning the spec, not just the package. Narrowing a project's
    /// range to the scaffold's exact pin removes its ability to resolve a future 8.x that supports
    /// its compiler, and 8.58.0 peers <c>typescript: &gt;=4.8.4 &lt;6.1.0</c>, so on TypeScript 7 the
    /// rewrite turns an install that resolved into ERESOLVE.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldTypeScriptEslintRange_IsNotNarrowedToTheScaffoldPin()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "^7.0.2",
                "typescript-eslint": "^8.57.1"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("^8.57.1", GetDep(result, "devDependencies", "typescript-eslint"));
        Assert.Equal("^7.0.2", GetDep(result, "devDependencies", "typescript"));
    }

    /// <summary>
    /// The same ownership holds when the project declares typescript-eslint as a runtime dependency:
    /// the entry is left where the project put it rather than being upgraded in place.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldTypeScriptEslintAsRuntimeDependency_IsLeftWhereTheProjectPutIt()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "dependencies": {
                "typescript-eslint": "^8.57.1"
              },
              "devDependencies": {
                "typescript": "^7.0.2"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("^8.57.1", GetDep(result, "dependencies", "typescript-eslint"));
        Assert.Null(GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// Ownership of the linter spec only survives while the merge leaves the compiler alone. Here it
    /// does not: TypeScript 5.9.3 is behind the scaffold, so it is upgraded to 6.0.3, and the
    /// project's exact typescript-eslint 8.57.1 peers <c>typescript: &lt;6.0.0</c>. Leaving that spec
    /// in place would be the thing that produces ERESOLVE, so the linter moves with the compiler.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldTypeScriptEslintPinnedBehindAnUpgradedCompiler_IsUpgradedWithIt()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": "8.57.1"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// npm rejects an override for a package the manifest depends on directly unless the two specs
    /// are identical, and it does so before any peer resolution. This manifest installs today; the
    /// merge moves the direct spec to the scaffold's 6.0.3, so the override has to move with it or
    /// <c>npm install</c> fails with EOVERRIDE.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldWithAnNpmOverride_MovesTheOverrideWithTheUpgradedDependency()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "^5.9.3"
              },
              "overrides": {
                "typescript": "^5.9.3",
                "left-pad": "1.3.0",
                "some-package": { "typescript": "^5.9.3" }
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);
        var overrides = ParseJson(result)["overrides"]!.AsObject();

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("6.0.3", overrides["typescript"]?.GetValue<string>());

        // Untouched: an override for a package this merge did not rewrite, and an override tree
        // for a different package, which scopes that package's own dependencies.
        Assert.Equal("1.3.0", overrides["left-pad"]?.GetValue<string>());
        Assert.Equal("^5.9.3", overrides["some-package"]?["typescript"]?.GetValue<string>());
    }

    /// <summary>
    /// npm compares an override against whatever direct spec the manifest ends up with, and does not
    /// care how it got there. An override beside no direct dependency at all is valid until this
    /// merge introduces one, so the three paths that write a direct spec - an addition, an upgrade
    /// made in the section the project chose, and the linter replacement that follows an upgraded
    /// compiler - all have to be reconciled, not just the plain in-place upgrade.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldWithAnOverrideForAPackageTheMergeAdds_MovesTheOverrideToTheAddedSpec()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "overrides": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("6.0.3", ParseJson(result)["overrides"]?["typescript"]?.GetValue<string>());
    }

    /// <summary>
    /// The scaffold declares typescript as a devDependency, but a project that put it in
    /// dependencies keeps it there and is upgraded in place. That rewrite is just as visible to npm
    /// as one made in the scaffold's own section.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldWithAnOverrideForADependencyUpgradedInTheOtherSection_MovesTheOverride()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "dependencies": {
                "typescript": "^5.9.3"
              },
              "overrides": {
                "typescript": "^5.9.3"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "dependencies", "typescript"));
        Assert.Null(GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("6.0.3", ParseJson(result)["overrides"]?["typescript"]?.GetValue<string>());
    }

    /// <summary>
    /// The linter replacement runs after the floor merge and is the last word on the spec, so an
    /// override on typescript-eslint has to follow that value rather than the one the project wrote.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldWithAnOverrideForTheReplacedLinter_MovesTheOverrideToTheReplacement()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": ">=8.57.1 <8.58.0"
              },
              "overrides": {
                "typescript-eslint": ">=8.57.1 <8.58.0"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
        Assert.Equal("8.58.0", ParseJson(result)["overrides"]?["typescript-eslint"]?.GetValue<string>());
    }

    /// <summary>
    /// npm applies the same rule to an object entry's "." key, which is a spec for the package
    /// itself rather than for its dependencies. Reproduced against the real resolver: with
    /// <c>devDependencies.typescript "6.0.3"</c> and
    /// <c>overrides.typescript { ".": "^5.9.3", "some-dep": "1.0.0" }</c>,
    /// <c>npm install --package-lock-only</c> reports
    /// <c>EOVERRIDE / Override for typescript@6.0.3 conflicts with direct dependency</c>; setting
    /// "." to 6.0.3 installs. So the "." key has to move with the direct spec, and the tree beside
    /// it has to survive.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldWithASelfScopedNestedOverride_MovesOnlyTheSelfEntry()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "^5.9.3"
              },
              "overrides": {
                "typescript": { ".": "^5.9.3", "some-dep": "1.0.0" }
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);
        var typeScriptOverride = ParseJson(result)["overrides"]!["typescript"]!.AsObject();

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("6.0.3", typeScriptOverride["."]?.GetValue<string>());
        Assert.Equal("1.0.0", typeScriptOverride["some-dep"]?.GetValue<string>());
    }

    [Fact]
    public void Merge_BrownfieldUnsupportedTypeScript_PreservesExistingLintScriptsItDidNotAdd()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "scripts": {
                "aspire:lint": "eslint src",
                "check": "npm run aspire:lint && npm test"
              },
              "devDependencies": { "typescript": "^7.0.2" }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);
        var scripts = GetScripts(result);

        Assert.Equal("eslint src", scripts["aspire:lint"]?.GetValue<string>());
        Assert.Equal("npm run aspire:lint && npm test", scripts["check"]?.GetValue<string>());
        Assert.Null(scripts["lint"]);
        Assert.Null(GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// npm completes a partial literal into a range over the components it leaves out, so `6` is
    /// `6.x.x` and `~6` is <c>&gt;=6.0.0 &lt;7.0.0</c> - both resolve to 6.1.0 once it is published,
    /// which is outside typescript-eslint 8.58.0's <c>&lt;6.1.0</c> peer range. Only `6.0` still
    /// pins the minor, so only `6.0` is provably inside it.
    /// </summary>
    /// <remarks>
    /// The scaffold here is deliberately behind the project. Dependency merging rewrites a project
    /// spec whose lower bound is below the scaffold's version, which would replace the partial
    /// literal with the scaffold's exact version and hide what this is testing. A project that is
    /// already ahead of the scaffold keeps its own spec, which is the case the support check exists
    /// to judge.
    ///
    /// See <see href="https://github.com/npm/node-semver#x-ranges-12x-1x-12-"/> and
    /// <see href="https://github.com/npm/node-semver#tilde-ranges-123-12-1"/>.
    /// </remarks>
    [Theory]
    [InlineData("6", false)]
    [InlineData("~6", false)]
    [InlineData("^6", false)]
    [InlineData("6.0", true)]
    [InlineData("~6.0", true)]
    public void Merge_BrownfieldAheadOfScaffoldOnPartialTypeScriptLiteral_KeepsLintToolchainOnlyWhenTheRangeCannotReachTheUnsupportedVersion(
        string existingTypeScript,
        bool expectsLintToolchain)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": { "typescript": "{{existingTypeScript}}" }
            }
            """;

        var result = MergeJson(existing, ScaffoldBehindOnTypeScript);

        // The spec has to survive the merge for the support check to be what decided the outcome.
        Assert.Equal(existingTypeScript, GetDep(result, "devDependencies", "typescript"));

        if (expectsLintToolchain)
        {
            Assert.Equal("8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
            Assert.Equal("eslint apphost.mts", GetScript(result, "aspire:lint"));
        }
        else
        {
            Assert.Null(ParseJson(result)["devDependencies"]?["typescript-eslint"]);
            Assert.DoesNotContain("aspire:lint", GetScripts(result).Select(script => script.Key));
        }
    }

    /// <summary>
    /// A comparator range can be unreadable to the lower-bound merge while still being ahead of the
    /// scaffold. Replacing it with the scaffold's exact pin would be a destructive downgrade.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldLinterComparatorRangeAboveScaffoldFloor_IsPreservedWhenTheMergeUpgradesTheCompiler()
    {
        const string ExistingLinter = ">=8.60.0 <8.66.0";
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": "{{ExistingLinter}}"
              }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal(ExistingLinter, GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// Comparator and wildcard ranges are replaced only when they cannot resolve to the scaffold
    /// floor or anything newer. Inclusive bounds can admit the floor itself; exclusive bounds must
    /// leave room for a later stable version.
    /// </summary>
    [Theory]
    [InlineData("<8.58.0", "8.58.0")]
    [InlineData("<=8.58.0", "<=8.58.0")]
    [InlineData(">8.58.0", ">8.58.0")]
    [InlineData(">=8.58.0", ">=8.58.0")]
    [InlineData(">=8.57.1 <8.58.0", "8.58.0")]
    [InlineData(">8.57.1 <8.58.0", "8.58.0")]
    [InlineData(">=8.58.0 <8.58.0", "8.58.0")]
    [InlineData(">8.58.0 <=8.58.0", "8.58.0")]
    [InlineData(">=8.57.1 <=8.58.0", ">=8.57.1 <=8.58.0")]
    [InlineData(">=8.57.1 <8.58.1", ">=8.57.1 <8.58.1")]
    [InlineData(">=8.58.0 <8.66.0", ">=8.58.0 <8.66.0")]
    [InlineData(">8.58.0 <8.66.0", ">8.58.0 <8.66.0")]
    [InlineData("8.57.x", "8.58.0")]
    [InlineData("8.58.x", "8.58.x")]
    [InlineData("8.x", "8.x")]
    public void Merge_BrownfieldLinterRange_IsReplacedOnlyWhenItCannotReachTheScaffoldFloor(
        string existingLinter,
        string expectedLinter)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": "{{existingLinter}}"
              }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal(expectedLinter, GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// Range forms outside the focused comparator and wildcard parser remain project-owned. An
    /// unsupported or malformed form is not enough evidence to replace the project's dependency.
    /// </summary>
    [Theory]
    [InlineData("^8.57.1 || >=8.60.0")]
    [InlineData("8.57.1 - 8.57.9")]
    [InlineData(">=8.60.0-alpha.1 <8.66.0")]
    [InlineData(">=8.60.0 <")]
    public void Merge_BrownfieldLinterRangeWithUnknownFloor_IsPreservedWhenTheMergeUpgradesTheCompiler(string existingLinter)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": "{{existingLinter}}"
              }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal(existingLinter, GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// Package specifications that are not ordinary npm semver ranges are project-owned references,
    /// not evidence that the project selected an old linter. The compiler upgrade must not replace
    /// workspace links, local packages, aliases, forks, or direct artifact references.
    /// </summary>
    [Theory]
    [InlineData("workspace:*")]
    [InlineData("file:../typescript-eslint")]
    [InlineData("npm:@contoso/typescript-eslint-fork@^8.57.1")]
    [InlineData("github:contoso/typescript-eslint-fork#semver:^8.57.1")]
    [InlineData("git+https://github.com/contoso/typescript-eslint-fork.git#v8.57.1")]
    [InlineData("https://packages.contoso.test/typescript-eslint-fork-8.57.1.tgz")]
    public void Merge_BrownfieldLinterOpaqueSpec_IsPreservedWhenTheMergeUpgradesTheCompiler(string existingLinter)
    {
        var existing = $$"""
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "5.9.3",
                "typescript-eslint": "{{existingLinter}}"
              }
            }
            """;

        var result = MergeJson(existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal(existingLinter, GetDep(result, "devDependencies", "typescript-eslint"));
    }

    [Fact]
    public void Merge_BrownfieldRuntimeLinterOpaqueSpec_RemainsInTheProjectOwnedSection()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "dependencies": {
                "typescript-eslint": "workspace:*"
              },
              "devDependencies": {
                "typescript": "5.9.3"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("6.0.3", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal("workspace:*", GetDep(result, "dependencies", "typescript-eslint"));
        Assert.Null(GetDep(result, "devDependencies", "typescript-eslint"));
    }

    /// <summary>
    /// The reconciliation is scoped to the compiler moving. A project whose compiler this merge
    /// leaves alone still owns its linter, however unusual the spec, because nothing about the
    /// install changed underneath it.
    /// </summary>
    [Fact]
    public void Merge_BrownfieldLinterSpecIsUncheckable_IsLeftAloneWhenTheCompilerDoesNotMove()
    {
        const string Existing = """
            {
              "name": "brownfield",
              "devDependencies": {
                "typescript": "^7.0.2",
                "typescript-eslint": ">=8.57.1 <8.58.0"
              }
            }
            """;

        var result = MergeJson(Existing, ScaffoldWithLintToolchain);

        Assert.Equal("^7.0.2", GetDep(result, "devDependencies", "typescript"));
        Assert.Equal(">=8.57.1 <8.58.0", GetDep(result, "devDependencies", "typescript-eslint"));
    }

    private const string ScaffoldWithLintToolchain = """
        {
          "scripts": {
            "aspire:lint": "eslint apphost.mts",
            "aspire:build": "tsc -p tsconfig.apphost.json"
          },
          "dependencies": { "vscode-jsonrpc": "^8.2.0" },
          "devDependencies": {
            "typescript": "6.0.3",
            "typescript-eslint": "8.58.0",
            "eslint": "^10.0.3"
          }
        }
        """;

    private const string ScaffoldBehindOnTypeScript = """
        {
          "scripts": {
            "aspire:lint": "eslint apphost.mts",
            "aspire:build": "tsc -p tsconfig.apphost.json"
          },
          "dependencies": { "vscode-jsonrpc": "^8.2.0" },
          "devDependencies": {
            "typescript": "5.9.3",
            "typescript-eslint": "8.58.0",
            "eslint": "^10.0.3"
          }
        }
        """;
}
