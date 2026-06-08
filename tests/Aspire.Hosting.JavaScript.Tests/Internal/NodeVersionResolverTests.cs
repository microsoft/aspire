// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class NodeVersionResolverTests
{
    [Fact]
    public void RootNvmrcWinsWhenAppDirHasNoPin()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var appDir = Path.Combine(root.FullName, "packages", "web");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(root.FullName, ".nvmrc"), "20");

            var version = NodeVersionResolver.ResolveNodeVersion(appDir, NullLogger.Instance, root.FullName);

            Assert.Equal("20", version);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void RootToolVersionsNodejsLineWins()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var appDir = Path.Combine(root.FullName, "packages", "web");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(root.FullName, ".tool-versions"), "nodejs 18.20.0\npython 3.12\n");

            var version = NodeVersionResolver.ResolveNodeVersion(appDir, NullLogger.Instance, root.FullName);

            Assert.Equal("18", version);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void PerAppNvmrcOverridesRoot()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var appDir = Path.Combine(root.FullName, "packages", "web");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(root.FullName, ".nvmrc"), "20");
            File.WriteAllText(Path.Combine(appDir, ".nvmrc"), "22");

            var version = NodeVersionResolver.ResolveNodeVersion(appDir, NullLogger.Instance, root.FullName);

            Assert.Equal("22", version);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void NoPinAnywhereReturnsDefaultNodeVersion()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var appDir = Path.Combine(root.FullName, "packages", "web");
            Directory.CreateDirectory(appDir);

            var version = NodeVersionResolver.ResolveNodeVersion(appDir, NullLogger.Instance, root.FullName);

            Assert.Equal(NodeVersionResolver.DefaultNodeVersion, version);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceRootEqualToWorkingDirIsSafe()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, ".nvmrc"), "20");

            // When workspaceRoot == workingDirectory the resolver must not double-read; the app-dir
            // pass alone still finds the pin.
            var version = NodeVersionResolver.ResolveNodeVersion(root.FullName, NullLogger.Instance, root.FullName);

            Assert.Equal("20", version);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }
}
