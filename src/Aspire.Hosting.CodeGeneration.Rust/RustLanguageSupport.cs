// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Rust;

/// <summary>
/// Provides language support for Rust AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class RustLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Rust.
    /// </summary>
    private const string LanguageId = "rust";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Rust";

    private const string LanguageDisplayName = "Rust";
    private static readonly string[] s_detectionPatterns = ["apphost.rs"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create src/main.rs
        files["src/main.rs"] = """
            // Aspire Rust AppHost
            // For more information, see: https://aspire.dev

            #[path = "../.modules/mod.rs"]
            mod aspire;

            use aspire::*;

            fn main() -> Result<(), Box<dyn std::error::Error>> {
                let builder = create_builder(None)?;

                // Add your resources here, for example:
                // let redis = builder.add_redis("cache")?;
                // let postgres = builder.add_postgres("db")?;

                let app = builder.build()?;
                app.run(None)?;
                Ok(())
            }
            """;

        // Create Cargo.toml
        files["Cargo.toml"] = """
            [package]
            name = "apphost"
            version = "0.1.0"
            edition = "2021"

            [dependencies]
            serde = { version = "1.0", features = ["derive"] }
            serde_json = "1.0"
            lazy_static = "1.4"
            """;

        // Create apphost.rs marker file for detection
        files["apphost.rs"] = """
            // Aspire Rust AppHost marker file
            // This file is used to detect the project type.
            // The actual entry point is in src/main.rs.
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
        var appHostPath = Path.Combine(directoryPath, "apphost.rs");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        var cargoPath = Path.Combine(directoryPath, "Cargo.toml");
        if (!File.Exists(cargoPath))
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "apphost.rs");
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
            // No separate install step - cargo run will build automatically
            InstallDependencies = null,
            Execute = new CommandSpec
            {
                Command = "cargo",
                Args = ["run"]
            }
        };
    }
}
