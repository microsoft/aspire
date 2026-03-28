// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Provides language support for TypeScript AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
public sealed class TypeScriptLanguageSupport : ILanguageSupport
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
    private static readonly string[] s_detectionPatterns = ["apphost.ts"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.ts
        files["apphost.ts"] = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // Add your resources here, for example:
            // const redis = await builder.addContainer("cache", "redis:latest");
            // const postgres = await builder.addPostgres("db");

            await builder.build().run();
            """;

        // Create package.json
        // NOTE: The engines.node constraint must match ESLint 10's own requirement
        // (^20.19.0 || ^22.13.0 || >=24) to avoid install/runtime failures on unsupported Node versions.
        var packageName = request.ProjectName?.ToLowerInvariant() ?? "aspire-apphost";
        files["package.json"] = $$"""
            {
              "name": "{{packageName}}",
              "private": true,
              "type": "module",
              "scripts": {
                "lint": "eslint apphost.ts",
                "predev": "npm run lint",
                "dev": "aspire run",
                "prebuild": "npm run lint",
                "build": "tsc",
                "watch": "tsc --watch"
              },
              "dependencies": {
                "vscode-jsonrpc": "^8.2.0"
              },
              "engines": {
                "node": "^20.19.0 || ^22.13.0 || >=24"
              },
              "devDependencies": {
                "@types/node": "^22.0.0",
                "eslint": "^10.0.3",
                "nodemon": "^3.1.14",
                "tsx": "^4.21.0",
                "typescript": "^5.9.3",
                "typescript-eslint": "^8.57.1"
              }
            }
            """;

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

        // Create tsconfig.json for TypeScript configuration
        files["tsconfig.json"] = """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "esModuleInterop": true,
                "forceConsistentCasingInFileNames": true,
                "strict": true,
                "skipLibCheck": true,
                "outDir": "./dist",
                "rootDir": "."
              },
              "include": ["apphost.ts", ".modules/**/*.ts"],
              "exclude": ["node_modules"]
            }
            """;

        // Create apphost.run.json with random ports
        // Use PortSeed if provided (for testing), otherwise use random
        var random = request.PortSeed.HasValue
            ? new Random(request.PortSeed.Value)
            : Random.Shared;

        var httpsPort = random.Next(10000, 65000);
        var httpPort = random.Next(10000, 65000);
        var otlpPort = random.Next(10000, 65000);
        var resourceServicePort = random.Next(10000, 65000);

        files["apphost.run.json"] = $$"""
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:{{httpsPort}};http://localhost:{{httpPort}}",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:{{otlpPort}}",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:{{resourceServicePort}}"
                  }
                }
              }
            }
            """;

        return files;
    }

    /// <inheritdoc />
    public DetectionResult Detect(string directoryPath)
    {
        // Check for apphost.ts
        var appHostPath = Path.Combine(directoryPath, "apphost.ts");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        // Check for package.json (required for TypeScript/Node.js projects)
        var packageJsonPath = Path.Combine(directoryPath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return DetectionResult.NotFound;
        }

        // Note: .csproj precedence is handled by the CLI, not here.
        // Language support should only check for its own language markers.

        return DetectionResult.Found(LanguageId, "apphost.ts");
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
                Args = ["tsx", "{appHostFile}"]
            },
            WatchExecute = new CommandSpec
            {
                Command = "npx",
                Args = [
                    "nodemon",
                    "--signal", "SIGTERM",
                    "--watch", ".",
                    "--ext", "ts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
                    "--exec", "npx tsx {appHostFile}"
                ]
            }
        };
    }
}
