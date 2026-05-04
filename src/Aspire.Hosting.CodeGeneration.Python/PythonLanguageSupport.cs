// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Python;

/// <summary>
/// Provides language support for Python AppHosts, implementing scaffolding, detection, and runtime configuration.
/// </summary>
/// <remarks>
/// This implementation generates the files required for a Python-based Aspire AppHost and configures
/// the runtime to create a virtual environment and install dependencies. When <c>uv</c> is available
/// on PATH it is preferred; otherwise the standard <c>python -m venv</c> / <c>pip</c> toolchain is used.
/// </remarks>
internal sealed class PythonLanguageSupport : ILanguageSupport
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

    /// <summary>
    /// Relative path from the project directory to the venv Python executable (platform-dependent).
    /// </summary>
    private static readonly string s_venvPython = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @".venv\Scripts\python.exe"
        : ".venv/bin/python";

    /// <summary>
    /// Lazily loaded Python script that creates a venv with a microvenv fallback.
    /// </summary>
    private static readonly Lazy<string> s_microvenvScript = new(LoadMicrovenvScript);

    /// <summary>
    /// Gets the language identifier for Python AppHosts.
    /// </summary>
    /// <value>The string <c>"python"</c>.</value>
    public string Language => LanguageId;

    /// <summary>
    /// Generates the initial scaffold files for a new Python AppHost project.
    /// </summary>
    /// <param name="request">The scaffold request containing project details such as the project name and an optional port seed.</param>
    /// <returns>
    /// A dictionary mapping relative file paths to their contents. The generated files include
    /// <c>.gitignore</c>, <c>apphost.py</c>, <c>pylock.toml</c>, and <c>apphost.run.json</c>.
    /// </returns>
    /// <remarks>
    /// The <c>apphost.run.json</c> file is generated with randomly assigned port numbers unless
    /// <see cref="ScaffoldRequest.PortSeed"/> is provided, in which case ports are deterministically assigned.
    /// </remarks>
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        files[".gitignore"] = """
            .venv/
            __pycache__/
            *.pyc
            .modules/
            .aspire/
            """;

        // Create apphost.py
        files["apphost.py"] = """
            # Aspire Python AppHost
            # For more information, see: https://aspire.dev

            from aspire_app import create_builder

            with create_builder() as builder:
                # Add your resources here, for example:
                # redis = builder.add_redis("cache")
                # postgres = builder.add_postgres("db")
                builder.run()
            """;

        // Create pylock.toml
        files["pylock.apphost.toml"] = $$"""
            created-by = 'Aspire'
            lock-version = '1.0'
            requires-python = '>=3.11'

            [[packages]]
            name = "aspire_app"
            editable = true

            [packages.directory]
            path = ".modules"
            """;

        // Create requirements.txt as a fallback for pip (which doesn't support pylock.toml)
        files["apphost_requirements.txt"] = """
            # Aspire Python AppHost requirements
            # This file is used when uv is not available and pip is used instead.
            -e .modules
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

    /// <summary>
    /// Detects whether the specified directory contains a Python AppHost project.
    /// </summary>
    /// <param name="directoryPath">The full path to the directory to inspect.</param>
    /// <returns>
    /// A <see cref="DetectionResult"/> with <see cref="DetectionResult.Found"/> if <c>apphost.py</c>
    /// and either <c>pylock.toml</c> or <c>requirements.txt</c> exist in <paramref name="directoryPath"/>;
    /// otherwise <see cref="DetectionResult.NotFound"/>.
    /// </returns>
    public DetectionResult Detect(string directoryPath)
    {
        var appHostPath = Path.Combine(directoryPath, "apphost.py");
        if (!File.Exists(appHostPath))
        {
            return DetectionResult.NotFound;
        }

        var hasPylock = File.Exists(Path.Combine(directoryPath, "pylock.apphost.toml"));
        var hasRequirements = File.Exists(Path.Combine(directoryPath, "apphost_requirements.txt"));

        if (!hasPylock && !hasRequirements)
        {
            return DetectionResult.NotFound;
        }

        return DetectionResult.Found(LanguageId, "apphost.py");
    }

    /// <summary>
    /// Gets the runtime execution specification for Python AppHosts.
    /// </summary>
    /// <returns>
    /// A <see cref="RuntimeSpec"/> configured for <c>uv</c> when available, otherwise falling back
    /// to the standard <c>python -m venv</c> / <c>pip install</c> toolchain.
    /// </returns>
    public RuntimeSpec GetRuntimeSpec()
    {
        return IsCommandAvailable("uv")
            ? GetUvRuntimeSpec()
            : GetVenvRuntimeSpec();
    }

    private static RuntimeSpec GetUvRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            Initialize =
            [
                new CommandSpec
                {
                    Command = "uv",
                    Args = ["venv", ".venv", "--allow-existing"]
                }
            ],
            InstallDependencies = new CommandSpec
            {
                Command = "uv",
                Args = ["pip", "sync", "pylock.apphost.toml"]
            },
            Execute = new CommandSpec
            {
                Command = "uv",
                Args = ["run", "python", "{appHostFile}", "{args}"]
            }
        };
    }

    private static RuntimeSpec GetVenvRuntimeSpec()
    {
        var python = FindPythonCommand();
        var hasVenv = IsCommandAvailable(python, "-m", "venv", "--help");

        var initializeArgs = hasVenv
            ? (string[])["-m", "venv", ".venv"]
            : ["-c", s_microvenvScript.Value, ".venv"];

        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            Initialize =
            [
                new CommandSpec
                {
                    Command = python,
                    Args = initializeArgs
                },
                new CommandSpec
                {
                    Command = s_venvPython,
                    Args = ["-m", "ensurepip", "--upgrade"]
                }
            ],
            InstallDependencies = new CommandSpec
            {
                Command = s_venvPython,
                Args = ["-m", "pip", "install", "-r", "apphost_requirements.txt"]
            },
            Execute = new CommandSpec
            {
                Command = s_venvPython,
                Args = ["{appHostFile}", "{args}"]
            }
        };
    }

    /// <summary>
    /// Returns the Python command name available on PATH (<c>python3</c> preferred, falls back to <c>python</c>).
    /// </summary>
    private static string FindPythonCommand()
    {
        return IsCommandAvailable("python3") ? "python3" : "python";
    }

    /// <summary>
    /// Checks whether a command is available by running it with the given arguments
    /// (defaults to <c>--version</c>) and checking for a zero exit code.
    /// </summary>
    private static bool IsCommandAvailable(string command, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (args.Length == 0)
            {
                startInfo.ArgumentList.Add("--version");
            }
            else
            {
                foreach (var arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string LoadMicrovenvScript()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Aspire.Hosting.CodeGeneration.Python.Resources.microvenv.py")
            ?? throw new InvalidOperationException("Embedded resource 'microvenv.py' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
