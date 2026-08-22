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
    public string CertificateBundleEnvironmentVariable => "NODE_EXTRA_CA_CERTS";

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.mts
        files[AppHostFileName] = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            // Add your resources here, for example:
            // const redis = await builder.addContainer("cache", "redis:latest");
            // const postgres = await builder.addPostgres("db");

            await builder.build().run();
            """;

        files[".gitignore"] = """
            node_modules/
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

        // Every version below is an exact pin, and must stay one. `aspire init` scaffolds no
        // package-lock.json, so the `npm install` it runs resolves each range from scratch, and a
        // range resolves to the newest matching version the configured registry advertises.
        //
        // That is unsafe against an Azure Artifacts npm mirror such as the approved
        // dotnet-public-npm feed, which only serves package versions it has already ingested and
        // answers 401 (not 404) for everything else. Its packument and its tarball store drift
        // apart, so it advertises versions it cannot hand out. Measured against
        // https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ on
        // 2026-08-09, where `^4.22.3` resolved to the 4.23.5 that packument named `latest`:
        //
        //   GET /tsx/-/tsx-4.23.1.tgz -> 200
        //   GET /tsx/-/tsx-4.23.2.tgz -> 401
        //   GET /tsx/-/tsx-4.23.5.tgz -> 401
        //
        // which failed `aspire init --language typescript` with `npm error code E401`. An exact
        // version cannot drift onto an un-ingested release, so it is the only permanently safe
        // form here. Keep these in sync with the versions the shipped ts-starter and py-starter
        // package-lock.json resolve; TypeScriptLanguageSupportTests asserts both properties.
        EnsureDependency(packageJson, "dependencies", "vscode-jsonrpc", "8.2.1");
        EnsureDependency(packageJson, "devDependencies", "@types/node", "22.19.15");
        EnsureDependency(packageJson, "devDependencies", "eslint", "10.0.3");
        EnsureDependency(packageJson, "devDependencies", "nodemon", "3.1.14");
        EnsureDependency(packageJson, "devDependencies", "tsx", "4.22.3");

        // TypeScript 6.0 is the newest release a scaffolded AppHost can take. TypeScript 7 is a native
        // (Go) compiler that ships no JavaScript compiler API, and `aspire:lint` runs typescript-eslint,
        // whose `typescript` peer range is capped below 6.1.0. 6.0 is also the compiler that the
        // `@typescript/typescript6` TS7 compatibility package re-exports, so this is the supported
        // bridge rather than a version left behind. See "Running Side-by-Side with TypeScript 6.0" in
        // https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/.
        EnsureDependency(packageJson, "devDependencies", "typescript", "6.0.3");

        // typescript-eslint 8.58.0 is the first release whose `typescript` peer range admits 6.x:
        // 8.57.1 peers `>=4.8.4 <6.0.0`, 8.58.0 peers `>=4.8.4 <6.1.0`. A `^8.57.1` floor is
        // satisfiable by 8.57.1 itself, so pairing it with the TypeScript 6 above would let a
        // resolver produce a scaffold that fails to install on the peer conflict.
        EnsureDependency(packageJson, "devDependencies", "typescript-eslint", "8.58.0");

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
        var runtimeSpec = new RuntimeSpec
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
                    "--ignore", ".aspire/modules/",
                    "--exec", $"npx --no-install tsc --noEmit -p {AppHostTsConfigFileName} && npx --no-install tsx --tsconfig {AppHostTsConfigFileName} \"{{appHostFile}}\""
                ]
            },
            MigrationFiles = new Dictionary<string, string>
            {
                [AppHostTsConfigFileName] = s_appHostTsConfigContent
            }
        };

        SetCertificateBundleEnvironmentVariableIfSupported(runtimeSpec, CertificateBundleEnvironmentVariable);

        return runtimeSpec;
    }

    /// <summary>
    /// Sets the certificate bundle environment variable when the runtime contract supports it.
    /// </summary>
    internal static void SetCertificateBundleEnvironmentVariableIfSupported(
        object runtimeSpec,
        string environmentVariableName)
    {
        // Aspire.TypeSystem is force-shared from the installed CLI. A newer codegen assembly can
        // therefore run against an older RuntimeSpec that has the same assembly identity but does not
        // expose this additive property. Probe by name so the new certificate feature is skipped while
        // the rest of code generation remains compatible.
        runtimeSpec.GetType()
            .GetProperty(nameof(RuntimeSpec.CertificateBundleEnvironmentVariable))
            ?.SetValue(runtimeSpec, environmentVariableName);
    }
}
