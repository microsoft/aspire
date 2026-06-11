// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;
using static Aspire.Templates.Tests.TestExtensions;

namespace Aspire.Templates.Tests;

public partial class TemplateTestsBase
{
    [GeneratedRegex(@"^\s*//")]
    private static partial Regex CommentLineRegex();

    // Regex is from src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.in.targets - _GeneratedClassNameFixupRegex
    [GeneratedRegex(@"(((?<=\.)|^)(?=\d)|\W)")]
    private static partial Regex GeneratedClassNameFixupRegex();
    private static Lazy<IBrowser> Browser => new(CreateBrowser);
    private static readonly XmlWriterSettings s_xmlWriterSettings = new() { ConformanceLevel = ConformanceLevel.Fragment };
    protected readonly TestOutputWrapper _testOutput;

    public static readonly string[] TestFrameworkTypes = ["none", "mstest", "nunit", "xunit.net"];

    public TemplateTestsBase(ITestOutputHelper testOutput)
    {
        _testOutput = new TestOutputWrapper(testOutput);
    }

    private static IBrowser CreateBrowser()
    {
        var t = Task.Run(async () => await PlaywrightProvider.CreateBrowserAsync());

        // default timeout for playwright.Chromium.LaunchAsync is 30secs,
        // so using a timeout here as a fallback
        if (!t.Wait(45 * 1000))
        {
            throw new TimeoutException("Browser creation timed out");
        }

        return t.Result;
    }

    public async Task<string> CreateAndAddTestTemplateProjectAsync(
        string id,
        string testTemplateName,
        AspireProject project,
        TestTargetFramework? tfm = null,
        BuildEnvironment? buildEnvironment = null,
        string? extraArgs = null,
        Func<AspireProject, Task>? onBuildAspireProject = null,
        string? overrideRootDir = null)
    {
        buildEnvironment ??= BuildEnvironment.ForDefaultFramework;
        var tmfArg = tfm is not null ? $"-f {tfm.Value.ToTFMString()}" : "";

        string rootDirToUse = overrideRootDir ?? project.RootDir;
        // Add test project
        var testProjectName = $"{id}.{FixupSymbolName(testTemplateName)}Tests";
        using var newTestCmd = new DotNetNewCommand(
                                    _testOutput,
                                    label: $"new-test-{testTemplateName}",
                                    buildEnv: buildEnvironment)
                                .WithWorkingDirectory(rootDirToUse);
        var res = await newTestCmd.ExecuteAsync($"{testTemplateName} {tmfArg} -o \"{testProjectName}\" {extraArgs}");
        res.EnsureSuccessful();

        var testProjectDir = Path.Combine(rootDirToUse, testProjectName);
        Assert.True(Directory.Exists(testProjectDir), $"Expected tests project at {testProjectDir}");

        var testProjectPath = Path.Combine(testProjectDir, testProjectName + ".csproj");
        Assert.True(File.Exists(testProjectPath), $"Expected tests project file at {testProjectPath}");

        var appHostProjectName = Path.GetFileName(project.AppHostProjectDirectory)!;
        PrepareTestCsFile(
            id: project.Id,
            projectDir: testProjectDir,
            appHostProjectName: appHostProjectName,
            testTemplateName: testTemplateName);
        PrepareTestProject(
            project: project,
            projectPath: testProjectPath,
            appHostProjectName: appHostProjectName);

        return testProjectDir;

        static void PrepareTestProject(AspireProject project, string projectPath, string appHostProjectName)
        {
            // Insert <ProjectReference Include="$(MSBuildThisFileDirectory)..\aspire-starter0.AppHost\aspire-starter0.AppHost.csproj" /> in the project file

            // taken from https://raw.githubusercontent.com/dotnet/templating/a325ffa18edd1590f9b340cf83d51d8eb567ebdc/src/Microsoft.TemplateEngine.Orchestrator.RunnableProjects/ValueForms/XmlEncodeValueFormFactory.cs
            StringBuilder output = new();
            using (var w = XmlWriter.Create(output, s_xmlWriterSettings))
            {
                w.WriteString(appHostProjectName);
            }
            var xmlEncodedId = output.ToString();

            var projectReference = $@"<ProjectReference Include=""$(MSBuildThisFileDirectory)..\{xmlEncodedId}\{xmlEncodedId}.csproj"" />";

            var newContents = File.ReadAllText(projectPath)
                                    .Replace("</Project>", $"<ItemGroup>{projectReference}</ItemGroup>\n</Project>");
            File.WriteAllText(projectPath, newContents);
        }

        static void PrepareTestCsFile(string id, string projectDir, string appHostProjectName, string testTemplateName)
        {
            var testCsPath = Path.Combine(projectDir, "IntegrationTest1.cs");
            var sb = new StringBuilder();

            // Uncomment everything after the marker line
            var inTest = false;
            var marker = testTemplateName switch
            {
                "aspire-nunit" or "aspire-nunit-9" => "// [Test]",
                "aspire-mstest" or "aspire-mstest-9" => "// [TestMethod]",
                "aspire-xunit" or "aspire-xunit-9" => "// [Fact]",
                _ => throw new NotImplementedException($"Unknown test template: {testTemplateName}")
            };

            foreach (var line in File.ReadAllLines(testCsPath))
            {
                if (!inTest && line.Contains(marker))
                {
                    inTest = true;
                }

                if (inTest && CommentLineRegex().IsMatch(line))
                {
                    sb.AppendLine(CommentLineRegex().Replace(line, "    "));
                    continue;
                }

                sb.AppendLine(line);
            }

            var classNameFromId = GeneratedClassNameFixupRegex().Replace(appHostProjectName, "_");
            sb.Replace("Projects.MyAspireApp_AppHost", $"Projects.{classNameFromId}");
            File.WriteAllText(testCsPath, sb.ToString());
        }
    }

    public static Task<IBrowserContext> CreateNewBrowserContextAsync()
        => PlaywrightProvider.HasPlaywrightSupport
                ? Browser.Value.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true })
                : throw new InvalidOperationException("Playwright is not available");

    protected Task<ResourceRow[]> CheckDashboardHasResourcesAsync(WrapperForIPage dashboardPageWrapper, IEnumerable<ResourceRow> expectedResources, string logPath, int timeoutSecs = 120)
        => CheckDashboardHasResourcesAsync(dashboardPageWrapper, expectedResources, _testOutput, logPath, timeoutSecs);

    protected static async Task<ResourceRow[]> CheckDashboardHasResourcesAsync(WrapperForIPage dashboardPageWrapper,
                                                                               IEnumerable<ResourceRow> expectedResources,
                                                                               ITestOutputHelper testOutput,
                                                                               string logPath,
                                                                               int timeoutSecs = 120)
    {
        try
        {
            return await CheckDashboardHasResourcesActualAsync(dashboardPageWrapper, expectedResources, testOutput, timeoutSecs);
        }
        catch
        {
            string screenshotPath = Path.Combine(logPath, $"dashboard-fail-{Guid.NewGuid().ToString()[..8]}.png");
            await dashboardPageWrapper.Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
            testOutput.WriteLine($"Dashboard screenshot saved to {screenshotPath}");
            throw;
        }
    }

    private static async Task<ResourceRow[]> CheckDashboardHasResourcesActualAsync(WrapperForIPage dashboardPageWrapper, IEnumerable<ResourceRow> expectedResources, ITestOutputHelper testOutput, int timeoutSecs = 120)
    {
        // FIXME: check the page has 'Resources' label
        // fluent-toolbar/h1 resources

        var numAttempts = 0;
        testOutput.WriteLine($"Waiting for resources to appear on the dashboard");
        await Task.Delay(500);

        var expectedRowsTable = expectedResources.ToDictionary(r => r.Name);
        HashSet<string> foundNames = [];
        List<ResourceRow> foundRows = [];

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

        while (foundNames.Count < expectedRowsTable.Count && !cts.IsCancellationRequested)
        {
            if (dashboardPageWrapper.HasErrors)
            {
                if (numAttempts >= 3)
                {
                    throw new InvalidOperationException($"Failed to load dashboard page after {numAttempts} attempts");
                }

                testOutput.WriteLine($"----- Reloading dashboard page");
                await dashboardPageWrapper.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
                numAttempts++;
            }

            await Task.Delay(500);

            // _testOutput.WriteLine($"Checking for rows again");
            var rowsLocator = dashboardPageWrapper.Page.Locator("//tr[@class='fluent-data-grid-row hover resource-row']");
            var allRows = await rowsLocator.AllAsync();
            // _testOutput.WriteLine($"found rows#: {allRows.Count}");
            if (allRows.Count == 0)
            {
                // Console.WriteLine ($"** no rows found ** elapsed: {sw.Elapsed.TotalSeconds} secs");
                continue;
            }

            foreach (var rowLoc in allRows)
            {
                // get the cells
                var cellLocs = await rowLoc.Locator("//td[@role='gridcell']").AllAsync();

                // is the resource name expected?
                var resourceNameCell = cellLocs[0];
                var resourceName = await resourceNameCell.InnerTextAsync();
                resourceName = resourceName.Trim();
                if (!expectedRowsTable.TryGetValue(resourceName, out var expectedRow))
                {
                    Assert.Fail($"Row with unknown name found: '{resourceName}'. Expected values: {string.Join(", ", expectedRowsTable.Keys.Select(k => $"'{k}'"))}");
                }
                if (foundNames.Contains(resourceName))
                {
                    continue;
                }

                AssertEqual(expectedRow.Name, resourceName, $"Name for '{resourceName}'");

                var stateCell = cellLocs[1];
                var actualState = await stateCell.InnerTextAsync().ConfigureAwait(false);
                actualState = actualState.Trim();
                if (expectedRow.State != actualState && actualState != "Finished" && !actualState.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AssertEqual(expectedRow.State, actualState, $"State for {resourceName}");

                // Check 'Source' column
                var sourceCell = cellLocs[3];
                // Since this will be the entire command, we can just confirm that the path of the executable contains
                // the expected source (executable/project)
                Assert.Contains(expectedRow.SourceContains, await sourceCell.InnerTextAsync());

                foundRows.Add(expectedRow);
                foundNames.Add(resourceName);
            }
        }

        if (foundNames.Count != expectedRowsTable.Count)
        {
            Assert.Fail($"Expected rows not found: {string.Join(", ", expectedRowsTable.Keys.Except(foundNames))}");
        }

        return foundRows.ToArray();
    }

    // Don't fixup the prefix so it can have characters meant for testing, like spaces
    public static string GetNewProjectId(string? prefix = null)
        => (prefix is null ? "" : $"{prefix}_") + FixupSymbolName(Path.GetRandomFileName());

    public static IEnumerable<string> GetProjectNamesForTest()
    {
        if (!PlatformDetection.IsRunningPRValidation && !EnvironmentVariables.RunOnlyBasicBuildTemplatesTests)
        {
            // Avoid running these cases on PR validation

            yield return "aspire_龦唉丂荳_㐁ᠭ_ᠤསྲིདخەلꌠ_1ᥕ项目1"; // sln should have UTF-8 byte order mark
            yield return "aspire_starter.1period then.34letters";
            yield return "aspire-starter & with.1";

            // ActiveIssue: https://github.com/dotnet/aspnetcore/issues/56277
            // yield return "aspire_😀";
        }

        // basic case
        yield return "aspire";
    }

    public static async Task AssertStarterTemplateRunAsync(IBrowserContext? context, AspireProject project, string config, ITestOutputHelper _testOutput)
    {
        await project.StartAppHostAsync(extraArgs: [$"-c {config}"], noBuild: false);

        if (context is not null)
        {
            var page = await project.OpenDashboardPageAsync(context);
            await CheckDashboardHasResourcesAsync(
                page,
                StarterTemplateRunTestsBase<StarterTemplateFixture>.GetExpectedResources(project, hasRedisCache: false),
                _testOutput,
                project.LogPath).ConfigureAwait(false);

            string apiServiceUrl = project.InfoTable["apiservice"].Endpoints[0].Uri;
            await StarterTemplateRunTestsBase<StarterTemplateFixture>.CheckApiServiceWorksAsync(apiServiceUrl, _testOutput, project.LogPath);

            string webFrontEnd = project.InfoTable["webfrontend"].Endpoints[0].Uri;
            await StarterTemplateRunTestsBase<StarterTemplateFixture>.CheckWebFrontendWorksAsync(context, webFrontEnd, _testOutput, project.LogPath);
        }
        else
        {
            _testOutput.WriteLine($"Skipping playwright part of the test");
        }

        await project.StopAppHostAsync();
    }

    public static async Task<CommandResult?> AssertTestProjectRunAsync(string testProjectDirectory, string testType, ITestOutputHelper testOutput, string config = "Debug", int testRunTimeoutSecs = 3 * 60)
    {
        if (testType == "none")
        {
            Assert.False(Directory.Exists(testProjectDirectory), "Expected no tests project to be created");
            return null;
        }
        else
        {
            Assert.True(Directory.Exists(testProjectDirectory), $"Expected tests project at {testProjectDirectory}");

            // Build first, because `dotnet test` does not show test results if all the tests pass
            using var buildCmd = new DotNetCommand(testOutput, label: $"test-{testType}")
                                    .WithWorkingDirectory(testProjectDirectory)
                                    .WithTimeout(TimeSpan.FromSeconds(testRunTimeoutSecs));

            (await buildCmd.ExecuteAsync($"test -c {config}")).EnsureSuccessful();

            // .. then test with --no-build
            using var testCmd = new DotNetCommand(testOutput, label: $"test-{testType}")
                                    .WithWorkingDirectory(testProjectDirectory)
                                    .WithTimeout(TimeSpan.FromSeconds(testRunTimeoutSecs));

            var testRes = (await testCmd.ExecuteAsync($"test -c {config} --no-build"))
                                .EnsureSuccessful();

            Assert.Matches("Passed! * - Failed: *0, Passed: *1, Skipped: *0, Total: *1", testRes.Output);
            return testRes;
        }
    }

    public static TheoryData<string, string, TestSdk, TestTargetFramework, string?> TestDataForNewAndBuildTemplateTests(string templateName, string extraArgs) => new()
        {
            // Previous Sdk, Previous TFM
            { templateName, extraArgs, TestSdk.Previous, TestTargetFramework.Previous, null },
            // Previous Sdk - Current TFM
            { templateName, extraArgs, TestSdk.Previous, TestTargetFramework.Current, "The current .NET SDK does not support targeting .NET 9.0" },

            // Current SDK, Previous TFM
            { templateName, extraArgs, TestSdk.Current, TestTargetFramework.Previous, null },
            // Current SDK, Current TFM
            { templateName, extraArgs, TestSdk.Current, TestTargetFramework.Current, null },
            // Current SDK, Next TFM
            { templateName, extraArgs, TestSdk.Current, TestTargetFramework.Next, "The current .NET SDK does not support targeting .NET 10.0" },

            // Next SDK, Previous TFM
            { templateName, extraArgs, TestSdk.Next, TestTargetFramework.Previous, null },
            // Next SDK, Current TFM
            { templateName, extraArgs, TestSdk.Next, TestTargetFramework.Current, null },
            // Next SDK, Next TFM
            { templateName, extraArgs, TestSdk.Next, TestTargetFramework.Next, null },

            // Current SDK + previous runtime, Previous TFM
            { templateName, extraArgs, TestSdk.NextSdkWithCurrentAndPreviousRuntime, TestTargetFramework.Previous, null },
            // Current SDK + previous runtime, Current TFM
            { templateName, extraArgs, TestSdk.NextSdkWithCurrentAndPreviousRuntime, TestTargetFramework.Current, null },
            // Next SDK + current runtime, Next TFM
            { templateName, extraArgs, TestSdk.NextSdkWithCurrentAndPreviousRuntime, TestTargetFramework.Next, null },
        };

    // Taken from dotnet/runtime src/tasks/Common/Utils.cs
    private static readonly char[] s_charsToReplace = ['.', '-', '+', '<', '>'];
    public static string FixupSymbolName(string name)
    {
        UTF8Encoding utf8 = new();
        byte[] bytes = utf8.GetBytes(name);
        StringBuilder sb = new();

        foreach (byte b in bytes)
        {
            if ((b >= (byte)'0' && b <= (byte)'9') ||
                (b >= (byte)'a' && b <= (byte)'z') ||
                (b >= (byte)'A' && b <= (byte)'Z') ||
                (b == (byte)'_'))
            {
                sb.Append((char)b);
            }
            else if (s_charsToReplace.Contains((char)b))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append($"_{b:X}_");
            }
        }

        return sb.ToString();
    }

}
