// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java;

/// <summary>
/// Provides language support for Java AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
public sealed class JavaLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Java.
    /// </summary>
    private const string LanguageId = "java";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Java";

    private const string LanguageDisplayName = "Java";
    private static readonly string[] s_detectionPatterns = ["AppHost.java"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create AppHost.java - must be in same package as generated code (aspire)
        // because Java only allows one public class per file
        files["AppHost.java"] = """
            // Aspire Java AppHost
            // For more information, see: https://aspire.dev

            package aspire;

            public class AppHost {
                public static void main(String[] args) {
                    try {
                        IDistributedApplicationBuilder builder = Aspire.createBuilder(null);

                        // Add your resources here, for example:
                        // var redis = builder.addRedis("cache");
                        // var postgres = builder.addPostgres("db");

                        DistributedApplication app = builder.build();
                        app.run(null);
                    } catch (Exception e) {
                        System.err.println("Failed to run: " + e.getMessage());
                        e.printStackTrace();
                        System.exit(1);
                    }
                }
            }
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
        var appHostPath = Path.Combine(directoryPath, "AppHost.java");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "AppHost.java");
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
            // No separate install step - compilation happens in Execute
            InstallDependencies = null,
            Execute = new CommandSpec
            {
                // Use a shell to compile and run in sequence
                // On Windows, use cmd /c; on Unix, use sh -c
                Command = OperatingSystem.IsWindows() ? "cmd" : "sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "javac -d . .modules\\Transport.java .modules\\Base.java .modules\\Aspire.java AppHost.java && java aspire.AppHost"]
                    : ["-c", "javac -d . .modules/Transport.java .modules/Base.java .modules/Aspire.java AppHost.java && java aspire.AppHost"]
            }
        };
    }
}
