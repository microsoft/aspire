// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    [InlineData("Api.csproj", true, false)]
    [InlineData("Common.props", true, false)]
    public void BuildFilesAreRecognized(string name, bool ancestorOnly, bool expected)
    {
        Assert.Equal(expected, DotnetWatchProjectFileWatcher.IsBuildFile(Path.Combine("dir", name), ancestorOnly));
    }

    [Fact]
    public void WatchesProjectAndReferencedDirectoriesAndTheirAncestors()
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
            File.WriteAllText(api, """<Project><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /><ProjectReference Include="$(Computed)\X.csproj" /></ItemGroup></Project>""");
            File.WriteAllText(lib, """<Project><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>""");
            File.WriteAllText(core, "<Project />");

            Assert.Equal(
                [Path.GetDirectoryName(lib) + Path.DirectorySeparatorChar, Path.GetDirectoryName(core) + Path.DirectorySeparatorChar],
                DotnetWatchProjectFileWatcher.ReadReferencedDirectories(api));

            var directories = DotnetWatchProjectFileWatcher.GetWatchedDirectories([api]);
            Assert.False(directories[Path.GetDirectoryName(api)!]);
            Assert.False(directories[Path.GetDirectoryName(lib)!]);
            Assert.False(directories[Path.GetDirectoryName(core)!]);
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

            var changes = new List<IReadOnlyList<string>>();
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new DotnetWatchProjectFileWatcher([project], c =>
            {
                lock (changes)
                {
                    changes.Add(c);
                }

                changed.TrySetResult();
            }, TimeSpan.FromMilliseconds(300));
            watcher.Start();

            File.WriteAllText(Path.Combine(root, "Api", "Program.cs"), "// not a build file");
            File.WriteAllText(project, "<Project><!-- 1 --></Project>");
            File.WriteAllText(project, "<Project><!-- 2 --></Project>");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />");

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(1000);

            lock (changes)
            {
                var batch = Assert.Single(changes);
                Assert.Contains(project, batch);
                Assert.Contains(Path.Combine(root, "Directory.Packages.props"), batch);
                Assert.DoesNotContain(batch, p => p.EndsWith("Program.cs", StringComparison.Ordinal));
                Assert.Equal(batch.Count, batch.Distinct().Count());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
