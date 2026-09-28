// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001 // RequiredCommandAnnotation is for evaluation purposes only.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddNpxTests
{
    [Fact]
    public void AddNpxAddsResourceAndRequiredCommands()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("lint", "eslint");

        Assert.Equal("lint", resource.Resource.Name);
        Assert.Equal("npx", resource.Resource.Command);
        Assert.Equal(["npx", "node"], resource.Resource.Annotations.OfType<RequiredCommandAnnotation>().Select(a => a.Command));
        Assert.Equal("Npx", resource.Resource.Annotations.OfType<ResourceSnapshotAnnotation>().Single().InitialSnapshot.ResourceType);
    }

    [Fact]
    public async Task AddNpxSeparatesNpxOptionsFromPackageArguments()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("lint", "eslint")
            .WithNpxVersion("9.25.1")
            .WithNpxRegistry("https://registry.example.com/")
            .WithNpxPreferOffline()
            .WithNpxArgs("--foreground-scripts")
            .WithArgs(".", "--fix");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource).DefaultTimeout();

        Assert.Equal(
            ["--yes", "--registry", "https://registry.example.com/", "--prefer-offline", "--foreground-scripts", "eslint@9.25.1", "--", ".", "--fix"],
            args);
    }

    [Fact]
    public async Task AddNpxWithExecutableUsesPackageOption()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("generator", "@example/generator")
            .WithNpxVersion("next")
            .WithNpxExecutable("create-example")
            .WithArgs("--help");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource).DefaultTimeout();

        Assert.Equal(
            ["--yes", "--package", "@example/generator@next", "create-example", "--", "--help"],
            args);
    }

    [Fact]
    public async Task AddNpxCachePreferenceUsesLastSetting()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("lint", "eslint")
            .WithNpxOffline()
            .WithNpxPreferOnline();

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource).DefaultTimeout();

        Assert.Equal(["--yes", "--prefer-online", "eslint", "--"], args);
    }

    [Fact]
    public async Task AddNpxVersionOverridesVersionInPackageSpec()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("tool", "foo@123")
            .WithNpxVersion("456");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource).DefaultTimeout();

        Assert.Equal(["--yes", "foo@456", "--"], args);
    }

    [Fact]
    public async Task AddNpxSupportsNamespacedPackageWithEmbeddedVersion()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("mcp", "@azure/mcp@1.2.3")
            .WithNpxExecutable("mcp");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource).DefaultTimeout();

        Assert.Equal(["--yes", "--package", "@azure/mcp@1.2.3", "mcp", "--"], args);
    }

    [Fact]
    public async Task AddNpxGeneratesExecutableManifestWithPackageAndToolArguments()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resource = builder.AddNpx("lint", "eslint")
            .WithNpxVersion("9.25.1")
            .WithNpxRegistry("https://registry.example.com/")
            .WithArgs(".", "--fix");

        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifest(resource.Resource).DefaultTimeout();
        var expectedManifest =
        """
        {
          "type": "executable.v0",
          "workingDirectory": ".",
          "command": "npx",
          "args": [
            "--yes",
            "--registry",
            "https://registry.example.com/",
            "eslint@9.25.1",
            "--",
            ".",
            "--fix"
          ]
        }
        """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }
}

#pragma warning restore ASPIRECOMMAND001
