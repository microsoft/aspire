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
    public void PrefixedScripts_AlwaysAdded()
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

        // Existing preserved
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());

        // All aspire: scripts added
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
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
}
