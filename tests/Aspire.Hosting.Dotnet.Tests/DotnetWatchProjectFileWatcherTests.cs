// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchProjectFileWatcherTests
{
    [Fact]
    public void ObservesEvaluatedInputsAndExplicitAncestorCandidates()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-inputs-");
        try
        {
            var api = Path.Combine(root.FullName, "src", "Api", "Api.csproj");
            var imported = Path.Combine(root.FullName, "external", "Build.custom");

            var files = DotnetWatchProjectFileWatcher.GetObservedFiles([api, imported]);

            Assert.Contains(Path.GetFullPath(api), files);
            Assert.Contains(Path.GetFullPath(imported), files);
            Assert.Contains(Path.Combine(root.FullName, "Directory.Build.props"), files);
            Assert.Contains(Path.Combine(root.FullName, "Directory.Build.targets"), files);
            Assert.Contains(Path.Combine(root.FullName, "Directory.Packages.props"), files);
            Assert.Contains(Path.Combine(root.FullName, "global.json"), files);
            Assert.DoesNotContain(Path.Combine(root.FullName, "NuGet.config"), files);
            Assert.DoesNotContain(Path.Combine(root.FullName, "src", "Api", "Program.cs"), files);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReportsSettledContentChangesAndIgnoresMetadataOnlyTouches()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-poll-");
        try
        {
            var project = Path.Combine(root.FullName, "Api", "Api.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            await File.WriteAllTextAsync(project, "<Project />");
            var props = Path.Combine(root.FullName, "Directory.Packages.props");
            var changes = new List<IReadOnlyList<string>>();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new DotnetWatchProjectFileWatcher([project], batch =>
            {
                lock (changes)
                {
                    changes.Add(batch);
                    var all = changes.SelectMany(change => change).ToHashSet();
                    if (all.Contains(project) && all.Contains(props))
                    {
                        observed.TrySetResult();
                    }
                }
            }, TimeSpan.FromMilliseconds(150), exception => observed.TrySetException(exception), TimeSpan.FromMilliseconds(25));
            watcher.Start();

            File.SetLastWriteTimeUtc(project, DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(100);
            lock (changes)
            {
                Assert.Empty(changes);
            }

            await File.WriteAllTextAsync(project, "<Project><!-- changed --></Project>");
            await File.WriteAllTextAsync(props, "<Project />");

            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (changes)
            {
                Assert.All(changes, batch => Assert.Equal(batch.Count, batch.Distinct().Count()));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReplacementRetainsQueuedEditsFromTheOldDebounceWindow()
    {
        var root = Directory.CreateTempSubdirectory("aspire-watch-handoff-");
        try
        {
            var project = Path.Combine(root.FullName, "Api.csproj");
            var imported = Path.Combine(root.FullName, "Build.custom");
            File.WriteAllText(project, "<Project />");
            File.WriteAllText(imported, "before");
            var batches = new List<IReadOnlyList<string>>();
            using var previous = new DotnetWatchProjectFileWatcher([project, imported],
                batches.Add, TimeSpan.FromMinutes(10), exception => throw exception);
            previous.Start();
            File.WriteAllText(project, "<Project><!-- changed --></Project>");
            File.WriteAllText(imported, "after");
            PollOnce(previous);

            var pending = previous.DisposeAndGetPendingChanges();

            Assert.Equal([project, imported], pending.Order());
            Assert.Empty(batches);
            Assert.Empty(previous.DisposeAndGetPendingChanges());

            using var replacement = new DotnetWatchProjectFileWatcher([project, imported],
                batches.Add, TimeSpan.Zero, exception => throw exception);
            replacement.Start();
            foreach (var path in pending)
            {
                File.AppendAllText(path, " ");
            }
            PollOnce(replacement);
            PollOnce(replacement);
            Assert.Equal([project, imported], Assert.Single(batches).Order());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PollOnce")]
    private static extern void PollOnce(DotnetWatchProjectFileWatcher watcher);
}
