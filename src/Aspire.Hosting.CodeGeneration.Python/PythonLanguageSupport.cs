// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Python;

/// <summary>
/// Provides language support for Python AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
public sealed class PythonLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Python.
    /// </summary>
    private const string LanguageId = "python";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Python";

    private const string LanguageDisplayName = "Python";
    private static readonly string[] s_detectionPatterns = ["apphost.py"];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        // Create apphost.py
        files["apphost.py"] = """
            # Aspire Python AppHost
            # For more information, see: https://aspire.dev

            import sys
            from pathlib import Path
            sys.path.insert(0, str(Path(__file__).parent / ".modules"))

            from aspire import create_builder

            builder = create_builder()

            # Add your resources here, for example:
            # redis = builder.add_redis("cache")
            # postgres = builder.add_postgres("db")

            builder.build().run()
            """;

        // Create requirements.txt
        files["requirements.txt"] = """
            # Aspire Python AppHost requirements
            """;

        // Create uv-install.py
        files["uv-install.py"] = """
            # Creates a venv and installs dependencies with uv.
            from __future__ import annotations

            import os
            import subprocess
            import sys
            from pathlib import Path


            def run(command: list[str]) -> None:
                result = subprocess.run(command)
                if result.returncode != 0:
                    sys.exit(result.returncode)


            root = Path(__file__).resolve().parent
            venv_dir = root / ".venv"
            python_path = venv_dir / ("Scripts" if os.name == "nt" else "bin") / (
                "python.exe" if os.name == "nt" else "python"
            )

            if not python_path.exists():
                run(["uv", "venv", str(venv_dir)])

            run(["uv", "pip", "install", "-r", "requirements.txt", "--python", str(python_path)])
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
        var appHostPath = Path.Combine(directoryPath, "apphost.py");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        var requirementsPath = Path.Combine(directoryPath, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "apphost.py");
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
            InstallDependencies = new CommandSpec
            {
                Command = GetPythonCommand(),
                Args = ["uv-install.py"]
            },
            Execute = new CommandSpec
            {
                Command = "uv",
                Args = ["run", "python", "{appHostFile}"]
            }
        };
    }

    /// <summary>
    /// Gets the appropriate Python command for the current platform.
    /// On Windows: tries 'python' first, then 'py' (Python launcher)
    /// On Linux/macOS: tries 'python3' first (more specific), then 'python'
    /// </summary>
    private static string GetPythonCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Try 'python' first, then 'py' (Python launcher)
            if (CommandExists("python"))
            {
                return "python";
            }
            return "py";
        }
        else
        {
            // Try 'python3' first (more specific), then 'python'
            if (CommandExists("python3"))
            {
                return "python3";
            }
            return "python";
        }
    }

    /// <summary>
    /// Checks if a command exists in the system PATH.
    /// </summary>
    private static bool CommandExists(string command)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return false;
            }

            var pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            var paths = pathEnv.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries);

            var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { ".exe", ".cmd", ".bat", "" }
                : new[] { "" };

            foreach (var path in paths)
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(path, command + ext);
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
