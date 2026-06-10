// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.Utils;
using static Aspire.Hosting.VolumeNameGenerator;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class VolumeNameGeneratorTests
{
    [Fact]
    public void VolumeGeneratorUsesUniqueName()
    {
        var builder = DistributedApplication.CreateBuilder();

        var volumePrefix = $"{Sanitize(builder.Environment.ApplicationName).ToLowerInvariant()}-{builder.Configuration["AppHost:Sha256"]!.ToLowerInvariant()[..10]}";

        var resource = builder.AddResource(new TestResource("myresource"));

        var volumeName = Generate(resource, "data");

        Assert.Equal($"{volumePrefix}-{resource.Resource.Name}-data", volumeName);
    }

    [Theory]
    [MemberData(nameof(InvalidNameParts))]
    public void ThrowsWhenSuffixContainsInvalidChars(string suffix)
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddResource(new TestResource("myresource"));

        Assert.Throws<ArgumentException>(nameof(suffix), () => Generate(resource, suffix));
    }

    public static object[][] InvalidNameParts => [
        ["This/is/invalid"],
        [@"This\is\invalid"],
        ["_ThisIsInvalidToo"],
        [".ThisIsInvalidToo"],
        ["-ThisIsInvalidToo"],
        ["This&IsInvalidToo"]
    ];

    private sealed class TestResource(string name) : IResource
    {
        public string Name { get; } = name;

        public ResourceAnnotationCollection Annotations { get; } = [];
    }

    [Fact]
    public void VolumeNameDiffersBetweenPublishAndRun()
    {
        var runBuilder = TestDistributedApplicationBuilder.Create();
        var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var runVolumePrefix = $"{Sanitize(runBuilder.Environment.ApplicationName).ToLowerInvariant()}-{runBuilder.Configuration["AppHost:Sha256"]!.ToLowerInvariant()[..10]}";
        var publishVolumePrefix = $"{Sanitize(publishBuilder.Environment.ApplicationName).ToLowerInvariant()}-{publishBuilder.Configuration["AppHost:Sha256"]!.ToLowerInvariant()[..10]}";

        var runResource = runBuilder.AddResource(new TestResource("myresource"));
        var publishResource = publishBuilder.AddResource(new TestResource("myresource"));

        var runVolumeName = Generate(runResource, "data");
        var publishVolumeName = Generate(publishResource, "data");

        Assert.Equal($"{runVolumePrefix}-{runResource.Resource.Name}-data", runVolumeName);
        Assert.Equal($"{publishVolumePrefix}-{publishResource.Resource.Name}-data", publishVolumeName);
        Assert.NotEqual(runVolumeName, publishVolumeName);
    }

    [Theory]
    [InlineData(@"C:\Project\App")]
    [InlineData(@"c:\project\app")]
    [InlineData(@"C:/Project/App")]
    [InlineData(@"C:\Project\App\")]
    [InlineData(@"C:\Project\..\Project\App")]
    public void VolumeNameConsistentAcrossWindowsPathCasingsAndFormats(string projectDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SharedVolumeNameConsistencyTest(projectDirectory);
    }

    [Theory]
    [InlineData("/home/project/app")]
    [InlineData("/home/Project/App")]
    [InlineData("/home/project/app/")]
    [InlineData("/home/project/../project/app")]
    [InlineData("./project/app")]
    public void VolumeNameConsistentAcrossLinuxPathFormats(string projectDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SharedVolumeNameConsistencyTest(projectDirectory);
    }

    private static void SharedVolumeNameConsistencyTest(string projectDirectory)
    {
        var options = new DistributedApplicationOptions
        {
            ProjectDirectory = projectDirectory,
            Args = [] // Ensure run mode (default)
        };

        var builder = (DistributedApplicationBuilder)DistributedApplication.CreateBuilder(options);

        // Verify this is in run mode so path-based hashing is used
        Assert.False(builder.ExecutionContext.IsPublishMode);
        Assert.True(builder.ExecutionContext.IsRunMode);

        var appHostSha = builder.Configuration["AppHost:Sha256"];
        Assert.NotNull(appHostSha);

        // Create a resource and verify volume name generation works
        var resource = builder.AddResource(new TestResource("myresource"));
        var volumeName = Generate(resource, "data");
        Assert.NotNull(volumeName);
        Assert.Contains("myresource-data", volumeName);

        // Compute expected SHA from normalized AppHostPath
        var expectedShaBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.AppHostPath.ToLowerInvariant()));
        var expectedSha = Convert.ToHexString(expectedShaBytes);

        // The SHA from the configuration should match the SHA computed from normalized AppHostPath
        Assert.Equal(expectedSha, appHostSha);
    }
}
