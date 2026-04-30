// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Provides language support for TypeScript AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class TypeScriptLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for TypeScript with Node.js.
    /// Format: {language}/{runtime} to support multiple runtimes (e.g., typescript/bun, typescript/deno).
    /// </summary>
    private const string LanguageId = "typescript/nodejs";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "TypeScript";

    private const string LanguageDisplayName = "TypeScript (Node.js)";
    private const string AppHostFileName = "apphost.ts";
    private const string PackageJsonFileName = "package.json";
    private const string AppHostTsConfigFileName = "tsconfig.apphost.json";

    /// <summary>
    /// The default content for tsconfig.apphost.json, shared between scaffolding and migration.
    /// </summary>
    private const string AppHostTsConfigContent = """
        {
          "compilerOptions": {
            "target": "ES2022",
            "module": "NodeNext",
            "moduleResolution": "NodeNext",
            "esModuleInterop": true,
            "forceConsistentCasingInFileNames": true,
            "strict": true,
            "skipLibCheck": true,
            "outDir": "./dist/apphost",
            "rootDir": "."
          },
          "include": ["apphost.ts", ".modules/**/*.ts"],
          "exclude": ["node_modules"]
        }
        """;

    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly string[] s_detectionPatterns = ["apphost.ts"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.ts
        files[AppHostFileName] = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // Add your resources here, for example:
            // const redis = await builder.addContainer("cache", "redis:latest");
            // const postgres = await builder.addPostgres("db");

            await builder.build().run();
            """;

        files[PackageJsonFileName] = CreatePackageJson(request);

        // Create eslint.config.mjs for catching unawaited promises in apphost.ts
        files["eslint.config.mjs"] = """
            // @ts-check

            import { defineConfig } from 'eslint/config';
            import tseslint from 'typescript-eslint';

            export default defineConfig({
              files: ['apphost.ts'],
              extends: [tseslint.configs.base],
              languageOptions: {
                parserOptions: {
                  projectService: true,
                },
              },
              rules: {
                '@typescript-eslint/no-floating-promises': ['error', { checkThenables: true }],
              },
            });
            """;

        // Create an apphost-specific tsconfig so existing brownfield TypeScript settings are preserved.
        files[AppHostTsConfigFileName] = AppHostTsConfigContent;

        // Create apphost.run.json with random ports
        // Use PortSeed if provided (for testing), otherwise use random
        var random = request.PortSeed.HasValue
            ? new Random(request.PortSeed.Value)
            : Random.Shared;

        var ports = AppHostProfilePortGenerator.Generate(random);

        files["apphost.run.json"] = $$"""
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:{{ports.DashboardHttpsPort}};http://localhost:{{ports.DashboardHttpPort}}",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:{{ports.OtlpHttpsPort}}",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:{{ports.ResourceServiceHttpsPort}}"
                  }
                }
              }
            }
            """;

        return files;
    }

    private static string CreatePackageJson(ScaffoldRequest request)
    {
        // Build scaffold output with only Aspire-desired content. We intentionally do NOT
        // read the existing package.json here — the CLI-side PackageJsonMerger handles all
        // combining with on-disk content. Including existing entries in the scaffold output
        // would cause a double-merge where correctness depends on JsonObject iteration order.
        var packageJson = new JsonObject();
        var packageJsonPath = Path.Combine(request.TargetPath, PackageJsonFileName);

        if (!File.Exists(packageJsonPath))
        {
            // Greenfield: include root metadata so the scaffold output is a complete package.json.
            var packageName = request.ProjectName?.ToLowerInvariant() ?? "aspire-apphost";
            packageJson["name"] = packageName;
            packageJson["version"] = "1.0.0";
            packageJson["private"] = true;
            packageJson["type"] = "module";
        }

        // NOTE: The engines.node constraint must match ESLint 10's own requirement
        // (^20.19.0 || ^22.13.0 || >=24) to avoid install/runtime failures on unsupported Node versions.
        // This is set for both greenfield and brownfield scenarios — the user is opting into Aspire
        // which requires these Node versions. The CLI-side MergeEngines also enforces this during merge.
        var engines = EnsureObject(packageJson, "engines");
        engines["node"] = "^20.19.0 || ^22.13.0 || >=24";

        var scripts = EnsureObject(packageJson, "scripts");
        scripts["aspire:lint"] = "eslint apphost.ts";
        scripts["aspire:start"] = "aspire run";
        scripts["aspire:build"] = $"tsc -p {AppHostTsConfigFileName}";
        scripts["aspire:dev"] = $"tsc --watch -p {AppHostTsConfigFileName}";

        EnsureDependency(packageJson, "dependencies", "vscode-jsonrpc", "^8.2.0");
        EnsureDependency(packageJson, "devDependencies", "@types/node", "^22.0.0");
        EnsureDependency(packageJson, "devDependencies", "eslint", "^10.0.3");
        EnsureDependency(packageJson, "devDependencies", "nodemon", "^3.1.14");
        EnsureDependency(packageJson, "devDependencies", "tsx", "^4.21.0");
        EnsureDependency(packageJson, "devDependencies", "typescript", "^5.9.3");
        EnsureDependency(packageJson, "devDependencies", "typescript-eslint", "^8.57.1");

        return packageJson.ToJsonString(s_jsonSerializerOptions);
    }

    private static void EnsureDependency(JsonObject packageJson, string sectionName, string packageName, string version)
    {
        var section = EnsureObject(packageJson, sectionName);

        var existingVersion = GetStringValue(section[packageName]);
        if (existingVersion is null)
        {
            section[packageName] = version;
            return;
        }

        if (NpmVersionHelper.ShouldUpgrade(existingVersion, version))
        {
            section[packageName] = version;
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        parent[propertyName] = obj;
        return obj;
    }

    private static string? GetStringValue(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var stringValue) ? stringValue : null;
    }

    /// <inheritdoc />
    public DetectionResult Detect(string directoryPath)
    {
        // Check for apphost.ts
        var appHostPath = Path.Combine(directoryPath, AppHostFileName);
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        // Check for package.json (required for TypeScript/Node.js projects)
        var packageJsonPath = Path.Combine(directoryPath, PackageJsonFileName);
        if (!File.Exists(packageJsonPath))
        {
            return DetectionResult.NotFound;
        }

        // Note: .csproj precedence is handled by the CLI, not here.
        // Language support should only check for its own language markers.

        return DetectionResult.Found(LanguageId, AppHostFileName);
    }

    /// <inheritdoc />
    public RuntimeSpec GetRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            ExtensionLaunchCapability = "node",
            InstallDependencies = new CommandSpec
            {
                Command = "npm",
                Args = ["install"]
            },
            Execute = new CommandSpec
            {
                Command = "npx",
                Args = ["--no-install", "tsx", "--tsconfig", AppHostTsConfigFileName, "{appHostFile}"]
            },
            WatchExecute = new CommandSpec
            {
                Command = "npx",
                Args = [
                    "--no-install",
                    "nodemon",
                    "--signal", "SIGTERM",
                    "--watch", ".",
                    "--ext", "ts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
                    "--exec", $"npx --no-install tsx --tsconfig {AppHostTsConfigFileName} {{appHostFile}}"
                ]
            },
            MigrationFiles = new Dictionary<string, string>
            {
                [AppHostTsConfigFileName] = AppHostTsConfigContent
            }
        };
    }
}
