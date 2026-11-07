// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchProjectFileWatcherTests
{
    [Theory]
    [InlineData("Api.csproj", false, true)]
    [InlineData("Lib.fsproj", false, true)]
    [InlineData("Common.props", false, true)]
    [InlineData("Custom.targets", false, true)]
    [InlineData("Program.cs", false, false)]
    [InlineData("appsettings.json", false, false)]
    [InlineData("Directory.Packages.props", true, true)]
    [InlineData("Directory.Build.props", true, true)]
    [InlineData("directory.build.targets", true, true)]
    [InlineData("global.json", true, true)]
    [InlineData("NuGet.config", true, true)]
    [InlineData("Api.csproj", true, false)]
    [InlineData("Common.props", true, false)]
    public void BuildFilesAreRecognized(string name, bool ancestorOnly, bool expected)
    {
        Assert.Equal(expected, DotnetWatchProjectFileWatcher.IsBuildFile(Path.Combine("dir", name), ancestorOnly));
    }

    [Fact]
    public void WatchesEvaluatedProjectsAndImportsAndTheirAncestors()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-refs").FullName;
        try
        {
            var api = Path.Combine(root, "src", "Api", "Api.csproj");
            var lib = Path.Combine(root, "src", "Lib", "Lib.csproj");
            var core = Path.Combine(root, "src", "Core", "Core.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
            Directory.CreateDirectory(Path.GetDirectoryName(core)!);
            var imported = Path.Combine(root, "external", "Build.custom");
            Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
            var directories = DotnetWatchProjectFileWatcher.GetWatchedDirectories([api, lib, core, imported]);
            Assert.False(directories[Path.GetDirectoryName(api)!]);
            Assert.False(directories[Path.GetDirectoryName(lib)!]);
            Assert.False(directories[Path.GetDirectoryName(core)!]);
            Assert.False(directories[Path.GetDirectoryName(imported)!]);
            Assert.True(directories[Path.Combine(root, "src")]);
            Assert.True(directories[root]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileBasedAppWatchesItsDirectory()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-fba").FullName;
        try
        {
            var app = Path.Combine(root, "ticker.cs");
            File.WriteAllText(app, "Console.WriteLine();");

            var directories = DotnetWatchProjectFileWatcher.GetWatchedDirectories([app]);
            Assert.False(directories[root]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsSettledBuildFileChangesOnly()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-fsw").FullName;
        try
        {
            var project = Path.Combine(root, "Api", "Api.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project />");

            var props = Path.Combine(root, "Directory.Packages.props");
            var changes = new List<IReadOnlyList<string>>();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new DotnetWatchProjectFileWatcher([project], c =>
            {
                lock (changes)
                {
                    changes.Add(c);
                    var all = changes.SelectMany(b => b).ToHashSet();
                    if (all.Contains(project) && all.Contains(props))
                    {
                        observed.TrySetResult();
                    }
                }
            }, TimeSpan.FromMilliseconds(300), exception => observed.TrySetException(exception));
            watcher.Start();

            File.WriteAllText(Path.Combine(root, "Api", "Program.cs"), "// not a build file");
            File.WriteAllText(project, "<Project><!-- 1 --></Project>");
            File.WriteAllText(project, "<Project><!-- 2 --></Project>");
            File.WriteAllText(props, "<Project />");

            // Notifications may be split across debounce windows on a slow machine, so validate every batch
            // rather than requiring a single one.
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(1000);

            lock (changes)
            {
                Assert.NotEmpty(changes);
                foreach (var batch in changes)
                {
                    Assert.NotEmpty(batch);
                    Assert.DoesNotContain(batch, p => p.EndsWith("Program.cs", StringComparison.Ordinal));
                    Assert.Equal(batch.Count, batch.Distinct().Count());
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplacementRetainsQueuedEditsFromTheOldDebounceWindow()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-handoff").FullName;
        try
        {
            var project = Path.Combine(root, "Api.csproj");
            var imported = Path.Combine(root, "Build.custom");
            var batches = new List<IReadOnlyList<string>>();
            using var previous = new DotnetWatchProjectFileWatcher([project, imported],
                batches.Add, TimeSpan.FromMinutes(10), exception => throw exception);
            OnChange(previous, project, ancestorOnly: false);
            OnChange(previous, imported, ancestorOnly: false);
            OnChange(previous, project, ancestorOnly: false);

            var pending = previous.DisposeAndGetPendingChanges();

            Assert.Equal([project, imported], pending);
            Assert.Empty(batches);
            Assert.Empty(previous.DisposeAndGetPendingChanges());
            using var replacement = new DotnetWatchProjectFileWatcher([project, imported],
                batches.Add, TimeSpan.FromMinutes(10), exception => throw exception);
            foreach (var path in pending)
            {
                OnChange(replacement, path, ancestorOnly: false);
            }
            Flush(replacement);
            Assert.Equal([project, imported], Assert.Single(batches));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnChange")]
    private static extern void OnChange(DotnetWatchProjectFileWatcher watcher, string path, bool ancestorOnly);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Flush")]
    private static extern void Flush(DotnetWatchProjectFileWatcher watcher);
}
