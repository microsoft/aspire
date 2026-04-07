// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable IDE0005 // Using directive is unnecessary.

using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ExecutableResourceBuilderExtensionTests
{
    [Theory]
    [InlineData("/absolute")]
    [InlineData("relative")]
    public void AddExecutableNormalisesWorkingDirectory(string workingDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", workingDirectory);

        var expectedPath = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, workingDirectory));
        var annotation = executable.Resource.Annotations.OfType<ExecutableAnnotation>().Single();
        Assert.Equal(expectedPath, annotation.WorkingDirectory);
    }

    [Fact]
    public void WithCommandMutatesCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory");

        executable.WithCommand("newcommand");
        var annotation = executable.Resource.Annotations.OfType<ExecutableAnnotation>().Single();
        Assert.Equal("newcommand", annotation.Command);
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("relative")]
    public void WithWorkingDirectoryMutatesAndNormalisesWorkingDirectory(string workingDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", "/whatever/workingdirectory");

        executable.WithWorkingDirectory(workingDirectory);

        var expectedPath = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, workingDirectory));
        var annotation = executable.Resource.Annotations.OfType<ExecutableAnnotation>().Single();
        Assert.Equal(expectedPath, annotation.WorkingDirectory);
    }

    [Fact]
    public void WithCommandDoesNotAllowEmptyString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory");

        Assert.Throws<ArgumentException>(() => executable.WithCommand(""));
    }

    [Fact]
    public void WithWorkingDirectoryAllowsEmptyString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory");

        executable.WithWorkingDirectory("");

        var annotation = executable.Resource.Annotations.OfType<ExecutableAnnotation>().Single();
        Assert.Equal(builder.AppHostDirectory, annotation.WorkingDirectory);
    }

    [Fact]
    public void WithDebugSupportAddsAnnotationInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var launchConfig = new ExecutableLaunchConfiguration("python");
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(_ => launchConfig, "ms-python.python");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        var exe = new Executable(new ExecutableSpec());
        annotation.LaunchConfigurationAnnotator(exe, "NoDebug");
        Assert.Equal("ms-python.python", annotation.LaunchConfigurationType);

        Assert.True(exe.TryGetAnnotationAsObjectList<ExecutableLaunchConfiguration>(Executable.LaunchConfigurationsAnnotation, out var annotations));
        Assert.Equal(launchConfig.Mode, annotations.Single().Mode);
        Assert.Equal(launchConfig.Type, annotations.Single().Type);
    }

    [Fact]
    public void WithDebugSupportDoesNotAddAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("python"), "ms-python.python");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }
}
