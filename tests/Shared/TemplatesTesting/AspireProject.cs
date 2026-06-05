// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Polly;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Templates.Tests;

public partial class AspireProject : IAsyncDisposable
{
    public const int DashboardAvailabilityTimeoutSecs = 60;
    private const int AppStartupWaitTimeoutSecs = 5 * 60;
    private static readonly Regex s_dashboardUrlRegex = new(@"Login URL:\s+(?<url>.*)", RegexOptions.Compiled);

    public static string GetNuGetConfigPathFor(TestTargetFramework targetFramework) =>
        Path.Combine(BuildEnvironment.TestAssetsPath, "nuget8.config");

    public static Lazy<HttpClient> Client => new(CreateHttpClient);
    public Process? AppHostProcess { get; private set; }
    public string Id { get; init; }
    public string RootDir { get; init; }
    public string LogPath { get; init; }
    public TestTargetFramework TargetFramework { get; init; }
    public string AppHostProjectDirectory { get; set; }
    public string ServiceDefaultsProjectPath => Path.Combine(RootDir, $"{Id}.ServiceDefaults");
    public string TestsProjectDirectory => Path.Combine(RootDir, $"{Id}.Tests");
    public string? DashboardUrl { get; private set; }
    public Dictionary<string, ProjectInfo> InfoTable { get; private set; } = new(capacity: 0);
    public TaskCompletionSource? AppExited { get; private set; }
    public bool IsRunning => AppHostProcess is not null && !AppHostProcess.TryGetHasExited();

    private readonly ITestOutputHelper _testOutput;
    private readonly BuildEnvironment _buildEnv;

    public AspireProject(string id, string baseDir, ITestOutputHelper testOutput, BuildEnvironment buildEnv, TestTargetFramework? tfm = default)
    {
        Id = id;
        RootDir = baseDir;
        _testOutput = testOutput;
        _buildEnv = buildEnv;
        LogPath = Path.Combine(_buildEnv.LogRootPath, Id);
        TargetFramework = tfm ?? BuildEnvironment.DefaultTargetFramework;
        AppHostProjectDirectory = Path.Combine(RootDir, $"{Id}.AppHost");
    }

    protected void InitPaths()
    {
        Directory.CreateDirectory(LogPath);
    }

    protected static void InitProjectDir(string dir, TestTargetFramework tfm)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />");
    }

    private static void GenerateNuGetConfig(string dir, TestTargetFramework tfm)
    {
        string srcNuGetConfigPath = GetNuGetConfigPathFor(tfm);
        string targetNuGetConfigPath = Path.Combine(dir, "nuget.config");
        File.Copy(srcNuGetConfigPath, targetNuGetConfigPath);
    }

    public static async Task<AspireProject> CreateNewTemplateProjectAsync(
        string id,
        string template,
        ITestOutputHelper testOutput,
        BuildEnvironment buildEnvironment,
        string extraArgs = "",
        TestTargetFramework? targetFramework = default,
        bool addEndpointsHook = true,
        string? overrideRootDir = null)
    {
        string rootDir;
        string projectDir;
        if (overrideRootDir is not null)
        {
            // This is case when we have multiple projects in a top level directory
            // thus the *project* directory differs from the *root* directory
            rootDir = overrideRootDir;
            projectDir = Path.Combine(rootDir, id);
        }
        else
        {
            rootDir = projectDir = Path.Combine(BuildEnvironment.TestRootPath, id);
        }

        string logPath = Path.Combine(BuildEnvironment.ForDefaultFramework.LogRootPath, id);
        Directory.CreateDirectory(logPath);

        var tfmToUse = targetFramework ?? BuildEnvironment.DefaultTargetFramework;
        InitProjectDir(projectDir, tfmToUse);
        GenerateNuGetConfig(rootDir, tfmToUse);

        File.WriteAllText(Path.Combine(rootDir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(rootDir, "Directory.Build.targets"), "<Project />");

        using var cmd = new DotNetNewCommand(
            testOutput,
            useDefaultArgs: true,
            buildEnv: buildEnvironment);

        cmd.WithWorkingDirectory(Path.GetDirectoryName(projectDir)!)
           .WithTimeout(TimeSpan.FromMinutes(5));

        var cmdString = $"{template} {extraArgs} -o \"{id}\"";
        if (tfmToUse != TestTargetFramework.None)
        {
            cmdString = $"{cmdString} -f {tfmToUse.ToTFMString()}";
        }
        var res = await cmd.ExecuteAsync(cmdString).ConfigureAwait(false);
        res.EnsureSuccessful();
        if (res.Output.Contains("Restore failed", StringComparison.OrdinalIgnoreCase) ||
            res.Output.Contains("Post action failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolCommandException($"`dotnet new {cmdString}` . Output: {res.Output}", res);
        }

        foreach (var csprojPath in Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.AllDirectories))
        {
            var csprojContent = File.ReadAllText(csprojPath);
            var matches = TargetFrameworkPropertyRegex().Matches(csprojContent);
            if (matches.Count == 0)
            {
                throw new XunitException($"Expected to find a <TargetFramework> element in {csprojPath}: {csprojContent}");
            }
            if (matches.Count > 1)
            {
                throw new XunitException($"Expected to find exactly one <TargetFramework> element in {csprojPath}: {csprojContent}");
            }

            if (matches[0].Groups["tfm"].Value != tfmToUse.ToTFMString())
            {
                throw new XunitException($"Expected to find {tfmToUse.ToTFMString()} but found '{matches[0].Groups["tfm"].Value}' in {csprojPath}: {csprojContent}");
            }
        }

        var project = new AspireProject(id, projectDir, testOutput, buildEnvironment, tfm: tfmToUse);
        if (addEndpointsHook)
        {
            File.Copy(Path.Combine(BuildEnvironment.TestAssetsPath, "EndPointWriterHook_cs"), Path.Combine(project.AppHostProjectDirectory, "EndPointWriterHook.cs"));
            string programCsPath = Path.Combine(project.AppHostProjectDirectory, "AppHost.cs");
            string programCs = File.ReadAllText(programCsPath);
            programCs = "using Aspire.Hosting.Lifecycle; " + programCs;
            programCs = programCs.Replace("builder.Build().Run();", EndpointWritersCodeSnippet);
            File.WriteAllText(programCsPath, programCs);
        }
        return project;
    }

    public async Task StartAppHostAsync(string[]? extraArgs = default, Action<ProcessStartInfo>? configureProcess = null, bool noBuild = true, bool waitForDashboardUrl = true, CancellationToken token = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Project is already running");
        }

        object outputLock = new();
        var output = new StringBuilder();
        var projectsParsed = new TaskCompletionSource();
        var appRunning = new TaskCompletionSource();
        var dashboardUrlParsed = new TaskCompletionSource();
        var stdoutComplete = new TaskCompletionSource();
        var stderrComplete = new TaskCompletionSource();
        AppExited = new();
        AppHostProcess = new Process();

        var processArguments = $"run {(noBuild ? "--no-build" : "")}";
        processArguments += extraArgs is not null ? " " + string.Join(" ", extraArgs) : "";
        AppHostProcess.StartInfo = new ProcessStartInfo(_buildEnv.DotNet, processArguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppHostProjectDirectory
        };

        foreach (var item in _buildEnv.EnvVars)
        {
            AppHostProcess.StartInfo.Environment[item.Key] = item.Value;
            _testOutput.WriteLine($"\t[{item.Key}] = {item.Value}");
        }

        _testOutput.WriteLine($"Starting the process: {_buildEnv.DotNet} {processArguments} in {AppHostProcess.StartInfo.WorkingDirectory}");
        AppHostProcess.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is null)
            {
                stdoutComplete.SetResult();
                return;
            }

            string line = e.Data;
            string logLine = $"[apphost] {line}";
            lock(outputLock)
            {
                output.AppendLine(logLine);
            }
            _testOutput.WriteLine(logLine);

            var m = s_dashboardUrlRegex.Match(line);
            if (m.Success)
            {
                DashboardUrl = m.Groups["url"].Value;
                dashboardUrlParsed.SetResult();
            }

            if (line?.StartsWith("$ENDPOINTS: ") == true)
            {
                InfoTable = ProjectInfo.Parse(line.Substring("$ENDPOINTS: ".Length));
                projectsParsed.SetResult();
            }

            if (line?.Contains("Distributed application started") == true)
            {
                appRunning.SetResult();
            }
        };
        AppHostProcess.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is null)
            {
                stderrComplete.SetResult();
                return;
            }

            string line = $"[apphost] {e.Data}";
            lock(outputLock)
            {
                output.AppendLine(line);
            }
            _testOutput.WriteLine(line);
        };

        Stopwatch runTimeStopwatch = new();
        EventHandler appExitedCallback = (sender, e) =>
        {
            _testOutput.WriteLine("");
            _testOutput.WriteLine($"----------- [{Path.GetFileName(AppHostProjectDirectory)}] app has exited -------------");
            _testOutput.WriteLine("");
            runTimeStopwatch.Stop();
            AppExited.SetResult();
        };
        AppHostProcess.EnableRaisingEvents = true;
        AppHostProcess.Exited += appExitedCallback;

        AppHostProcess.EnableRaisingEvents = true;

        configureProcess?.Invoke(AppHostProcess.StartInfo);

        runTimeStopwatch.Start();
        AppHostProcess.Start();
        AppHostProcess.BeginOutputReadLine();
        AppHostProcess.BeginErrorReadLine();

        var tasksToWaitFor = new List<Task> { appRunning.Task, projectsParsed.Task };
        if (waitForDashboardUrl)
        {
            tasksToWaitFor.Add(dashboardUrlParsed.Task);
        }

        var successfulStartupTask = Task.WhenAll(tasksToWaitFor);
        var startupTimeoutTask = Task.Delay(TimeSpan.FromSeconds(AppStartupWaitTimeoutSecs), token);

        string outputMessage;
        var resultTask = await Task.WhenAny(successfulStartupTask, AppExited.Task, startupTimeoutTask).ConfigureAwait(false);
        if (resultTask != successfulStartupTask)
        {
            string reason;
            // timed out, or the app has exited
            if (startupTimeoutTask.IsCompleted)
            {
                runTimeStopwatch.Stop();
                reason = $"Timed out after {AppStartupWaitTimeoutSecs} secs waiting for the app to start.";
                _testOutput.WriteLine($"{reason}. Killing ..");
                AppHostProcess.CloseAndKillProcessIfRunning();
                AppHostProcess = null;
            }
            else
            {
                reason = $"App exited before startup could complete with exit code {AppHostProcess.ExitCode}. It ran for {runTimeStopwatch.Elapsed} secs.";
                _testOutput.WriteLine(reason);

                // wait for all the output to be read
                var allOutputCompleteTask = Task.WhenAll(stdoutComplete.Task, stderrComplete.Task);
                var allOutputCompleteTimeoutTask = Task.Delay(TimeSpan.FromSeconds(5), token);
                var completedTask = await Task.WhenAny(allOutputCompleteTask, allOutputCompleteTimeoutTask).ConfigureAwait(false);
                if (completedTask == allOutputCompleteTimeoutTask)
                {
                    _testOutput.WriteLine($"\tand timed out waiting for the full output");
                }
            }

            lock (outputLock)
            {
                outputMessage = output.ToString();
            }
            var exceptionMessage = $"{reason}: {Environment.NewLine}{outputMessage}";
            if (outputMessage.Contains("docker was found but appears to be unhealthy", StringComparison.OrdinalIgnoreCase))
            {
                exceptionMessage = "Docker was found but appears to be unhealthy. " + exceptionMessage;
            }

            // should really fail and quit after this
            throw new InvalidOperationException(exceptionMessage);
        }

        foreach (var project in InfoTable.Values)
        {
            project.Client = Client.Value;
        }

        _testOutput.WriteLine($"-- Ready to run tests --");
    }

    public async Task<CommandResult> BuildAsync(string[]? extraBuildArgs = default, CancellationToken token = default, string? workingDirectory = null)
    {
        workingDirectory ??= Path.Combine(RootDir, $"{Id}.AppHost");

        using var restoreCmd = new DotNetCommand(_testOutput, buildEnv: _buildEnv, label: "restore")
                                    .WithWorkingDirectory(workingDirectory);
        var res = await restoreCmd.ExecuteAsync($"restore \"-bl:{Path.Combine(LogPath!, $"{Id}-restore.binlog")}\" /p:TreatWarningsAsErrors=true");
        res.EnsureSuccessful();

        var buildArgs = $"build \"-bl:{Path.Combine(LogPath!, $"{Id}-build.binlog")}\" /p:TreatWarningsAsErrors=true";
        if (extraBuildArgs is not null)
        {
            buildArgs += " " + string.Join(" ", extraBuildArgs);
        }
        using var buildCmd = new DotNetCommand(_testOutput, buildEnv: _buildEnv, label: "build")
                                        .WithWorkingDirectory(workingDirectory);
        res = await buildCmd.ExecuteAsync(buildArgs);
        res.EnsureSuccessful();
        return res;
    }

    public async Task<WrapperForIPage> OpenDashboardPageAsync(IBrowserContext context, int timeoutSecs = DashboardAvailabilityTimeoutSecs)
    {
        string dashboardUrlToUse;
        if (Environment.GetEnvironmentVariable("DASHBOARD_URL_FOR_TEST") is string dashboardUrlForTest)
        {
            dashboardUrlToUse = dashboardUrlForTest;
        }
        else
        {
            dashboardUrlToUse = DashboardUrl!;
        }
        if (string.IsNullOrEmpty(dashboardUrlToUse))
        {
            throw new InvalidOperationException("Dashboard URL is not available");
        }

        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(DashboardAvailabilityTimeoutSecs));
        await WaitForDashboardToBeAvailableAsync(dashboardUrlToUse, _testOutput, cts.Token).ConfigureAwait(false);

        var dashboardPageWrapper = await context.NewPageWithLoggingAsync(_testOutput);
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 3,
                ShouldHandle = new PredicateBuilder().Handle<PlaywrightException>(ex =>
                {
                    return ex.Message.Contains("net::ERR_NETWORK_CHANGED", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("net::ERR_SOCKET_NOT_CONNECTED", StringComparison.OrdinalIgnoreCase);
                }),
                OnRetry = (args) =>
                {
                    _testOutput.WriteLine($"Reloading dashboard page due to {args.Outcome.Exception?.Message}");
                    return ValueTask.CompletedTask;
                },
                Delay = TimeSpan.FromSeconds(1)
            })
            .Build();

        await pipeline.ExecuteAsync(async token =>
        {
            _testOutput.WriteLine($"Opening dashboard page at {dashboardUrlToUse}");
            await dashboardPageWrapper.GotoAsync(dashboardUrlToUse);
        }, cts.Token).ConfigureAwait(false);

        return dashboardPageWrapper;
    }

    public Task WaitForDashboardToBeAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(DashboardUrl))
        {
            throw new InvalidOperationException("Dashboard URL is not available");
        }

        return WaitForDashboardToBeAvailableAsync(DashboardUrl, _testOutput, cancellationToken);
    }

    public static async Task WaitForDashboardToBeAvailableAsync(string dashboardUrl, ITestOutputHelper testOutput, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dashboardUrl, nameof(dashboardUrl));

        testOutput.WriteLine($"Waiting for the dashboard to be available at {dashboardUrl}...");
        var res = await Client.Value.GetAsync(dashboardUrl, token);
        res.EnsureSuccessStatusCode();
    }

    public async Task StopAppHostAsync(CancellationToken token = default)
    {
        if (AppHostProcess is null)
        {
            throw new InvalidOperationException("Tried to stop the app host process but it is not running.");
        }

        if (AppExited?.Task.IsCompleted == false)
        {
            AppHostProcess.StandardInput.WriteLine("Stop");
        }
        await AppHostProcess.WaitForExitAsync(token).ConfigureAwait(false);
        AppHostProcess.WaitForExit(500);
        AppHostProcess.Dispose();
        AppHostProcess = null;
    }

    public async ValueTask DisposeAsync()
    {
        // TODO: check that everything shutdown
        if (AppHostProcess is null)
        {
            return;
        }

        await DumpDockerInfoAsync(new TestOutputWrapper(null)).ConfigureAwait(false);
        await StopAppHostAsync().ConfigureAwait(false);
    }

    public async Task DumpDockerInfoAsync(ITestOutputHelper? testOutputArg = null, CancellationToken cancellationToken = default)
    {
        if (!RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            return;
        }

        var testOutput = testOutputArg ?? _testOutput!;
        testOutput.WriteLine("--------------------------- Docker info ---------------------------");

        using var cmd = new ToolCommand("docker", testOutput!, "container-list");
        (await cmd.ExecuteAsync($"container list --all").ConfigureAwait(false))
            .EnsureSuccessful();

        testOutput.WriteLine("--------------------------- Docker info (end) ---------------------------");
    }

    public async Task DumpComponentLogsAsync(string component, ITestOutputHelper? testOutputArg = null)
    {
        if (!RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            return;
        }

        var testOutput = testOutputArg ?? _testOutput!;

        string containerName;
        {
            using var cmd = new ToolCommand("docker", testOutput, label: "container-list")
                                .WithTimeout(TimeSpan.FromSeconds(30));
            var res = (await cmd.ExecuteAsync($"container list --all --filter name={component} --format {{{{.Names}}}}"))
                .EnsureSuccessful();
            containerName = res.Output;
        }

        if (string.IsNullOrEmpty(containerName))
        {
            testOutput.WriteLine($"No container found for {component}");
        }
        else
        {
            using var cmd = new ToolCommand("docker", testOutput, label: component)
                                .WithTimeout(TimeSpan.FromSeconds(30));
            (await cmd.ExecuteAsync($"container logs {containerName} -n 50"))
                .EnsureSuccessful();
        }
    }

    public void EnsureAppHostRunning()
    {
        if (AppHostProcess is null || AppHostProcess.TryGetHasExited() || AppExited?.Task.IsCompleted == true)
        {
            throw new InvalidOperationException("The app host process is not running.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient()
            .ConfigureHttpClientDefaults(b =>
            {
                b.ConfigureHttpClient(client =>
                {
                    // Disable the HttpClient timeout to allow the timeout strategies to control the timeout.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                });

                b.UseSocketsHttpHandler((handler, sp) =>
                {
                    handler.PooledConnectionLifetime = TimeSpan.FromSeconds(5);
                    handler.ConnectTimeout = TimeSpan.FromSeconds(5);
                });

                // Ensure transient errors are retried for up to 5 minutes
                b.AddStandardResilienceHandler(options =>
                {
                    options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5); // needs to be at least double the AttemptTimeout to pass options validation
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
                    options.Retry.OnRetry = async (args) =>
                    {
                        var msg = $"Retry #{args.AttemptNumber+1} for '{args.Outcome.Result?.RequestMessage?.RequestUri}'" +
                                        $" due to StatusCode: {(int?)args.Outcome.Result?.StatusCode} ReasonPhrase: '{args.Outcome.Result?.ReasonPhrase}'";

                        msg += (args.Outcome.Exception is not null) ? $" Exception: {args.Outcome.Exception} " : "";
                        if (args.Outcome.Result?.Content is HttpContent content && (await content.ReadAsStringAsync()) is string contentStr)
                        {
                            msg += $" Content:{Environment.NewLine}{contentStr}";
                        }
                        Console.WriteLine(msg);
                    };
                    options.Retry.MaxRetryAttempts = 20;
                });
            });

        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient();
    }

    public const string EndpointWritersCodeSnippet = """
        builder.Services.TryAddEventingSubscriber<EndPointWriterHook>();

        var app = builder.Build();

        // Run a task to read from the console and stop the app if an external process sends "Stop".
        // This allows for easier control than sending CTRL+C to the console in a cross-platform way.
        _ = Task.Run(async () =>
        {
            var s = Console.ReadLine();
            if (s == "Stop")
            {
                await app.StopAsync();
            }
        });
        app.Run();
        """;

    [GeneratedRegex(@"<TargetFramework>(?<tfm>[^<]*)</TargetFramework>")]
    private static partial Regex TargetFrameworkPropertyRegex();
}
