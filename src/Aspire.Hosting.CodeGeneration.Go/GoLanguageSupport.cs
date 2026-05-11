// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go;

/// <summary>
/// Provides language support for Go AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class GoLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Go.
    /// </summary>
    private const string LanguageId = "go";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Go";

    private const string LanguageDisplayName = "Go";
    private static readonly string[] s_detectionPatterns = ["apphost.go"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.go
        files["apphost.go"] = """
            // Aspire Go AppHost
            // For more information, see: https://aspire.dev

            package main

            import (
            	"log"
            	"apphost/modules/aspire"
            )

            func main() {
            	builder, err := aspire.CreateBuilder()
            	if err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}

            	// Add your resources here, for example:
            	// cache := builder.AddRedis("cache")
            	// db := builder.AddPostgres("db")
            	// if err := builder.Err(); err != nil {
            	// 	log.Fatal(aspire.FormatError(err))
            	// }

            	app, err := builder.Build()
            	if err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}
            	if err := app.Run(); err != nil {
            		log.Fatal(aspire.FormatError(err))
            	}
            }
            """;

        // Create go.mod with require and replace directives for local modules
        // Go requires both a `require` directive and a `replace` directive for local modules
        files["go.mod"] = """
            module apphost

            go 1.26

            require apphost/modules/aspire v0.0.0

            replace apphost/modules/aspire => ./.modules
            """;

        // Create apphost.run.json with random ports
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
        var appHostPath = Path.Combine(directoryPath, "apphost.go");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        var goModPath = Path.Combine(directoryPath, "go.mod");
        if (!File.Exists(goModPath))
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "apphost.go");
    }

    /// <inheritdoc />
    public RuntimeSpec GetRuntimeSpec()
    {
        // Note: InstallDependencies is null because "go run ." handles module
        // resolution automatically, and InstallDependencies runs BEFORE code
        // generation which means the .modules directory doesn't exist yet.
        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            InstallDependencies = null,
            Execute = new CommandSpec
            {
                Command = "go",
                Args = ["run", "."]
            }
        };
    }
}
