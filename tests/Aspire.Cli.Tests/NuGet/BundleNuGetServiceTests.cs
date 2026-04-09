// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class BundleNuGetServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RestorePackagesAsync_UsesWorkspaceAspireDirectoryForRestoreArtifacts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.CreateDirectory("apphost");
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(
            managedDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(managedPath, string.Empty);

        List<string[]> invocations = [];
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) => invocations.Add(args.ToArray())
        };

        var service = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(executionFactory),
            NullLogger<BundleNuGetService>.Instance);

        var libsPath = await service.RestorePackagesAsync(
            [("Aspire.Hosting.JavaScript", "9.4.0")],
            workingDirectory: appHostDirectory.FullName);

        var restoreRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "packages", "restore");
        var restoreDirectory = Directory.GetParent(libsPath)!.FullName;

        Assert.StartsWith(restoreRoot, libsPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invocations.Count);
        Assert.Equal(Path.Combine(restoreDirectory, "obj"), GetArgumentValue(invocations[0], "--output"));
        Assert.Equal(libsPath, GetArgumentValue(invocations[1], "--output"));
        Assert.Equal(Path.Combine(restoreDirectory, "obj", "project.assets.json"), GetArgumentValue(invocations[1], "--assets"));
    }

    private static string GetArgumentValue(string[] arguments, string optionName)
    {
        var optionIndex = Array.IndexOf(arguments, optionName);
        Assert.True(optionIndex >= 0 && optionIndex < arguments.Length - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
