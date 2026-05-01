// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0612
#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only
#pragma warning disable ASPIREEXTENSION001 // SupportsDebuggingAnnotation is experimental

using Microsoft.Extensions.DependencyInjection;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.Utils;
using System.Diagnostics;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using System.Text.Json;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.Python.Tests;

public class AddPythonAppTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [RequiresTools(["python"])]
    public async Task AddPythonAppProducesDockerfileResourceInManifest()
    {
        var (projectDirectory, pythonExecutable, scriptName) = CreateTempPythonProject(outputHelper);

        var manifestPath = Path.Combine(projectDirectory, "aspire-manifest.json");

        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = Path.GetFullPath(projectDirectory);
            options.Args = ["--publisher", "manifest", "--output-path", manifestPath];
        }, outputHelper);

        var pyproj = builder.AddPythonApp("pyproj", projectDirectory, scriptName);

        var manifest = await ManifestUtils.GetManifest(pyproj.Resource, manifestDirectory: projectDirectory);
        var expectedManifest = $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile"
              },
              "env": {
                "OTEL_TRACES_EXPORTER": "otlp",
                "OTEL_LOGS_EXPORTER": "otlp",
                "OTEL_METRICS_EXPORTER": "otlp",
                "OTEL_PYTHON_LOGGING_AUTO_INSTRUMENTATION_ENABLED": "true"
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString(), ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    public async Task AddInstrumentedPythonProjectProducesDockerfileResourceInManifest()
    {
        var (projectDirectory, pythonExecutable, scriptName) = CreateTempPythonProject(outputHelper, instrument: true);

        var manifestPath = Path.Combine(projectDirectory, "aspire-manifest.json");

        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = Path.GetFullPath(projectDirectory);
            options.Args = ["--publisher", "manifest", "--output-path", manifestPath];
        }, outputHelper);

        var pyproj = builder.AddPythonApp("pyproj", projectDirectory, scriptName);

        var manifest = await ManifestUtils.GetManifest(pyproj.Resource, manifestDirectory: projectDirectory);
        var expectedManifest = $$"""
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile"
              },
              "env": {
                "OTEL_TRACES_EXPORTER": "otlp",
                "OTEL_LOGS_EXPORTER": "otlp",
                "OTEL_METRICS_EXPORTER": "otlp",
                "OTEL_PYTHON_LOGGING_AUTO_INSTRUMENTATION_ENABLED": "true"
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString(), ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/8466")]
    public async Task PythonResourceFinishesSuccessfully()
    {
        var (projectDirectory, _, scriptName) = CreateTempPythonProject(outputHelper);

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        builder.AddPythonApp("pyproj", projectDirectory, scriptName);

        using var app = builder.Build();

        await app.StartAsync();

        await app.ResourceNotifications.WaitForResourceAsync("pyproj", "Finished").WaitAsync(TimeSpan.FromSeconds(30));

        await app.StopAsync();

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    public async Task PythonResourceSupportsWithReference()
    {
        var (projectDirectory, _, scriptName) = CreateTempPythonProject(outputHelper);

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);

        var externalResource = builder.AddConnectionString("connectionString");
        builder.Configuration["ConnectionStrings:connectionString"] = "test";

        var pyproj = builder.AddPythonApp("pyproj", projectDirectory, scriptName)
                            .WithReference(externalResource);

        using var app = builder.Build();
        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(pyproj.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("test", environmentVariables["ConnectionStrings__connectionString"]);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    public async Task AddPythonApp_SetsResourcePropertiesCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);

        var (projectDirectory, pythonExecutable, scriptName) = CreateTempPythonProject(outputHelper);

        builder.AddPythonApp("pythonProject", projectDirectory, scriptName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        // Filter to get only the PythonAppResource (pip installer may also be present if requirements.txt exists)
        var pythonProjectResource = executableResources.OfType<PythonAppResource>().Single();

        Assert.Equal("pythonProject", pythonProjectResource.Name);
        Assert.Equal(projectDirectory, pythonProjectResource.WorkingDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        Assert.Equal(scriptName, commandArguments[0]);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    public async Task AddPythonApp_ObsoleteMethod_StillWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);

        var (projectDirectory, pythonExecutable, scriptName) = CreateTempPythonProject(outputHelper);

#pragma warning disable CS0618 // Type or member is obsolete
        builder.AddPythonApp("pythonProject", projectDirectory, scriptName);
#pragma warning restore CS0618 // Type or member is obsolete

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        // Filter to get only the PythonAppResource (pip installer may also be present if requirements.txt exists)
        var pythonProjectResource = executableResources.OfType<PythonAppResource>().Single();

        Assert.Equal("pythonProject", pythonProjectResource.Name);
        Assert.Equal(projectDirectory, pythonProjectResource.WorkingDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        Assert.Equal(scriptName, commandArguments[0]);

        // Verify it creates a script entrypoint
        var resource = appModel.Resources.OfType<PythonAppResource>().Single();
        var entrypointAnnotation = resource.Annotations.OfType<PythonEntrypointAnnotation>().Single();
        Assert.Equal(EntrypointType.Script, entrypointAnnotation.Type);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    [Fact]
    [RequiresTools(["python"])]
    public async Task AddPythonAppWithScriptArgs_IncludesTheArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);

        var (projectDirectory, pythonExecutable, scriptName) = CreateTempPythonProject(outputHelper);

        builder.AddPythonApp("pythonProject", projectDirectory, scriptName)
            .WithArgs("test");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        // Filter to get only the PythonAppResource (pip installer may also be present if requirements.txt exists)
        var pythonProjectResource = executableResources.OfType<PythonAppResource>().Single();

        Assert.Equal("pythonProject", pythonProjectResource.Name);
        Assert.Equal(projectDirectory, pythonProjectResource.WorkingDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(projectDirectory, ".venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        Assert.Equal(scriptName, commandArguments[0]);
        Assert.Equal("test", commandArguments[1]);

        // If we don't throw, clean up the directories.
        Directory.Delete(projectDirectory, true);
    }

    private static (string projectDirectory, string pythonExecutable, string scriptName) CreateTempPythonProject(ITestOutputHelper outputHelper, bool instrument = false)
    {
        var projectDirectory = Directory.CreateTempSubdirectory().FullName;

        if (instrument)
        {
            PreparePythonProject(outputHelper, projectDirectory, PythonApp, InstrumentedPythonAppRequirements);
        }
        else
        {
            PreparePythonProject(outputHelper, projectDirectory, PythonApp);
        }

        var pythonExecutable = Path.Combine(projectDirectory,
            ".venv",
            OperatingSystem.IsWindows() ? "Scripts" : "bin",
            OperatingSystem.IsWindows() ? "python.exe" : "python"
            );

        return (projectDirectory, pythonExecutable, "main.py");
    }

    private static void PreparePythonProject(ITestOutputHelper outputHelper, string projectDirectory, string scriptContent, string? requirementsContent = null)
    {
        var scriptPath = Path.Combine(projectDirectory, "main.py");
        File.WriteAllText(scriptPath, scriptContent);

        var requirementsPath = Path.Combine(projectDirectory, "requirements.txt");
        File.WriteAllText(requirementsPath, requirementsContent);

        // This dockerfile doesn't *need* to work but it's a good sanity check.
        var dockerFilePath = Path.Combine(projectDirectory, "Dockerfile");
        File.WriteAllText(dockerFilePath,
            """
            FROM python:3.9
            WORKDIR /app
            COPY requirements.txt .
            RUN pip install --no-cache-dir -r requirements.txt
            COPY . .
            CMD ["python", "main.py"]
            """);

        var prepareVirtualEnvironmentStartInfo = new ProcessStartInfo()
        {
            FileName = "python",
            Arguments = $"-m venv .venv",
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var createVirtualEnvironmentProcess = Process.Start(prepareVirtualEnvironmentStartInfo);
        var createVirtualEnvironmentProcessResult = createVirtualEnvironmentProcess!.WaitForExit(TimeSpan.FromMinutes(2));

        outputHelper.WriteLine("Create Virtual Environment Standard Output:");

        CopyStreamToTestOutput("python -m venv .venv (Standard Output)", createVirtualEnvironmentProcess.StandardOutput, outputHelper);
        CopyStreamToTestOutput("python -m venv .venv (Standard Error)", createVirtualEnvironmentProcess.StandardError, outputHelper);

        if (!createVirtualEnvironmentProcessResult)
        {
            createVirtualEnvironmentProcess.Kill(true);
            throw new InvalidOperationException("Failed to create virtual environment.");
        }

        var relativePipPath = Path.Combine(
            ".venv",
            OperatingSystem.IsWindows() ? "Scripts" : "bin",
            OperatingSystem.IsWindows() ? "pip.exe" : "pip"
            );
        var pipPath = Path.GetFullPath(relativePipPath, projectDirectory);

        var installRequirementsStartInfo = new ProcessStartInfo()
        {
            FileName = pipPath,
            Arguments = $"install -q -r requirements.txt",
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var installRequirementsProcess = Process.Start(installRequirementsStartInfo);
        var installRequirementsProcessResult = installRequirementsProcess!.WaitForExit(TimeSpan.FromMinutes(2));

        CopyStreamToTestOutput("pip install -r requirements.txt (Standard Output)", installRequirementsProcess.StandardOutput, outputHelper);
        CopyStreamToTestOutput("pip install -r requirements.txt (Standard Error)", installRequirementsProcess.StandardError, outputHelper);

        if (!installRequirementsProcessResult)
        {
            installRequirementsProcess.Kill(true);
            throw new InvalidOperationException("Failed to install requirements.");
        }
    }

    private static void CopyStreamToTestOutput(string label, StreamReader reader, ITestOutputHelper outputHelper)
    {
        var output = reader.ReadToEnd();
        outputHelper.WriteLine($"{label}:\n\n{output}");
    }

    private static void AssertPythonCommandPath(string expectedVenvPath, string actualCommand)
    {
        var expectedCommand = OperatingSystem.IsWindows()
            ? Path.Join(expectedVenvPath, "Scripts", "python.exe")
            : Path.Join(expectedVenvPath, "bin", "python");
        
        Assert.Equal(expectedCommand, actualCommand);
    }

    private const string PythonApp = """"
        import logging

        # Reset the logging configuration to a sensible default.
        logging.basicConfig()
        logging.getLogger().setLevel(logging.NOTSET)

        # Write a basic log message.
        logging.getLogger(__name__).info("Hello world!")
        """";

    private const string InstrumentedPythonAppRequirements = """"
        opentelemetry-distro[otlp]
        """";

    [Fact]
    public void AddPythonApp_DoesNotThrowOnMissingVirtualEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        // Should not throw - validation is deferred until runtime
        var exception = Record.Exception(() =>
            builder.AddPythonApp("pythonProject", tempDir.Path, "main.py"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WithVirtualEnvironment_UpdatesCommandToUseNewVirtualEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment("custom-venv");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempDir.Path));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, "custom-venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, "custom-venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);
        Assert.Equal(scriptName, commandArguments[0]);
    }

    [Fact]
    public async Task WithVirtualEnvironment_SupportsAbsolutePath()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptName = "main.py";

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(tempVenvDir.Path);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(tempVenvDir.Path, "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(tempVenvDir.Path, "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);
        Assert.Equal(scriptName, commandArguments[0]);
    }

    [Fact]
    public void WithVirtualEnvironment_ThrowsOnNullBuilder()
    {
        IResourceBuilder<PythonAppResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.WithVirtualEnvironment("some-venv"));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void WithVirtualEnvironment_ThrowsOnNullOrEmptyPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";
        var resourceBuilder = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var nullException = Assert.Throws<ArgumentNullException>(() =>
            resourceBuilder.WithVirtualEnvironment(null!));
        Assert.Equal("virtualEnvironmentPath", nullException.ParamName);

        var emptyException = Assert.Throws<ArgumentException>(() =>
            resourceBuilder.WithVirtualEnvironment(string.Empty));
        Assert.Equal("virtualEnvironmentPath", emptyException.ParamName);
    }

    [Fact]
    public async Task WithVirtualEnvironment_CanBeChainedWithOtherExtensions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        var resourceBuilder = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(".venv")
            .WithArgs("arg1", "arg2")
            .WithEnvironment("TEST_VAR", "test_value");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);
        Assert.Equal(3, commandArguments.Count);
        Assert.Equal(scriptName, commandArguments[0]);
        Assert.Equal("arg1", commandArguments[1]);
        Assert.Equal("arg2", commandArguments[2]);

        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pythonProjectResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal("test_value", environmentVariables["TEST_VAR"]);
    }

    [Fact]
    public void WithVirtualEnvironment_UsesAppDirectoryWhenVenvExistsThere()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempAppDir = new TestTempDirectory();

        // Create .venv in the app directory
        var appVenvPath = Path.Combine(tempAppDir.Path, ".venv");
        Directory.CreateDirectory(appVenvPath);

        var scriptName = "main.py";
        var resourceBuilder = builder.AddPythonApp("pythonProject", tempAppDir.Path, scriptName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        // Should use the app directory .venv since it exists there
        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempAppDir.Path));
        var expectedVenvPath = Path.Combine(expectedProjectDirectory, ".venv");

        AssertPythonCommandPath(expectedVenvPath, pythonProjectResource.Command);
    }

    [Fact]
    public void WithVirtualEnvironment_UsesAppHostDirectoryWhenVenvOnlyExistsThere()
    {
        using var tempProjectDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempProjectDir.Path;
        }).WithTestAndResourceLogging(outputHelper);
        
        // Create app directory as a subdirectory of AppHost (realistic scenario)
        var appDirName = "python-app";
        var appDirPath = Path.Combine(builder.AppHostDirectory, appDirName);
        Directory.CreateDirectory(appDirPath);

        // Create .venv in the AppHost directory (not in app directory)
        var appHostVenvPath = Path.Combine(builder.AppHostDirectory, ".venv");
        Directory.CreateDirectory(appHostVenvPath);

        try
        {
            var scriptName = "main.py";
            var resourceBuilder = builder.AddPythonApp("pythonProject", appDirName, scriptName);

            var app = builder.Build();
            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var executableResources = appModel.GetExecutableResources();

            var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

            // Should use the AppHost directory .venv since it only exists there
            AssertPythonCommandPath(appHostVenvPath, pythonProjectResource.Command);
        }
        finally
        {
            // Clean up
            if (Directory.Exists(appDirPath))
            {
                Directory.Delete(appDirPath, true);
            }
            if (Directory.Exists(appHostVenvPath))
            {
                Directory.Delete(appHostVenvPath, true);
            }
        }
    }

    [Fact]
    public void WithVirtualEnvironment_PrefersAppDirectoryWhenVenvExistsInBoth()
    {
        using var tempProjectDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempProjectDir.Path;
        }).WithTestAndResourceLogging(outputHelper);
        
        // Create app directory as a subdirectory of AppHost (realistic scenario)
        var appDirName = "python-app";
        var appDirPath = Path.Combine(builder.AppHostDirectory, appDirName);
        Directory.CreateDirectory(appDirPath);

        // Create .venv in both directories
        var appVenvPath = Path.Combine(appDirPath, ".venv");
        Directory.CreateDirectory(appVenvPath);

        var appHostVenvPath = Path.Combine(builder.AppHostDirectory, ".venv");
        Directory.CreateDirectory(appHostVenvPath);

        try
        {
            var scriptName = "main.py";
            var resourceBuilder = builder.AddPythonApp("pythonProject", appDirName, scriptName);

            var app = builder.Build();
            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var executableResources = appModel.GetExecutableResources();

            var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

            // Should prefer the app directory .venv when it exists in both locations
            AssertPythonCommandPath(appVenvPath, pythonProjectResource.Command);
        }
        finally
        {
            // Clean up
            if (Directory.Exists(appDirPath))
            {
                Directory.Delete(appDirPath, true);
            }
            if (Directory.Exists(appHostVenvPath))
            {
                Directory.Delete(appHostVenvPath, true);
            }
        }
    }

    [Fact]
    public void WithVirtualEnvironment_DefaultsToAppDirectoryWhenVenvExistsInNeither()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempAppDir = new TestTempDirectory();

        // Don't create .venv in either directory

        var scriptName = "main.py";
        var resourceBuilder = builder.AddPythonApp("pythonProject", tempAppDir.Path, scriptName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        // Should default to app directory when it doesn't exist in either location
        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempAppDir.Path));
        var expectedVenvPath = Path.Combine(expectedProjectDirectory, ".venv");

        AssertPythonCommandPath(expectedVenvPath, pythonProjectResource.Command);
    }

    [Fact]
    public void WithVirtualEnvironment_ExplicitPath_UsesVerbatim()
    {
        using var tempProjectDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(options =>
        {
            options.ProjectDirectory = tempProjectDir.Path;
        }).WithTestAndResourceLogging(outputHelper);
        
        // Create app directory as a subdirectory of AppHost
        var appDirName = "python-app";
        var appDirPath = Path.Combine(builder.AppHostDirectory, appDirName);
        Directory.CreateDirectory(appDirPath);

        // Create .venv in the AppHost directory
        var appHostVenvPath = Path.Combine(builder.AppHostDirectory, ".venv");
        Directory.CreateDirectory(appHostVenvPath);

        // Create a custom venv in the app directory
        var customVenvPath = Path.Combine(appDirPath, "custom-venv");
        Directory.CreateDirectory(customVenvPath);

        try
        {
            var scriptName = "main.py";
            
            // Explicitly specify a custom venv path - should use it verbatim, not fall back to AppHost .venv
            var resourceBuilder = builder.AddPythonApp("pythonProject", appDirName, scriptName)
                .WithVirtualEnvironment("custom-venv");

            var app = builder.Build();
            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var executableResources = appModel.GetExecutableResources();

            var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

            // Should use the explicitly specified path, NOT the AppHost .venv
            AssertPythonCommandPath(customVenvPath, pythonProjectResource.Command);
        }
        finally
        {
            // Clean up
            if (Directory.Exists(appDirPath))
            {
                Directory.Delete(appDirPath, true);
            }
            if (Directory.Exists(appHostVenvPath))
            {
                Directory.Delete(appHostVenvPath, true);
            }
        }
    }

    [Fact]
    public void WithUv_CreatesUvEnvironmentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the installer resource exists
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.Equal("pythonProject-installer", installerResource.Name);

        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempDir.Path));
        Assert.Equal(expectedProjectDirectory, installerResource.WorkingDirectory);

        // Verify the package manager annotation
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("uv", packageManager.ExecutableName);

        // Verify the install command annotation
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        var arg = Assert.Single(installAnnotation.Args);
        Assert.Equal("sync", arg);
    }

    [Fact]
    public async Task WithUv_AddsUvSyncArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the install command annotation has the correct args
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        var arg = Assert.Single(installAnnotation.Args);
        Assert.Equal("sync", arg);
    }

    [Fact]
    public async Task WithUv_AddsWaitForCompletionRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Manually trigger BeforeStartEvent to wire up wait dependencies
        await PublishBeforeStartEventAsync(app);

        var pythonAppResource = appModel.Resources.OfType<PythonAppResource>().Single();
        var uvEnvironmentResource = appModel.Resources.OfType<PythonInstallerResource>().Single();

        var waitAnnotations = pythonAppResource.Annotations.OfType<WaitAnnotation>();
        var waitForCompletionAnnotation = Assert.Single(waitAnnotations);
        Assert.Equal(uvEnvironmentResource, waitForCompletionAnnotation.Resource);
        Assert.Equal(WaitType.WaitForCompletion, waitForCompletionAnnotation.WaitType);
    }

    [Fact]
    public void WithUv_ThrowsOnNullBuilder()
    {
        IResourceBuilder<PythonAppResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.WithUv());

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void WithUv_IsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        // Call WithUv twice
        var pythonBuilder = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithUv()
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that only one UV environment resource was created
        var uvEnvironmentResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.Equal("pythonProject-installer", uvEnvironmentResource.Name);
    }

    [Fact]
    public void InstallerResourceHasCertificateTrustScopeNone()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the installer resource exists
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.Equal("pythonProject-installer", installerResource.Name);

        // Verify the installer has CertificateTrustScope.None
        Assert.True(installerResource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(CertificateTrustScope.None, certAnnotation.Scope);
    }

    [Fact]
    public void WithPip_AfterWithUv_ReplacesPackageManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        // Call WithUv then WithPip - WithPip should replace WithUv
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithUv()
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that only one installer resource was created
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.Equal("pythonProject-installer", installerResource.Name);

        // Verify that pip is the active package manager (not uv)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify the install command is for pip (not uv sync)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal("install", installAnnotation.Args[0]);
        Assert.Equal("-r", installAnnotation.Args[1]);
        Assert.Equal("requirements.txt", installAnnotation.Args[2]);
    }

    [Fact]
    public void WithUv_AfterWithPip_ReplacesPackageManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        // Call WithPip then WithUv - WithUv should replace WithPip
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithPip()
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that only one installer resource was created
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.Equal("pythonProject-installer", installerResource.Name);

        // Verify that uv is the active package manager (not pip)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("uv", packageManager.ExecutableName);

        // Verify the install command is for uv (not pip install)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        var arg = Assert.Single(installAnnotation.Args);
        Assert.Equal("sync", arg);
    }

    [Fact]
    public void AddPythonApp_CreatesResourceWithScriptEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonApp("python-script", tempDir.Path, "main.py");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());
        Assert.Equal("python-script", resource.Name);

        var entrypointAnnotation = resource.Annotations.OfType<PythonEntrypointAnnotation>().Single();
        Assert.Equal(EntrypointType.Script, entrypointAnnotation.Type);
        Assert.Equal("main.py", entrypointAnnotation.Entrypoint);
    }

    [Fact]
    public void AddPythonModule_CreatesResourceWithModuleEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonModule("flask-app", tempDir.Path, "flask");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());
        Assert.Equal("flask-app", resource.Name);

        var entrypointAnnotation = resource.Annotations.OfType<PythonEntrypointAnnotation>().Single();
        Assert.Equal(EntrypointType.Module, entrypointAnnotation.Type);
        Assert.Equal("flask", entrypointAnnotation.Entrypoint);
    }

    [Fact]
    public void AddPythonExecutable_CreatesResourceWithExecutableEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonExecutable("pytest", tempDir.Path, "pytest");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());
        Assert.Equal("pytest", resource.Name);

        var entrypointAnnotation = resource.Annotations.OfType<PythonEntrypointAnnotation>().Single();
        Assert.Equal(EntrypointType.Executable, entrypointAnnotation.Type);
        Assert.Equal("pytest", entrypointAnnotation.Entrypoint);
    }

    [Fact]
    public async Task AddPythonApp_SetsCorrectCommandAndArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        Assert.Equal("pythonProject", pythonProjectResource.Name);

        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempDir.Path));
        Assert.Equal(expectedProjectDirectory, pythonProjectResource.WorkingDirectory);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        Assert.Single(commandArguments);
        Assert.Equal(scriptName, commandArguments[0]);
    }

    [Fact]
    public async Task AddPythonModule_SetsCorrectCommandAndArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var moduleName = "flask";

        builder.AddPythonModule("pythonProject", tempDir.Path, moduleName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempDir.Path));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "Scripts", "python.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "bin", "python"), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        Assert.Equal(2, commandArguments.Count);
        Assert.Equal("-m", commandArguments[0]);
        Assert.Equal(moduleName, commandArguments[1]);
    }

    [Fact]
    public async Task AddPythonExecutable_SetsCorrectCommandAndArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var executableName = "pytest";

        builder.AddPythonExecutable("pythonProject", tempDir.Path, executableName);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executableResources = appModel.GetExecutableResources();

        var pythonProjectResource = Assert.Single(executableResources.OfType<PythonAppResource>());

        var expectedProjectDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, tempDir.Path));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "Scripts", $"{executableName}.exe"), pythonProjectResource.Command);
        }
        else
        {
            Assert.Equal(Path.Join(expectedProjectDirectory, ".venv", "bin", executableName), pythonProjectResource.Command);
        }

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(pythonProjectResource, TestServiceProvider.Instance);

        // Executable doesn't add entrypoint to args
        Assert.Empty(commandArguments);
    }

    [Fact]
    public async Task AddPythonModule_WithArgs_AddsArgumentsCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonModule("flask-app", tempDir.Path, "flask")
            .WithArgs("run", "--debug", "--host=0.0.0.0");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, TestServiceProvider.Instance);

        Assert.Equal(5, commandArguments.Count);
        Assert.Equal("-m", commandArguments[0]);
        Assert.Equal("flask", commandArguments[1]);
        Assert.Equal("run", commandArguments[2]);
        Assert.Equal("--debug", commandArguments[3]);
        Assert.Equal("--host=0.0.0.0", commandArguments[4]);
    }

    [Fact]
    public async Task AddPythonApp_WithArgs_AddsArgumentsCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonApp("python-app", tempDir.Path, "main.py")
            .WithArgs("arg1", "arg2");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, TestServiceProvider.Instance);

        Assert.Equal(3, commandArguments.Count);
        Assert.Equal("main.py", commandArguments[0]);
        Assert.Equal("arg1", commandArguments[1]);
        Assert.Equal("arg2", commandArguments[2]);
    }

    [Fact]
    public async Task AddPythonExecutable_WithArgs_AddsArgumentsCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonBuilder = builder.AddPythonExecutable("pytest", tempDir.Path, "pytest")
            .WithArgs("-q", "--verbose");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());

        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, TestServiceProvider.Instance);

        Assert.Equal(2, commandArguments.Count);
        Assert.Equal("-q", commandArguments[0]);
        Assert.Equal("--verbose", commandArguments[1]);
    }

    [Fact]
    public async Task WithEntrypoint_ChangesEntrypointTypeAndValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        // Start with a script
        var pythonBuilder = builder.AddPythonApp("python-app", tempDir.Path, "main.py")
            .WithArgs("arg1", "arg2");

        // Change to a module
        pythonBuilder.WithEntrypoint(EntrypointType.Module, "uvicorn")
            .WithArgs("main:app", "--reload");

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PythonAppResource>());

        // Verify the entrypoint was updated
        var entrypointAnnotation = resource.Annotations.OfType<PythonEntrypointAnnotation>().Single();
        Assert.Equal(EntrypointType.Module, entrypointAnnotation.Type);
        Assert.Equal("uvicorn", entrypointAnnotation.Entrypoint);

        // Verify arguments
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, TestServiceProvider.Instance);

        Assert.Equal(4, commandArguments.Count);
        Assert.Equal("-m", commandArguments[0]);
        Assert.Equal("uvicorn", commandArguments[1]);
        Assert.Equal("main:app", commandArguments[2]);
        Assert.Equal("--reload", commandArguments[3]);
    }

    [Fact]
    public void WithEntrypoint_UpdatesCommandForExecutableType()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        // Start with a script
        var pythonBuilder = builder.AddPythonApp("python-app", tempDir.Path, "main.py");

        // Get the initial command (should be python executable)
        var initialCommand = pythonBuilder.Resource.Command;

        // Change to an executable
        pythonBuilder.WithEntrypoint(EntrypointType.Executable, "pytest");

        var newCommand = pythonBuilder.Resource.Command;

        // Commands should be different - one is python, one is pytest
        Assert.NotEqual(initialCommand, newCommand);
        Assert.Contains("pytest", newCommand);
    }

    [Fact]
    public void WithEntrypoint_ThrowsWhenVirtualEnvironmentNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);

        // Create a resource without going through AddPythonApp (missing annotations)
        var resource = new PythonAppResource("test", "python", "/tmp");
        var resourceBuilder = builder.CreateResourceBuilder(resource);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resourceBuilder.WithEntrypoint(EntrypointType.Module, "flask"));

        Assert.Contains("Python environment annotation", exception.Message);
    }

    [Fact]
    public void WithEntrypoint_ThrowsOnNullBuilder()
    {
        IResourceBuilder<PythonAppResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.WithEntrypoint(EntrypointType.Module, "flask"));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void WithEntrypoint_ThrowsOnNullOrEmptyEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var resourceBuilder = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py");

        var nullException = Assert.Throws<ArgumentNullException>(() =>
            resourceBuilder.WithEntrypoint(EntrypointType.Module, null!));
        Assert.Equal("entrypoint", nullException.ParamName);

        var emptyException = Assert.Throws<ArgumentException>(() =>
            resourceBuilder.WithEntrypoint(EntrypointType.Module, string.Empty));
        Assert.Equal("entrypoint", emptyException.ParamName);
    }

    [Fact]
    public async Task WithUv_GeneratesDockerfileInPublishMode()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a UV-based Python project with pyproject.toml and uv.lock
        var pyprojectContent = """
            [project]
            name = "test-app"
            version = "0.1.0"
            requires-python = ">=3.12"
            dependencies = []

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"
            """;

        var uvLockContent = """
            version = 1
            requires-python = ">=3.12"
            """;

        var scriptContent = """
            print("Hello from UV project!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "pyproject.toml"), pyprojectContent);
        File.WriteAllText(Path.Combine(projectDirectory, "uv.lock"), uvLockContent);
        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        var manifestPath = Path.Combine(projectDirectory, "aspire-manifest.json");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources with different entrypoint types
        builder.AddPythonApp("script-app", projectDirectory, "main.py")
            .WithUv();

        builder.AddPythonModule("module-app", projectDirectory, "mymodule")
            .WithUv();

        builder.AddPythonExecutable("executable-app", projectDirectory, "pytest")
            .WithUv();

        var app = builder.Build();

        app.Run();

        // Verify that Dockerfiles were generated for each entrypoint type
        var scriptDockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(scriptDockerfilePath), "Dockerfile should be generated for script entrypoint");

        var moduleDockerfilePath = Path.Combine(outputDir.Path, "module-app.Dockerfile");
        Assert.True(File.Exists(moduleDockerfilePath), "Dockerfile should be generated for module entrypoint");

        var executableDockerfilePath = Path.Combine(outputDir.Path, "executable-app.Dockerfile");
        Assert.True(File.Exists(executableDockerfilePath), "Dockerfile should be generated for executable entrypoint");

        var scriptDockerfileContent = File.ReadAllText(scriptDockerfilePath);
        var moduleDockerfileContent = File.ReadAllText(moduleDockerfilePath);
        var executableDockerfileContent = File.ReadAllText(executableDockerfilePath);

        await Verify(scriptDockerfileContent)
            .AppendContentAsFile(moduleDockerfileContent)
            .AppendContentAsFile(executableDockerfileContent);
    }

    [Fact]
    public async Task WithUv_GeneratesDockerfileInPublishMode_WithoutUvLock()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a UV-based Python project with pyproject.toml but NO uv.lock
        var pyprojectContent = """
            [project]
            name = "test-app"
            version = "0.1.0"
            requires-python = ">=3.12"
            dependencies = []

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"
            """;

        var scriptContent = """
            print("Hello from UV project!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "pyproject.toml"), pyprojectContent);
        // Note: NO uv.lock file created
        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        var manifestPath = Path.Combine(projectDirectory, "aspire-manifest.json");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources with different entrypoint types
        builder.AddPythonApp("script-app", projectDirectory, "main.py")
            .WithUv();

        builder.AddPythonModule("module-app", projectDirectory, "mymodule")
            .WithUv();

        builder.AddPythonExecutable("executable-app", projectDirectory, "pytest")
            .WithUv();

        var app = builder.Build();

        app.Run();

        // Verify that Dockerfiles were generated for each entrypoint type
        var scriptDockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(scriptDockerfilePath), "Dockerfile should be generated for script entrypoint");

        var moduleDockerfilePath = Path.Combine(outputDir.Path, "module-app.Dockerfile");
        Assert.True(File.Exists(moduleDockerfilePath), "Dockerfile should be generated for module entrypoint");

        var executableDockerfilePath = Path.Combine(outputDir.Path, "executable-app.Dockerfile");
        Assert.True(File.Exists(executableDockerfilePath), "Dockerfile should be generated for executable entrypoint");

        var scriptDockerfileContent = File.ReadAllText(scriptDockerfilePath);
        var moduleDockerfileContent = File.ReadAllText(moduleDockerfilePath);
        var executableDockerfileContent = File.ReadAllText(executableDockerfilePath);

        // Verify the Dockerfiles don't use --locked flag
        Assert.DoesNotContain("--locked", scriptDockerfileContent);
        Assert.DoesNotContain("--locked", moduleDockerfileContent);
        Assert.DoesNotContain("--locked", executableDockerfileContent);

        await Verify(scriptDockerfileContent)
            .AppendContentAsFile(moduleDockerfileContent)
            .AppendContentAsFile(executableDockerfileContent);
    }

    [Fact]
    public async Task WithDebugSupport_RemovesScriptArgumentForScriptEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["python"]
        };

        // Set DEBUG_SESSION_INFO to trigger VS Code debug support callback
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var scriptPath = "main.py";

        var pythonApp = builder.AddPythonApp("myapp", appDirectory, scriptPath)
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithArgs("arg1", "arg2");

        var app = builder.Build();

        var resource = pythonApp.Resource;

        // Use ArgumentEvaluator to get the resolved argument list (after callbacks are applied)
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, app.Services);

        // Verify the script path was removed but other args remain
        Assert.Collection(commandArguments,
            arg => Assert.Equal("arg1", arg),
            arg => Assert.Equal("arg2", arg));
    }

    [Fact]
    public async Task WithDebugSupport_DoesntRemoveScriptArgumentForScriptEntrypoint_WhenResourceTypeNotSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // Set DEBUG_SESSION_INFO to trigger VS Code debug support callback
        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"]
        };

        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var scriptPath = "main.py";

        var pythonApp = builder.AddPythonApp("myapp", appDirectory, scriptPath)
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithArgs("arg1", "arg2");

        var app = builder.Build();

        var resource = pythonApp.Resource;

        // Use ArgumentEvaluator to get the resolved argument list (after callbacks are applied)
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, app.Services);

        // Verify the script path was removed but other args remain
        Assert.Collection(commandArguments,
            arg => Assert.Equal("main.py", arg),
            arg => Assert.Equal("arg1", arg),
            arg => Assert.Equal("arg2", arg));
    }

    [Fact]
    public async Task WithDebugSupport_RemovesModuleArgumentsForModuleEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["python"]
        };

        // Set DEBUG_SESSION_INFO to trigger VS Code debug support callback
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var moduleName = "flask";

        var pythonApp = builder.AddPythonModule("myapp", appDirectory, moduleName)
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithArgs("run");

        var app = builder.Build();

        var resource = pythonApp.Resource;

        // Use ArgumentEvaluator to get the resolved argument list (after callbacks are applied)
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, app.Services);

        // Verify "-m" and module name were removed but other args remain
        Assert.Collection(commandArguments,
            arg => Assert.Equal("run", arg));
    }

    [Fact]
    public void WithDebugSupport_PopulatesWorkingDirectory_ForScriptEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);

        var pythonApp = builder.AddPythonApp("myapp", appDirectory, "main.py")
            .WithVirtualEnvironment(virtualEnvironmentPath);

        var launchConfig = InvokeLaunchConfigurationAnnotator(pythonApp.Resource);

        Assert.Equal(appDirectory, launchConfig.WorkingDirectory);
        Assert.Equal(Path.Combine(appDirectory, "main.py"), launchConfig.ProgramPath);
        Assert.Equal(string.Empty, launchConfig.Module);
    }

    [Fact]
    public void WithDebugSupport_PopulatesWorkingDirectory_ForModuleEntrypoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);

        var pythonApp = builder.AddPythonModule("myapp", appDirectory, "flask")
            .WithVirtualEnvironment(virtualEnvironmentPath);

        var launchConfig = InvokeLaunchConfigurationAnnotator(pythonApp.Resource);

        Assert.Equal(appDirectory, launchConfig.WorkingDirectory);
        Assert.Equal("flask", launchConfig.Module);
        // ProgramPath must be empty for module entrypoints; the working_directory field
        // now carries the cwd to the debugger instead of being smuggled through program_path.
        Assert.Equal(string.Empty, launchConfig.ProgramPath);
    }

    [Fact]
    public void WithDebugSupport_PropagatesWorkingDirectoryOverride()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var customWorkingDirectory = Path.Combine(tempDir.Path, "custom");
        Directory.CreateDirectory(customWorkingDirectory);

        var pythonApp = builder.AddPythonApp("myapp", appDirectory, "main.py")
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithWorkingDirectory(customWorkingDirectory);

        var launchConfig = InvokeLaunchConfigurationAnnotator(pythonApp.Resource);

        var expectedWorkingDirectory = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.Combine(builder.AppHostDirectory, customWorkingDirectory));
        Assert.Equal(expectedWorkingDirectory, launchConfig.WorkingDirectory);
        Assert.Equal(Path.Combine(expectedWorkingDirectory, "main.py"), launchConfig.ProgramPath);
    }

    private static PythonLaunchConfiguration InvokeLaunchConfigurationAnnotator(IResource resource)
    {
        Assert.True(resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));

        var exe = Executable.Create("test", "python");
        supportsDebugging.LaunchConfigurationAnnotator(exe, ExecutableLaunchMode.Debug);

        Assert.True(exe.TryGetAnnotationAsObjectList<PythonLaunchConfiguration>(
            Executable.LaunchConfigurationsAnnotation,
            out var launchConfigs));
        return Assert.Single(launchConfigs);
    }

    [Fact]
    public async Task WithDebugSupport_DoesntRemoveModuleArgumentsForModuleEntrypoint_WhenResourceTypeNotSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        var runSessionInfo = new RunSessionInfo
        {
            ProtocolsSupported = ["test"]
        };

        // Set DEBUG_SESSION_INFO to trigger VS Code debug support callback
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(runSessionInfo);
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var moduleName = "flask";

        var pythonApp = builder.AddPythonModule("myapp", appDirectory, moduleName)
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithArgs("run");

        var app = builder.Build();

        var resource = pythonApp.Resource;

        // Use ArgumentEvaluator to get the resolved argument list (after callbacks are applied)
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, app.Services);

        // Verify "-m" and module name were removed but other args remain
        Assert.Collection(commandArguments,
            arg => Assert.Equal("-m", arg),
            arg => Assert.Equal("flask", arg),
            arg => Assert.Equal("run", arg));
    }

    [Fact]
    public async Task WithDebugSupport_ExecutableTypeDoesNotModifyArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TestTempDirectory();

        // Set DEBUG_SESSION_INFO to trigger VS Code debug support callback
        builder.Configuration["DEBUG_SESSION_INFO"] = "{}";

        var appDirectory = Path.Combine(tempDir.Path, "myapp");
        Directory.CreateDirectory(appDirectory);
        var virtualEnvironmentPath = Path.Combine(tempDir.Path, ".venv");
        Directory.CreateDirectory(virtualEnvironmentPath);
        var executableName = "myexe";

        var pythonApp = builder.AddPythonExecutable("myapp", appDirectory, executableName)
            .WithVirtualEnvironment(virtualEnvironmentPath)
            .WithArgs("arg1", "arg2");

        var resource = pythonApp.Resource;

        var app = builder.Build();

        // Use ArgumentEvaluator to get the resolved argument list (after callbacks are applied)
        var commandArguments = await ArgumentEvaluator.GetArgumentListAsync(resource, TestServiceProvider.Instance);

        // For executable type, no args are removed (no debug support callback)
        Assert.Collection(commandArguments,
            arg => Assert.Equal("arg1", arg),
            arg => Assert.Equal("arg2", arg));
    }

    [Fact]
    public async Task PythonApp_SetsPythonUtf8EnvironmentVariable_OnWindowsInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py");

        var app = builder.Build();
        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pythonApp.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("1", environmentVariables["PYTHONUTF8"]);
        }
        else
        {
            Assert.False(environmentVariables.ContainsKey("PYTHONUTF8"));
        }
    }

    [Fact]
    public async Task PythonApp_DoesNotSetPythonUtf8EnvironmentVariable_OnNonWindowsPlatforms()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py");

        var app = builder.Build();
        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pythonApp.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(environmentVariables.ContainsKey("PYTHONUTF8"));
        }
    }

    [Fact]
    public async Task PythonApp_DoesNotSetPythonUtf8EnvironmentVariable_InPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py");

        var app = builder.Build();
        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            pythonApp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // PYTHONUTF8 should not be set in Publish mode, even on Windows
        Assert.False(environmentVariables.ContainsKey("PYTHONUTF8"));
    }

    [Fact]
    public async Task WithUv_CustomBaseImages_GeneratesDockerfileWithCustomImages()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a UV-based Python project with pyproject.toml and uv.lock
        var pyprojectContent = """
            [project]
            name = "test-app"
            version = "0.1.0"
            requires-python = ">=3.12"
            dependencies = []

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"
            """;

        var uvLockContent = """
            version = 1
            requires-python = ">=3.12"
            """;

        var scriptContent = """
            print("Hello from UV project with custom images!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "pyproject.toml"), pyprojectContent);
        File.WriteAllText(Path.Combine(projectDirectory, "uv.lock"), uvLockContent);
        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resource with custom base images
        builder.AddPythonApp("custom-images-app", projectDirectory, "main.py")
            .WithUv()
            .WithDockerfileBaseImage(
                buildImage: "ghcr.io/astral-sh/uv:python3.13-bookworm",
                runtimeImage: "python:3.13-slim");

        var app = builder.Build();
        app.Run();

        // Verify that Dockerfile was generated
        var dockerfilePath = Path.Combine(outputDir.Path, "custom-images-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated");

        var dockerfileContent = File.ReadAllText(dockerfilePath);

        // Verify the custom build image is used
        Assert.Contains("FROM ghcr.io/astral-sh/uv:python3.13-bookworm AS builder", dockerfileContent);

        // Verify the custom runtime image is used
        Assert.Contains("FROM python:3.13-slim AS app", dockerfileContent);
    }

    [Fact]
    public async Task FallbackDockerfile_GeneratesDockerfileWithoutUv_WithRequirementsTxt()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a Python project without UV but with requirements.txt
        var requirementsContent = """
            flask==3.0.0
            requests==2.31.0
            """;

        var scriptContent = """
            print("Hello from non-UV project!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "requirements.txt"), requirementsContent);
        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources without UV environment
        builder.AddPythonApp("script-app", projectDirectory, "main.py");

        var app = builder.Build();
        app.Run();

        // Verify that Dockerfile was generated
        var dockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated for non-UV Python app");

        var dockerfileContent = File.ReadAllText(dockerfilePath);

        // Verify it's a fallback Dockerfile (single stage, no UV)
        Assert.DoesNotContain("uv sync", dockerfileContent);
        Assert.DoesNotContain("ghcr.io/astral-sh/uv", dockerfileContent);

        // Verify it uses pip install for requirements.txt
        Assert.Contains("pip install --no-cache-dir -r requirements.txt", dockerfileContent);

        // Verify it uses the same runtime image as UV workflow
        Assert.Contains("FROM python:3.13-slim-bookworm", dockerfileContent);

        await Verify(dockerfileContent);
    }

    [Fact]
    public async Task FallbackDockerfile_GeneratesDockerfileWithPyprojectToml()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a Python project without UV but with pyproject.toml
        var scriptContent = """
            print("Hello from non-UV project with pyproject.toml!")
            """;

        var pyprojectContent = """
            [project]
            name = "test-app"
            version = "0.1.0"
            requires-python = ">=3.11"
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);
        File.WriteAllText(Path.Combine(projectDirectory, "pyproject.toml"), pyprojectContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources without UV environment
        builder.AddPythonApp("script-app", projectDirectory, "main.py");

        var app = builder.Build();
        app.Run();

        // Verify that Dockerfile was generated
        var dockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated for non-UV Python app");

        var dockerfileContent = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContent);
    }

    [Fact]
    public async Task FallbackDockerfile_GeneratesDockerfileWithoutAnyDependencyFiles()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a Python project with NO pyproject.toml and NO requirements.txt
        var scriptContent = """
            print("Hello from Python app with no dependencies!")
            """;

        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources without UV environment
        builder.AddPythonApp("script-app", projectDirectory, "main.py");

        var app = builder.Build();
        app.Run();

        // Verify that Dockerfile was generated
        var dockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), "Dockerfile should be generated for Python app");

        var dockerfileContent = File.ReadAllText(dockerfilePath);

        await Verify(dockerfileContent);
    }

    [Fact]
    public async Task FallbackDockerfile_GeneratesDockerfileForAllEntrypointTypes()
    {
        using var sourceDir = new TestTempDirectory();
        using var outputDir = new TestTempDirectory();
        var projectDirectory = sourceDir.Path;

        // Create a Python project without UV
        var scriptContent = """
            print("Hello!")
            """;

        var pythonVersionContent = "3.12";

        File.WriteAllText(Path.Combine(projectDirectory, "main.py"), scriptContent);
        File.WriteAllText(Path.Combine(projectDirectory, ".python-version"), pythonVersionContent);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        // Add Python resources with different entrypoint types, none using UV
        builder.AddPythonApp("script-app", projectDirectory, "main.py");
        builder.AddPythonModule("module-app", projectDirectory, "mymodule");
        builder.AddPythonExecutable("executable-app", projectDirectory, "pytest");

        var app = builder.Build();
        app.Run();

        // Verify that Dockerfiles were generated for each entrypoint type
        var scriptDockerfilePath = Path.Combine(outputDir.Path, "script-app.Dockerfile");
        Assert.True(File.Exists(scriptDockerfilePath), "Dockerfile should be generated for script entrypoint");

        var moduleDockerfilePath = Path.Combine(outputDir.Path, "module-app.Dockerfile");
        Assert.True(File.Exists(moduleDockerfilePath), "Dockerfile should be generated for module entrypoint");

        var executableDockerfilePath = Path.Combine(outputDir.Path, "executable-app.Dockerfile");
        Assert.True(File.Exists(executableDockerfilePath), "Dockerfile should be generated for executable entrypoint");

        var scriptDockerfileContent = File.ReadAllText(scriptDockerfilePath);
        var moduleDockerfileContent = File.ReadAllText(moduleDockerfilePath);
        var executableDockerfileContent = File.ReadAllText(executableDockerfilePath);

        // Verify none use UV
        Assert.DoesNotContain("uv sync", scriptDockerfileContent);
        Assert.DoesNotContain("uv sync", moduleDockerfileContent);
        Assert.DoesNotContain("uv sync", executableDockerfileContent);

        // Verify correct entrypoints
        Assert.Contains("ENTRYPOINT [\"python\",\"main.py\"]", scriptDockerfileContent);
        Assert.Contains("ENTRYPOINT [\"python\",\"-m\",\"mymodule\"]", moduleDockerfileContent);
        Assert.Contains("ENTRYPOINT [\"pytest\"]", executableDockerfileContent);

        await Verify(scriptDockerfileContent)
            .AppendContentAsFile(moduleDockerfileContent)
            .AppendContentAsFile(executableDockerfileContent);
    }

    [Fact]
    public void AutoDetection_PyprojectToml_AddsPip()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create a pyproject.toml file
        var pyprojectPath = Path.Combine(tempDir.Path, "pyproject.toml");
        File.WriteAllText(pyprojectPath, "[project]\nname = \"test\"");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var app = builder.Build();

        // Verify that WithPip was automatically called (pip supports pyproject.toml)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify that the install command uses pyproject.toml
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal("install", installAnnotation.Args[0]);
        Assert.Equal(".", installAnnotation.Args[1]);

        // Verify that WithPip created the installer resource
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.NotNull(installerResource);
        Assert.Equal("pythonProject-installer", installerResource.Name);
    }

    [Fact]
    public void AutoDetection_RequirementsTxt_AddsPip()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create a requirements.txt file
        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests==2.31.0");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var app = builder.Build();

        // Verify that WithPip was automatically called
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify that the install command uses requirements.txt
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal("install", installAnnotation.Args[0]);
        Assert.Equal("-r", installAnnotation.Args[1]);
        Assert.Equal("requirements.txt", installAnnotation.Args[2]);

        // Verify that WithPip created the installer resource
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.NotNull(installerResource);
        Assert.Equal("pythonProject-installer", installerResource.Name);
    }

    [Fact]
    public void AutoDetection_PyprojectToml_TakesPrecedenceOverRequirementsTxt()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create both pyproject.toml and requirements.txt
        var pyprojectPath = Path.Combine(tempDir.Path, "pyproject.toml");
        File.WriteAllText(pyprojectPath, "[project]\nname = \"test\"");
        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests==2.31.0");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var app = builder.Build();

        // Verify that WithPip was automatically called
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify the install command uses pyproject.toml (takes precedence)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal("install", installAnnotation.Args[0]);
        Assert.Equal(".", installAnnotation.Args[1]);
    }

    [Fact]
    public void AutoDetection_NoConfigFile_DoesNotAddPackageManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, scriptName);

        var app = builder.Build();

        // Verify that no package manager was automatically added
        Assert.False(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out _));

        // Verify that no installer resource was created
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.Null(installerResource);
    }

    [Fact]
    public void WithVirtualEnvironment_DisableCreation_DoesNotCreateVenvCreator()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Add Python script with venv but disable automatic creation
        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: false);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that no venv creator resource was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.Null(venvCreatorResource);
    }

    [Fact]
    public void WithVirtualEnvironment_EnableCreation_CreatesVenvCreator()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create a requirements.txt to trigger pip installation
        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // Add Python script with venv and enable automatic creation (default)
        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: true);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that a venv creator resource was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);
        Assert.Equal("pythonProject-venv-creator", venvCreatorResource.Name);
    }

    [Fact]
    public void WithVirtualEnvironment_DefaultBehavior_CreatesVenvCreator()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create a requirements.txt to trigger pip installation
        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // Add Python script with venv using default behavior (createIfNotExists defaults to true)
        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(tempVenvDir.Path);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify that a venv creator resource was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);
        Assert.Equal("pythonProject-venv-creator", venvCreatorResource.Name);
    }

    // ===== Method Ordering Tests =====
    // These tests verify that WithPip, WithUv, and WithVirtualEnvironment work correctly in any order

    [Fact]
    public void WithUv_DisablesVenvCreation_And_SetsPackageManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify uv is the package manager
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("uv", packageManager.ExecutableName);

        // Verify NO venv creator was created (uv handles venv itself)
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.Null(venvCreatorResource);

        // Verify installer exists
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.NotNull(installerResource);
    }

    [Fact]
    public async Task WithPip_CreatesDefaultVenv_And_WaitsForVenvCreation()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        // Create requirements.txt
        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Manually trigger BeforeStartEvent to wire up wait dependencies
        await PublishBeforeStartEventAsync(app);

        // Verify pip is the package manager
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify default .venv was created
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var envAnnotation));
        Assert.NotNull(envAnnotation.VirtualEnvironment);
        Assert.Contains(".venv", envAnnotation.VirtualEnvironment.VirtualEnvironmentPath);

        // Verify venv creator was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);

        // Verify installer exists and waits for venv creator
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        var installerWaits = installerResource.Annotations.OfType<WaitAnnotation>()
            .Any(w => w.Resource == venvCreatorResource);
        Assert.True(installerWaits);
    }

    [Fact]
    public void WithPip_ThenWithVirtualEnvironment_CreateIfNotExistsTrue_CreatesVenv()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip()
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: true);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify venv annotation
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var envAnnotation));
        Assert.NotNull(envAnnotation.VirtualEnvironment);
        Assert.True(envAnnotation.CreateVenvIfNotExists);

        // Verify venv creator was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);
    }

    [Fact]
    public void WithPip_ThenWithVirtualEnvironment_CreateIfNotExistsFalse_DoesNotCreateVenv()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip()
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: false);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify venv annotation with createIfNotExists: false
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var envAnnotation));
        Assert.NotNull(envAnnotation.VirtualEnvironment);
        Assert.False(envAnnotation.CreateVenvIfNotExists);

        // Verify NO venv creator was created
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.Null(venvCreatorResource);

        // Verify installer still exists
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.NotNull(installerResource);
    }

    [Fact]
    public async Task MethodOrdering_WithPip_WithVirtualEnvironment_CreateTrue_WithPip_CreatesVenv()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // WithPip → WithVirtualEnvironment(createIfNotExists: true) → WithPip again
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip()
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: true)
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Manually trigger BeforeStartEvent to wire up wait dependencies
        await PublishBeforeStartEventAsync(app);

        // Verify venv creator was created (createIfNotExists: true persists)
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);

        // Verify installer waits for venv creator
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        var installerWaits = installerResource.Annotations.OfType<WaitAnnotation>()
            .Any(w => w.Resource == venvCreatorResource);
        Assert.True(installerWaits);
    }

    [Fact]
    public void MethodOrdering_WithPip_WithVirtualEnvironment_CreateFalse_WithPip_DoesNotCreateVenv()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // WithPip → WithVirtualEnvironment(createIfNotExists: false) → WithPip again
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip()
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: false)
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify NO venv creator was created (createIfNotExists: false persists)
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.Null(venvCreatorResource);

        // Verify installer still exists
        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().SingleOrDefault();
        Assert.NotNull(installerResource);
    }

    [Fact]
    public void MethodOrdering_WithPip_ThenWithUv_ReplacesPackageManager_And_DisablesVenvCreation()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // WithPip → WithUv (uv should replace pip and disable venv creation)
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip()
            .WithUv();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify uv is the package manager (replaced pip)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("uv", packageManager.ExecutableName);

        // Verify NO venv creator (uv disables venv creation)
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.Null(venvCreatorResource);

        // Verify only one installer exists
        Assert.Single(appModel.Resources.OfType<PythonInstallerResource>());
    }

    [Fact]
    public void MethodOrdering_WithUv_ThenWithPip_ReplacesPackageManager_And_EnablesVenvCreation()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        // WithUv → WithPip (pip should replace uv and enable venv creation)
        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithUv()
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify pip is the package manager (replaced uv)
        Assert.True(pythonApp.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager));
        Assert.Contains("pip", packageManager.ExecutableName);

        // Verify venv creator was created (pip enables venv creation)
        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);

        // Verify only one installer exists
        Assert.Single(appModel.Resources.OfType<PythonInstallerResource>());
    }

    [Fact]
    public async Task WithPip_InstallFalse_CreatesInstallerWithExplicitStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithPip(install: false);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Manually trigger BeforeStartEvent to wire up dependencies
        await PublishBeforeStartEventAsync(app);

        // Verify installer resource WAS created
        var installerResource = Assert.Single(appModel.Resources.OfType<PythonInstallerResource>());
        Assert.Equal("pythonProject-installer", installerResource.Name);

        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));

        // Verify the app does NOT wait for the installer
        var pythonAppResource = appModel.Resources.OfType<PythonAppResource>().Single();
        var waitAnnotations = pythonAppResource.Annotations.OfType<WaitAnnotation>().ToList();
        
        // Should not wait for installer, only for venv creator
        Assert.All(waitAnnotations, wait => Assert.NotEqual(installerResource, wait.Resource));
    }

    [Fact]
    public async Task WithUv_InstallFalse_CreatesInstallerWithExplicitStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptPath = Path.Combine(tempDir.Path, "main.py");
        File.WriteAllText(scriptPath, "print('Hello')");

        var pythonApp = builder.AddPythonApp("pythonProject", tempDir.Path, "main.py")
            .WithUv(install: false);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Manually trigger BeforeStartEvent to wire up dependencies
        await PublishBeforeStartEventAsync(app);

        // Verify installer resource WAS created
        var installerResource = Assert.Single(appModel.Resources.OfType<PythonInstallerResource>());
        Assert.Equal("pythonProject-installer", installerResource.Name);

        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));

        // Verify the app does NOT wait for the installer
        var pythonAppResource = appModel.Resources.OfType<PythonAppResource>().Single();
        var waitAnnotations = pythonAppResource.Annotations.OfType<WaitAnnotation>().ToList();
        
        // Should not have any wait annotations since uv doesn't create venv
        Assert.Empty(waitAnnotations);
    }

    [Fact]
    public void InstallerResourceHasNameValidationPolicyAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();

        var scriptName = "main.py";

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithPip();

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var installerResource = appModel.Resources.OfType<PythonInstallerResource>().Single();
        Assert.True(installerResource.TryGetLastAnnotation<NameValidationPolicyAnnotation>(out var policy));
        Assert.Same(NameValidationPolicyAnnotation.None, policy);
    }

    [Fact]
    public void VenvCreatorResourceHasNameValidationPolicyAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        using var tempDir = new TestTempDirectory();
        using var tempVenvDir = new TestTempDirectory();

        var scriptName = "main.py";
        var scriptPath = Path.Combine(tempDir.Path, scriptName);
        File.WriteAllText(scriptPath, "print('Hello')");

        var requirementsPath = Path.Combine(tempDir.Path, "requirements.txt");
        File.WriteAllText(requirementsPath, "requests");

        builder.AddPythonApp("pythonProject", tempDir.Path, scriptName)
            .WithVirtualEnvironment(tempVenvDir.Path, createIfNotExists: true);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var venvCreatorResource = appModel.Resources.OfType<PythonVenvCreatorResource>().SingleOrDefault();
        Assert.NotNull(venvCreatorResource);
        Assert.True(venvCreatorResource.TryGetLastAnnotation<NameValidationPolicyAnnotation>(out var policy));
        Assert.Same(NameValidationPolicyAnnotation.None, policy);
    }

    /// <summary>
    /// Helper method to manually trigger BeforeStartEvent for tests.
    /// This is needed because BeforeStartEvent is normally triggered during StartAsync(),
    /// but tests often build and assert on the model without starting the application.
    /// </summary>
    private static async Task PublishBeforeStartEventAsync(DistributedApplication app)
    {
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel), CancellationToken.None);
    }
}
