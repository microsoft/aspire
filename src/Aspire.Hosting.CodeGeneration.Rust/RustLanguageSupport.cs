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
    private const string AppHostFileName = "apphost.rs";

    // Must stay in sync with the [[bin]] target the scaffolded Cargo.toml below declares.
    private const string AppHostBinaryName = "apphost";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Rust";

    private const string LanguageDisplayName = "Rust";
    private static readonly string[] s_detectionPatterns = [AppHostFileName];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        files[AppHostFileName] = """
            // Aspire Rust AppHost
            // For more information, see: https://aspire.dev

            #[path = ".aspire/modules/mod.rs"]
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

            [[bin]]
            name = "apphost"
            path = "apphost.rs"

            [dependencies]
            serde = { version = "1.0", features = ["derive"] }
            serde_json = "1.0"
            lazy_static = "1.4"

            # The generated SDK under .aspire/modules is large, and incremental compilation splits it into
            # thousands of codegen units. On macOS debug info stays in those object files, because
            # split-debuginfo defaults to "unpacked", so LLDB has to stitch a debug map across all of them
            # and fails to resolve any type whose definition landed in a different unit. Compiling the
            # AppHost in one pass keeps the debugger working and is not measurably slower.
            [profile.dev]
            incremental = false
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
        var appHostPath = Path.Combine(directoryPath, AppHostFileName);
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        var cargoPath = Path.Combine(directoryPath, "Cargo.toml");
        if (!File.Exists(cargoPath))
        {
            return DetectionResult.NotFound;
        }

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
            ExtensionLaunchCapability = LanguageId,
            // No separate install step - cargo run will build automatically
            InstallDependencies = null,
            Execute = new CommandSpec
            {
                Command = "cargo",
                // The binary is named explicitly because the scaffolded manifest declares `[[bin]] apphost`
                // and a package is free to gain more. A bare `cargo run` is ambiguous the moment a second
                // [[bin]] target exists and fails with "could not determine which binary to run", which would
                // stop the app host from starting at all. Naming it here rather than adding `default-run` to
                // the manifest also fixes app hosts that were scaffolded before this change, since the
                // command comes from the CLI while the manifest is already on disk.
                // See https://doc.rust-lang.org/cargo/commands/cargo-run.html
                Args = ["run", "--bin", AppHostBinaryName, "--"]
            }
        };
    }
}
