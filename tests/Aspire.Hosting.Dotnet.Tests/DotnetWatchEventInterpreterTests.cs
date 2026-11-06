// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.Watch;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Tests;

// SPIKE: the notices printed by `aspire run --watch`, derived from the watcher's status events and console output.
public class DotnetWatchEventInterpreterTests
{
    private static readonly string s_root = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static readonly string s_api = Path.Combine(s_root, "Api", "Api.csproj");
    private static readonly string s_worker = Path.Combine(s_root, "Worker", "Worker.csproj");
    private static readonly string s_apiProgram = Path.Combine(s_root, "Api", "Program.cs");
    private static readonly string s_shared = Path.Combine(s_root, "Shared", "Greeter.cs");

    private static DotnetWatchEventInterpreter CreateReady()
    {
        var interpreter = new DotnetWatchEventInterpreter([(s_api, "api"), (s_worker, "worker")]);
        interpreter.OnStatus("building", [s_api, s_worker], null, null);
        var notice = Assert.Single(interpreter.OnStatus("build_complete", [s_api, s_worker], true, null));
        Assert.Equal(WatchNoticeKind.Watching, notice.Kind);
        Assert.Contains("api, worker", notice.Message);
        return interpreter;
    }

    [Fact]
    public void HotReloadIsAttributedToTheProjectThatOwnsTheFile()
    {
        var interpreter = CreateReady();

        Assert.Empty(interpreter.OnLogLine($"⌚ File updated: {s_apiProgram}"));
        var notice = Assert.Single(interpreter.OnLogLine("🔥 C# and Razor changes applied in 42ms."));

        Assert.Equal(WatchNoticeKind.HotReloadApplied, notice.Kind);
        Assert.Equal("🔥 Hot reload applied to api in 42 ms (Program.cs).", notice.Message);
        Assert.Equal([s_api], notice.ProjectPaths);
    }

    [Fact]
    public void UnattributableSharedFileEditDoesNotNameProjects()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_shared}");
        var notice = Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 7ms."));

        // Without a reference graph the owner is unknown: every service gets the notice, but it doesn't claim they all changed.
        Assert.Equal("🔥 Hot reload applied in 7 ms (Greeter.cs).", notice.Message);
        Assert.Equal([s_api, s_worker], notice.ProjectPaths);
    }

    [Fact]
    public void SharedLibraryEditIsAttributedToTheServicesThatReferenceIt()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-refs").FullName;
        try
        {
            var api = Path.Combine(root, "Api", "Api.csproj");
            var worker = Path.Combine(root, "Worker", "Worker.csproj");
            var shared = Path.Combine(root, "Shared", "Shared.csproj");
            var core = Path.Combine(root, "Core", "Core.csproj");
            WriteProject(api, @"..\Shared\Shared.csproj");
            WriteProject(worker);
            WriteProject(shared, "../Core/Core.csproj");
            WriteProject(core);

            var projects = new[] { (api, "api"), (worker, "worker") };
            var references = projects.ToDictionary(p => p.Item1, p => DotnetWatchEventInterpreter.ReadReferencedDirectories(p.Item1));
            var interpreter = new DotnetWatchEventInterpreter(projects, root, references);
            interpreter.OnStatus("build_complete", [api, worker], true, null);

            // Api -> Shared -> Core; the independent worker isn't affected by either.
            interpreter.OnLogLine($"File updated: {Path.Combine(root, "Shared", "Greeter.cs")}");
            var notice = Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 7ms."));
            Assert.Equal("🔥 Hot reload applied to api in 7 ms (Greeter.cs).", notice.Message);
            Assert.Equal([api], notice.ProjectPaths);

            interpreter.OnLogLine($"File updated: {Path.Combine(root, "Core", "Clock.cs")}");
            Assert.Equal([api], Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 5ms.")).ProjectPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void WriteProject(string path, params string[] references)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />"));
            File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>");
        }
    }

    [Fact]
    public void CompileErrorShowsFirstErrorAndFixIsAnnounced()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Empty(interpreter.OnLogLine("🔥 Unable to apply changes due to compilation errors."));
        Assert.True(interpreter.HasPending);

        var error = Assert.Single(interpreter.OnLogLine($"❌ {s_apiProgram}(8,43): error CS0117: 'Greeter' does not contain a definition for 'Mesage' [{s_api}]"));
        Assert.Equal(WatchNoticeKind.CompileError, error.Kind);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal([s_api], error.ProjectPaths);
        Assert.Contains("Build error in api: Program.cs(8,43): CS0117: 'Greeter' does not contain a definition for 'Mesage'.", error.Message);
        Assert.Contains("keeps running the last good code", error.Message);
        Assert.False(interpreter.HasPending);

        // Later errors of the same attempt don't produce more notices.
        Assert.Empty(interpreter.OnLogLine($"{s_apiProgram}(9,1): error CS1002: ; expected"));

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        var fixedNotice = Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 30ms."));
        Assert.StartsWith("✅ Fixed. 🔥 Hot reload applied to api", fixedNotice.Message);
    }

    [Fact]
    public void RevertingBrokenEditReportsFixedInsteadOfNoChanges()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        interpreter.OnLogLine("Unable to apply changes due to compilation errors.");
        Assert.Single(interpreter.OnLogLine($"{s_apiProgram}(19,32): error CS1525: Invalid expression term ')' [{s_api}]"));

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        var notice = Assert.Single(interpreter.OnLogLine("No managed code changes to apply."));
        Assert.Equal(LogLevel.Information, notice.Level);
        Assert.StartsWith("✅ Fixed. api already runs this code", notice.Message);

        // Back to normal afterwards.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.StartsWith("📝", Assert.Single(interpreter.OnLogLine("No managed code changes to apply.")).Message);
    }

    [Fact]
    public void FailureWithoutErrorLineIsFlushedWithPointerToLogs()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        interpreter.OnLogLine("Unable to apply changes due to compilation errors.");

        var notice = Assert.Single(interpreter.FlushPending());
        Assert.Contains("see the 'dotnet-watch' resource logs", notice.Message);
        Assert.Empty(interpreter.FlushPending());
    }

    [Fact]
    public void InitialBuildFailureThenRecovery()
    {
        var interpreter = new DotnetWatchEventInterpreter([(s_api, "api"), (s_worker, "worker")]);
        Assert.Empty(interpreter.OnStatus("building", [s_api, s_worker], null, null));

        // The status event can arrive before the compiler output; the notice waits for the first error.
        Assert.Empty(interpreter.OnStatus("build_complete", [s_api, s_worker], false, null));
        var failed = Assert.Single(interpreter.OnLogLine($"{s_apiProgram}(8,43): error CS0117: 'Greeter' does not contain a definition for 'Mesage' [{s_api}]"));
        Assert.Equal(WatchNoticeKind.BuildFailed, failed.Kind);
        Assert.StartsWith("❌ Initial build failed: Program.cs(8,43): CS0117:", failed.Message);
        Assert.Contains("api, worker start once the build succeeds", failed.Message);
        Assert.Equal("Program.cs(8,43): CS0117: 'Greeter' does not contain a definition for 'Mesage'", interpreter.FirstError);

        interpreter.OnStatus("building", [s_api, s_worker], null, null);
        var ready = Assert.Single(interpreter.OnStatus("build_complete", [s_api, s_worker], true, null));
        Assert.Equal(WatchNoticeKind.Watching, ready.Kind);
        Assert.StartsWith("✅ Build succeeded. 👀 Watching api, worker", ready.Message);
    }

    [Fact]
    public void RudeEditReportsRebuildAndRestartButNotHotReload()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Empty(interpreter.OnLogLine("🔥 Restart is needed to apply the changes."));
        Assert.Equal(WatchNoticeKind.Rebuilding, Assert.Single(interpreter.OnStatus("building", [s_api], null, null)).Kind);
        Assert.Empty(interpreter.OnStatus("build_complete", [s_api], true, null));
        var restarting = Assert.Single(interpreter.OnStatus("restarting", [s_api], null, null));
        Assert.Equal("🔄 Restarting api to apply changes that can't be hot reloaded.", restarting.Message);

        // The watcher's console output arrives after the status events (it flows through DCP) and reports the
        // restart cycle as "changes applied"; that isn't a hot reload.
        Assert.Empty(interpreter.OnLogLine("C# and Razor changes applied in 5201ms."));

        // The next real edit is reported again.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Equal(WatchNoticeKind.HotReloadApplied, Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 20ms.")).Kind);
    }

    [Fact]
    public void CrashAndRelaunch()
    {
        var interpreter = CreateReady();

        var crashed = Assert.Single(interpreter.OnStatus("process_exited", [s_api], null, 3));
        Assert.Equal(WatchNoticeKind.Crashed, crashed.Kind);
        Assert.Equal(LogLevel.Error, crashed.Level);
        Assert.Equal("💥 api exited with code 3. Save a change or use Restart to relaunch it.", crashed.Message);

        // The crashed project is relaunched rather than patched.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Empty(interpreter.OnLogLine("C# and Razor changes applied in 32ms."));

        var relaunched = Assert.Single(interpreter.OnStatus("process_started", [s_api], null, null));
        Assert.Equal("▶️ api is running again.", relaunched.Message);
        Assert.Empty(interpreter.OnStatus("process_started", [s_api], null, null));
    }

    [Fact]
    public void RelaunchReportedBeforeItsConsoleLineIsNotReportedAsHotReload()
    {
        var interpreter = CreateReady();
        interpreter.OnStatus("process_exited", [s_api], null, 3);

        // The status pipe delivers process_started before DCP delivers the console lines of the same cycle.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Equal(WatchNoticeKind.Relaunched, Assert.Single(interpreter.OnStatus("process_started", [s_api], null, null)).Kind);
        Assert.True(interpreter.HasPending);
        Assert.Empty(interpreter.OnLogLine("C# and Razor changes applied in 32ms."));

        // The next real edit is reported again.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Equal(WatchNoticeKind.HotReloadApplied, Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 20ms.")).Kind);
    }

    [Fact]
    public void RelaunchWithoutAConsoleLineDoesNotSuppressLaterHotReloads()
    {
        var interpreter = CreateReady();
        interpreter.OnStatus("process_exited", [s_api], null, 3);
        interpreter.OnStatus("process_started", [s_api], null, null);

        // No "changes applied" line followed the relaunch; the coordinator flushes after a short delay.
        Assert.True(interpreter.HasPending);
        Assert.Empty(interpreter.FlushPending());
        Assert.False(interpreter.HasPending);

        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Equal(WatchNoticeKind.HotReloadApplied, Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 20ms.")).Kind);
    }

    [Fact]
    public void ProjectFileChangePointsToRebuild()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {s_api}");
        var notice = Assert.Single(interpreter.OnLogLine("⌚ No managed code changes to apply."));

        Assert.Equal(WatchNoticeKind.ProjectFileChanged, notice.Kind);
        Assert.Equal(LogLevel.Warning, notice.Level);
        Assert.Contains("Api.csproj changed", notice.Message);
        Assert.Contains("use Rebuild on api", notice.Message);
        Assert.Contains("'aspire resource api rebuild'", notice.Message);
    }

    [Fact]
    public void NonCodeFileChangeSaysNoRebuildNeeded()
    {
        var interpreter = CreateReady();

        interpreter.OnLogLine($"File updated: {Path.Combine(s_root, "Api", "appsettings.json")}");
        var notice = Assert.Single(interpreter.OnLogLine("No managed code changes to apply."));
        Assert.Equal("📝 appsettings.json changed in api; no rebuild needed.", notice.Message);

        // The file list doesn't leak into the next notice.
        interpreter.OnLogLine($"File updated: {s_apiProgram}");
        Assert.Equal("🔥 Hot reload applied to api in 5 ms (Program.cs).", Assert.Single(interpreter.OnLogLine("C# and Razor changes applied in 5ms.")).Message);
    }

    [Fact]
    public void UnrelatedOutputProducesNoNotices()
    {
        var interpreter = CreateReady();

        Assert.Empty(interpreter.OnLogLine("  Determining projects to restore..."));
        Assert.Empty(interpreter.OnLogLine("Build succeeded."));
        Assert.Empty(interpreter.OnLogLine("    0 Error(s)"));
        Assert.Empty(interpreter.OnStatus("hot_reload_applied", [s_api, s_worker], null, null));
    }
}
