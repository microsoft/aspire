// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ProjectTemplatesIncrementalBuildTests : IDisposable
{
    private const string ReplacementOutputMarker = "Processed:";
    private const string CatalogFileName = "aspire-templates.cat";

    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;
    private readonly string _projectPath;
    private readonly string _templatesRoot;
    private readonly string _scriptPath;
    private readonly string _artifactsPath;
    private readonly string _packagesPath;
    private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal);

    public ProjectTemplatesIncrementalBuildTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _projectPath = Path.Combine(RepoRoot.Path, "src", "Aspire.ProjectTemplates", "Aspire.ProjectTemplates.csproj");
        _templatesRoot = Path.Combine(_workspace.Path, "templates");
        _scriptPath = Path.Combine(_workspace.Path, "replace-text.cs");
        _artifactsPath = Path.Combine(_workspace.Path, "artifacts");
        _packagesPath = Path.Combine(_workspace.Path, "packages");

        CopyDirectory(Path.Combine(RepoRoot.Path, "src", "Aspire.ProjectTemplates", "templates"), _templatesRoot);
        CreateTestReplacementScript(_scriptPath);

        _properties["_TemplateSourceRoot"] = EnsureTrailingDirectorySeparator(_templatesRoot);
        _properties["_ReplaceTextScriptPath"] = _scriptPath;
        _properties["BaseOutputPath"] = EnsureTrailingDirectorySeparator(Path.Combine(_artifactsPath, "bin"));
        _properties["BaseIntermediateOutputPath"] = EnsureTrailingDirectorySeparator(Path.Combine(_artifactsPath, "obj"));
        _properties["PackageOutputPath"] = _packagesPath;
        _properties["PackageVersion"] = "91.2.3-initial.1";
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task ReplacementIsIncrementalAndPackagedContentMatchesGeneratedTemplates()
    {
        (await RunDotNetAsync("restore")).EnsureSuccessful();

        AssertReplacementRan(await BuildAsync());

        var responseFile = GetSingleGeneratedFile("replace-text-args.rsp");
        Assert.True(
            new FileInfo(responseFile).Length > 8191,
            $"Expected the response file to exceed the Windows command-line limit: {responseFile}");

        AssertReplacementSkipped(await BuildAsync());

        AppendText(Path.Combine(_templatesRoot, "aspire-empty", "aspire.config.json"));
        AssertReplacementRan(await RunReplacementTargetAsync());

        AppendText(Path.Combine(
            _templatesRoot,
            "aspire-empty",
            "AspireApplication.1.AppHost",
            "AspireApplication.1.AppHost.csproj"));
        AssertReplacementRan(await RunReplacementTargetAsync());

        AppendText(Path.Combine(
            _templatesRoot,
            "aspire-empty",
            ".template.config",
            "template.json"));
        AssertReplacementRan(await RunReplacementTargetAsync());

        AppendText(Path.Combine(
            _templatesRoot,
            "aspire-empty",
            ".template.config",
            "localize",
            "templatestrings.fr.json"));
        AssertReplacementRan(await RunReplacementTargetAsync());

        AppendText(_scriptPath);
        AssertReplacementRan(await RunReplacementTargetAsync());

        var replacementProperties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PackageVersion"] = "91.3.4-properties.2",
            ["MicrosoftAspNetCoreOpenApiVersion"] = "101.0.1",
            ["MicrosoftAspNetCoreOpenApiPreviewVersion"] = "102.0.2",
            ["MicrosoftAspNetCoreOpenApiNet11Version"] = "103.0.3",
            ["MicrosoftExtensionsHttpResilienceVersion"] = "104.0.4",
            ["MicrosoftExtensionsServiceDiscoveryVersion"] = "105.0.5",
            ["OpenTelemetryExporterOpenTelemetryProtocolVersion"] = "106.0.6",
            ["OpenTelemetryInstrumentationExtensionsHostingVersion"] = "107.0.7",
            ["OpenTelemetryInstrumentationAspNetCoreVersion"] = "108.0.8",
            ["OpenTelemetryInstrumentationHttpVersion"] = "109.0.9",
            ["OpenTelemetryInstrumentationRuntimeVersion"] = "110.0.10"
        };

        foreach (var (property, value) in replacementProperties)
        {
            _properties[property] = value;
            (await RunPrepareResponseTargetAsync()).EnsureSuccessful();
            Assert.Contains(value, File.ReadAllText(responseFile));
        }
        Assert.Contains("91.3", File.ReadAllText(responseFile));
        AssertReplacementRan(await RunReplacementTargetAsync());

        var generatedProject = Path.Combine(
            GetGeneratedTemplatesRoot(),
            "aspire-empty",
            "AspireApplication.1.AppHost",
            "AspireApplication.1.AppHost.csproj");
        File.Delete(generatedProject);
        AssertReplacementRan(await RunReplacementTargetAsync());
        Assert.True(File.Exists(generatedProject));

        AssertReplacementRan(await RunDotNetAsync("build", "--no-restore", "-t:Rebuild"));

        (await RunDotNetAsync("clean")).EnsureSuccessful();
        Assert.Empty(Directory.GetFiles(_artifactsPath, "replace-text.stamp", SearchOption.AllDirectories));
        AssertReplacementRan(await BuildAsync());

        var originalScript = File.ReadAllText(_scriptPath);
        File.WriteAllText(
            _scriptPath,
            """
            // Licensed to the .NET Foundation under one or more agreements.
            // The .NET Foundation licenses this file to you under the MIT license.

            System.Environment.Exit(42);
            """);
        var failedBuild = await BuildAsync();
        Assert.NotEqual(0, failedBuild.ExitCode);
        File.WriteAllText(_scriptPath, originalScript);
        AssertReplacementRan(await BuildAsync());

        var packResult = await RunDotNetAsync("pack", "--no-restore", "-c", "Debug");
        packResult.EnsureSuccessful();

        AssertGeneratedAndPackagedContent(replacementProperties);
    }

    private async Task<CommandResult> BuildAsync()
        => await RunDotNetAsync("build", "--no-restore");

    private async Task<CommandResult> RunReplacementTargetAsync()
        => await RunDotNetAsync("msbuild", "-t:ReplacePackageVersionOnTemplates");

    private async Task<CommandResult> RunPrepareResponseTargetAsync()
        => await RunDotNetAsync("msbuild", "-t:PrepareReplaceTextResponseFile");

    private async Task<CommandResult> RunDotNetAsync(string command, params string[] commandArguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = RepoRoot.Path,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.ArgumentList.Add(_projectPath);
        foreach (var argument in commandArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.ArgumentList.Add("-v:normal");
        foreach (var (property, value) in _properties)
        {
            process.StartInfo.ArgumentList.Add($"-p:{property}={value}");
        }

        _output.WriteLine($"Executing: {process.StartInfo.FileName} {string.Join(' ', process.StartInfo.ArgumentList)}");

        process.Start();
        // Read both streams concurrently to avoid deadlock when a redirected pipe buffer fills.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        var combinedOutput = $"{output}{Environment.NewLine}{error}";
        _output.WriteLine(combinedOutput);

        return new CommandResult(process.StartInfo, process.ExitCode, combinedOutput);
    }

    private void AssertGeneratedAndPackagedContent(IReadOnlyDictionary<string, string> replacementProperties)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["!!REPLACE_WITH_LATEST_VERSION!!"] = replacementProperties["PackageVersion"],
            ["!!REPLACE_WITH_LATEST_MAJOR_MINOR_VERSION!!"] = "91.3",
            ["!!REPLACE_WITH_ASPNETCORE_OPENAPI_9_VERSION!!"] = replacementProperties["MicrosoftAspNetCoreOpenApiVersion"],
            ["!!REPLACE_WITH_ASPNETCORE_OPENAPI_10_VERSION!!"] = replacementProperties["MicrosoftAspNetCoreOpenApiPreviewVersion"],
            ["!!REPLACE_WITH_ASPNETCORE_OPENAPI_11_VERSION!!"] = replacementProperties["MicrosoftAspNetCoreOpenApiNet11Version"],
            ["!!REPLACE_WITH_DOTNET_EXTENSIONS_VERSION!!"] = replacementProperties["MicrosoftExtensionsHttpResilienceVersion"],
            ["!!REPLACE_WITH_SERVICE_DISCOVERY_VERSION!!"] = replacementProperties["MicrosoftExtensionsServiceDiscoveryVersion"],
            ["!!REPLACE_WITH_OTEL_EXPORTER_VERSION!!"] = replacementProperties["OpenTelemetryExporterOpenTelemetryProtocolVersion"],
            ["!!REPLACE_WITH_OTEL_HOSTING_VERSION!!"] = replacementProperties["OpenTelemetryInstrumentationExtensionsHostingVersion"],
            ["!!REPLACE_WITH_OTEL_ASPNETCORE_VERSION!!"] = replacementProperties["OpenTelemetryInstrumentationAspNetCoreVersion"],
            ["!!REPLACE_WITH_OTEL_HTTP_VERSION!!"] = replacementProperties["OpenTelemetryInstrumentationHttpVersion"],
            ["!!REPLACE_WITH_OTEL_RUNTIME_VERSION!!"] = replacementProperties["OpenTelemetryInstrumentationRuntimeVersion"]
        };

        var generatedRoot = GetGeneratedTemplatesRoot();
        var sourceFiles = Directory.GetFiles(_templatesRoot, "*", SearchOption.AllDirectories);
        Assert.Equal(sourceFiles.Length, Directory.GetFiles(generatedRoot, "*", SearchOption.AllDirectories).Length);

        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(_templatesRoot, sourceFile);
            var generatedFile = Path.Combine(generatedRoot, relativePath);
            Assert.True(File.Exists(generatedFile), $"Missing generated template: {relativePath}");

            if (IsReplacementOwnedFile(relativePath))
            {
                var expected = File.ReadAllText(sourceFile);
                foreach (var (find, replace) in replacements)
                {
                    expected = expected.Replace(find, replace, StringComparison.Ordinal);
                }

                Assert.Equal(expected, File.ReadAllText(generatedFile));
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(sourceFile), File.ReadAllBytes(generatedFile));
            }
        }

        var packagePath = Assert.Single(Directory.GetFiles(_packagesPath, "*.nupkg"));
        using var package = ZipFile.OpenRead(packagePath);
        var packagedTemplates = package.Entries
            .Where(entry => entry.FullName.StartsWith("content/templates/", StringComparison.Ordinal) &&
                            !entry.FullName.EndsWith('/'))
            .ToDictionary(
                entry => entry.FullName["content/templates/".Length..].Replace('/', Path.DirectorySeparatorChar),
                StringComparer.Ordinal);

        // GenerateCatalogFiles is Windows-only and additionally skips when makecat.exe is missing,
        // which is only a hard error for CI/official builds. Deriving the expectation from what the
        // build actually emitted keeps this assertion exercised on every OS instead of leaving a
        // Windows-only branch that never runs, and avoids demanding the Windows SDK locally.
        var generatedCatalogs = Directory.GetFiles(_artifactsPath, CatalogFileName, SearchOption.AllDirectories);
        Assert.True(
            generatedCatalogs.Length <= 1,
            $"Expected at most one generated catalog: {string.Join(", ", generatedCatalogs)}");

        Assert.Equal(sourceFiles.Length + generatedCatalogs.Length, packagedTemplates.Count);
        foreach (var generatedCatalog in generatedCatalogs)
        {
            Assert.True(
                packagedTemplates.TryGetValue(CatalogFileName, out var catalogEntry),
                $"Expected the generated catalog to be packaged: {generatedCatalog}");

            using var catalogStream = catalogEntry!.Open();
            using var catalogBytes = new MemoryStream();
            catalogStream.CopyTo(catalogBytes);
            Assert.Equal(File.ReadAllBytes(generatedCatalog), catalogBytes.ToArray());
        }

        foreach (var generatedFile in Directory.GetFiles(generatedRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(generatedRoot, generatedFile);
            var entry = packagedTemplates[relativePath];
            using var entryStream = entry.Open();
            using var memoryStream = new MemoryStream();
            entryStream.CopyTo(memoryStream);
            Assert.Equal(File.ReadAllBytes(generatedFile), memoryStream.ToArray());
        }
    }

    private string GetGeneratedTemplatesRoot()
    {
        var contentDirectories = Directory.GetDirectories(_artifactsPath, "content", SearchOption.AllDirectories);
        var contentDirectory = Assert.Single(contentDirectories);
        return Path.Combine(contentDirectory, "templates");
    }

    private string GetSingleGeneratedFile(string fileName)
        => Assert.Single(Directory.GetFiles(_artifactsPath, fileName, SearchOption.AllDirectories));

    private static bool IsReplacementOwnedFile(string relativePath)
    {
        var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var fileName = Path.GetFileName(relativePath);

        var normalizedDirectory = Path.GetDirectoryName(normalizedPath)?.Replace(Path.DirectorySeparatorChar, '/');

        return relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Equals("aspire-apphost-singlefile/apphost.cs", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Contains("/.template.config/", StringComparison.Ordinal) &&
               (normalizedDirectory?.EndsWith("/.template.config", StringComparison.Ordinal) == true ||
                fileName.StartsWith("templatestrings.", StringComparison.Ordinal) &&
                fileName.EndsWith(".json", StringComparison.Ordinal));
    }

    private static void AppendText(string path)
        => File.AppendAllText(path, $"{Environment.NewLine} ");

    private static string EnsureTrailingDirectorySeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : $"{path}{Path.DirectorySeparatorChar}";

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    private static void CreateTestReplacementScript(string path)
    {
        // The production response file has this shape, with one unquoted argument per line:
        //   --files
        //   C:\path with spaces\template.json
        //   --replacements
        //   token
        //   value
        // This package-free test script keeps the integration test fast while exercising the same
        // response-file boundary and replacement ordering as the production file-based app.
        File.WriteAllText(
            path,
            """
            // Licensed to the .NET Foundation under one or more agreements.
            // The .NET Foundation licenses this file to you under the MIT license.

            var responseArgument = args.Single(argument => argument.StartsWith('@'));
            var lines = File.ReadAllLines(responseArgument[1..].Trim('"'));
            var replacementsIndex = Array.IndexOf(lines, "--replacements");
            var files = lines[1..replacementsIndex];
            var replacements = lines[(replacementsIndex + 1)..];

            foreach (var file in files)
            {
                var content = File.ReadAllText(file);
                for (var index = 0; index < replacements.Length; index += 2)
                {
                    content = content.Replace(replacements[index], replacements[index + 1], StringComparison.Ordinal);
                }
                File.WriteAllText(file, content);
            }

            Console.WriteLine($"Processed: {files.Length} file(s)");
            """);
    }

    private static void AssertReplacementRan(CommandResult result)
    {
        result.EnsureSuccessful();
        Assert.Contains(ReplacementOutputMarker, result.Output);
    }

    private static void AssertReplacementSkipped(CommandResult result)
    {
        result.EnsureSuccessful();
        Assert.DoesNotContain(ReplacementOutputMarker, result.Output);
    }
}
