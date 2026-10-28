// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
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

        // Create apphost.go. The blank line before the closing quotes is intentional: a raw string literal
        // drops the last newline, and gofmt requires the file to end with one.
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

            	// Add your resources here. Each call records its failure on the handle that it returns,
            	// so check every resource, for example:
            	// cache := builder.AddRedis("cache")
            	// db := builder.AddPostgres("db")
            	// for _, resource := range []aspire.Resource{cache, db} {
            	// 	if err := resource.Err(); err != nil {
            	// 		log.Fatal(aspire.FormatError(err))
            	// 	}
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

        // .aspire/modules holds the generated SDK. It is regenerated from the AppHost's integrations
        // and is large, so it should not be committed. As with apphost.go, the blank line before the closing
        // quotes is intentional: the file should end with a newline, like go-starter/.gitignore does.
        files[".gitignore"] = """
            .aspire/

            """;

        // Create go.mod with require and replace directives for local modules
        // Go requires both a `require` directive and a `replace` directive for local modules
        files["go.mod"] = """
            module apphost

            go 1.26

            require apphost/modules/aspire v0.0.0

            replace apphost/modules/aspire => ./.aspire/modules
            """;

        // Create apphost.run.json with random ports
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

        // The literals above take the line endings of this source file (CRLF in a Windows checkout), and gofmt requires LF.
        // Only CRLF is replaced, not every line ending: see AtsGoCodeGenerator.GenerateDistributedApplication for why.
        return files.ToDictionary(file => file.Key, file => file.Value.Replace("\r\n", "\n"));
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
        // generation which means the .aspire/modules directory doesn't exist yet.
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
