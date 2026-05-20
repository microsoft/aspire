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
    private const string AppHostFileName = "apphost.mts";
    private const string PackageJsonFileName = "package.json";
    private const string AppHostTsConfigFileName = "tsconfig.apphost.json";
    private const string AppHostPackageName = "aspire-apphost";
    private const string EslintConfigFileName = "eslint.config.mjs";

    /// <summary>
    /// Cached content of <c>tsconfig.apphost.json</c>, sourced from the embedded resource
    /// of the same name so the scaffold and the lint regression tests share a single
    /// source of truth.
    /// </summary>
    private static readonly string s_appHostTsConfigContent = EmbeddedResources.Read(AppHostTsConfigFileName);

    /// <summary>
    /// Cached content of <c>eslint.config.mjs</c>, sourced from the embedded resource of
    /// the same name. The scaffolded file enables <c>@typescript-eslint/no-floating-promises</c>
    /// against <c>apphost.mts</c> so unawaited AppHost promises surface as lint errors.
    /// </summary>
    private static readonly string s_eslintConfigContent = EmbeddedResources.Read(EslintConfigFileName);

    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly string[] s_detectionPatterns = ["apphost.mts", "apphost.ts"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.mts
        files[AppHostFileName] = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.mjs';

            const builder = await createBuilder();

            // Add your resources here, for example:
            // const redis = await builder.addContainer("cache", "redis:latest");
            // const postgres = await builder.addPostgres("db");

            await builder.build().run();
            """;

        files[".gitignore"] = """
            node_modules/
            .modules/
            dist/
            .aspire/
            """;
        files[PackageJsonFileName] = CreatePackageJson(request);

        // Create eslint.config.mjs for catching unawaited promises in apphost.mts
        files[EslintConfigFileName] = s_eslintConfigContent;

        // Create an apphost-specific tsconfig so existing brownfield TypeScript settings are preserved.
        files[AppHostTsConfigFileName] = s_appHostTsConfigContent;

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
        var hasExistingPackageJson = HasExistingPackageJson(request);
        if (!hasExistingPackageJson)
        {
            // Fresh package: include metadata so the scaffold output is a complete package.json.
            var packageName = IsNestedBrownfieldPackage(request.TargetPath)
                ? AppHostPackageName
                : request.ProjectName?.ToLowerInvariant() ?? AppHostPackageName;
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
        scripts["aspire:lint"] = "eslint apphost.mts";
        scripts["aspire:start"] = "aspire run";
        scripts["aspire:build"] = $"tsc -p {AppHostTsConfigFileName}";
        scripts["aspire:dev"] = $"tsc --watch -p {AppHostTsConfigFileName}";

        if (!hasExistingPackageJson)
        {
            scripts["lint"] = "npm run aspire:lint";
            scripts["predev"] = "npm run aspire:lint";
            scripts["dev"] = "npm run aspire:start";
            scripts["prebuild"] = "npm run aspire:lint";
            scripts["build"] = "npm run aspire:build";
            scripts["watch"] = "npm run aspire:dev";
        }

        EnsureDependency(packageJson, "dependencies", "vscode-jsonrpc", "^8.2.0");
        EnsureDependency(packageJson, "devDependencies", "@types/node", "^22.0.0");
        EnsureDependency(packageJson, "devDependencies", "eslint", "^10.0.3");
        EnsureDependency(packageJson, "devDependencies", "nodemon", "^3.1.14");
        EnsureDependency(packageJson, "devDependencies", "tsx", "^4.21.0");
        EnsureDependency(packageJson, "devDependencies", "typescript", "^5.9.3");
        EnsureDependency(packageJson, "devDependencies", "typescript-eslint", "^8.57.1");

        return packageJson.ToJsonString(s_jsonSerializerOptions);
    }

    private static bool IsNestedBrownfieldPackage(string targetPath)
    {
        var targetDirectory = new DirectoryInfo(targetPath);
        return string.Equals(targetDirectory.Name, AppHostPackageName, StringComparison.OrdinalIgnoreCase) &&
            targetDirectory.Parent is { } parent &&
            File.Exists(Path.Combine(parent.FullName, PackageJsonFileName));
    }

    private static bool HasExistingPackageJson(ScaffoldRequest request)
    {
        var packageJsonPath = Path.Combine(request.TargetPath, PackageJsonFileName);
        return File.Exists(packageJsonPath);
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
        var appHostFileName = File.Exists(Path.Combine(directoryPath, AppHostFileName))
            ? AppHostFileName
            : "apphost.ts";
        var appHostPath = Path.Combine(directoryPath, appHostFileName);
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

        return DetectionResult.Found(LanguageId, appHostFileName);
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
            PreExecute =
            [
                new CommandSpec
                {
                    Command = "npx",
                    Args = ["--no-install", "tsc", "--noEmit", "-p", AppHostTsConfigFileName]
                }
            ],
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
                    "--ext", "ts,mts",
                    "--ignore", "node_modules/",
                    "--ignore", ".modules/",
                    "--exec", $"npx --no-install tsc --noEmit -p {AppHostTsConfigFileName} && npx --no-install tsx --tsconfig {AppHostTsConfigFileName} \"{{appHostFile}}\""
                ]
            },
            MigrationFiles = new Dictionary<string, string>
            {
                [AppHostTsConfigFileName] = s_appHostTsConfigContent
            }
        };
    }
}
