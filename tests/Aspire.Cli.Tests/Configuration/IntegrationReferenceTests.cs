// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class IntegrationReferenceTests
{
    [Fact]
    public void PackageReference_HasVersionAndNoProjectPath()
    {
        var reference = IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0");

        Assert.True(reference.IsPackageReference);
        Assert.False(reference.IsProjectReference);
        Assert.Equal("13.2.0", reference.Version);
        Assert.Null(reference.ProjectPath);
    }

    [Fact]
    public void ProjectReference_HasProjectPathAndNoVersion()
    {
        var reference = IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj");

        Assert.True(reference.IsProjectReference);
        Assert.False(reference.IsPackageReference);
        Assert.Null(reference.Version);
        Assert.Equal("/path/to/MyIntegration.csproj", reference.ProjectPath);
    }

    [Fact]
    public void GetIntegrationReferences_DetectsCsprojAsProjectReference()
    {
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = "13.2.0",
                ["MyIntegration"] = "../src/MyIntegration/MyIntegration.csproj"
            }
        };

        var refs = config.GetIntegrationReferences("13.2.0", "/home/user/app").ToList();

        // Base Aspire.Hosting + Redis (packages) + MyIntegration (project ref) = 3
        Assert.Equal(3, refs.Count);

        var packageRefs = refs.Where(r => r.IsPackageReference).ToList();
        var projectRefs = refs.Where(r => r.IsProjectReference).ToList();

        Assert.Equal(2, packageRefs.Count);
        Assert.Single(projectRefs);
        Assert.Equal("MyIntegration", projectRefs[0].Name);
        Assert.EndsWith(".csproj", projectRefs[0].ProjectPath!);
    }

    [Fact]
    public void GetIntegrationReferences_ResolvesRelativeProjectPath()
    {
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["MyIntegration"] = "../MyIntegration/MyIntegration.csproj"
            }
        };

        var refs = config.GetIntegrationReferences("13.2.0", "/home/user/app").ToList();
        var projectRef = refs.Single(r => r.IsProjectReference);

        // Path should be resolved to absolute
        Assert.True(Path.IsPathRooted(projectRef.ProjectPath!));
    }

    [Fact]
    public void GetIntegrationReferences_EmptyVersionDefaultsToSdkVersion()
    {
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting.Redis"] = ""
            }
        };

        var refs = config.GetIntegrationReferences("13.2.0", "/tmp").ToList();
        var redis = refs.Single(r => r.Name == "Aspire.Hosting.Redis");

        Assert.Equal("13.2.0", redis.Version);
        Assert.True(redis.IsPackageReference);
    }

    [Fact]
    public void GetIntegrationReferences_SkipsBasePackages()
    {
        var config = new AspireJsonConfiguration
        {
            SdkVersion = "13.2.0",
            Packages = new Dictionary<string, string>
            {
                ["Aspire.Hosting"] = "13.2.0",
                ["Aspire.Hosting.AppHost"] = "13.2.0",
                ["Aspire.Hosting.Redis"] = "13.2.0"
            }
        };

        var refs = config.GetIntegrationReferences("13.2.0", "/tmp").ToList();

        // Base Aspire.Hosting (auto-added) + Redis = 2
        // Aspire.Hosting from packages dict is skipped (duplicate)
        // Aspire.Hosting.AppHost is skipped (SDK-only)
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting");
        Assert.Contains(refs, r => r.Name == "Aspire.Hosting.Redis");
    }
}
