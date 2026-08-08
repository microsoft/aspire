// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable IDE0005 // Using directive is unnecessary.

using System.Text.Json;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
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
    public void WithPersistentLifetimeAddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithPersistentLifetime();

        var annotation = executable.Resource.Annotations.OfType<PersistenceAnnotation>().Single();
        Assert.Equal(PersistenceMode.Persistent, annotation.Mode);
    }

    [Fact]
    public async Task WithDebugSupportAddsAnnotationInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var launchConfig = new ExecutableLaunchConfiguration("python");
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(_ => Task.FromResult(launchConfig), "ms-python.python");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        var exe = new Executable(new ExecutableSpec());
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            executable.Resource,
            ExecutableLaunchMode.NoDebug);
        await annotation.LaunchConfigurationAnnotator(exe, callbackContext);
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
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("python")),
                "ms-python.python");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public async Task WithDebugSupportSupportsAContextIgnoringProducer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var executable = builder.AddExecutable("async", "command", "workingdirectory")
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go");

        var launchConfiguration = Assert.IsType<ExecutableLaunchConfiguration>(
            await executable.Resource.CreateLaunchConfigurationAsync(
                LaunchConfigurationTestHelpers.CreateCallbackContext(executable.Resource)));

        Assert.Equal("go", launchConfiguration.Type);
    }

    [Fact]
#pragma warning disable CS0618 // Verify the shipped overload remains source-compatible while forwarding to the context overload.
    public async Task WithDebugSupportLegacyModeProducerOverloadForwardsToContextProducer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        string? observedMode = null;
        var executable = builder.AddExecutable("legacy", "command", "workingdirectory")
            .WithDebugSupport(
                (string mode) =>
                {
                    observedMode = mode;
                    return new ExecutableLaunchConfiguration("go")
                    {
                        Mode = mode
                    };
                },
                "go");

        var launchConfiguration = Assert.IsType<ExecutableLaunchConfiguration>(
            await executable.Resource.CreateLaunchConfigurationAsync(
                LaunchConfigurationTestHelpers.CreateCallbackContext(
                    executable.Resource,
                    ExecutableLaunchMode.Debug)));

        Assert.Equal(ExecutableLaunchMode.Debug, observedMode);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfiguration.Mode);
        Assert.Equal("go", launchConfiguration.Type);
    }
#pragma warning restore CS0618

    [Fact]
#pragma warning disable CS0618 // Verify the shipped overload preserves its task-return validation.
    public void WithDebugSupportLegacyModeProducerOverloadRejectsTaskReturningProducer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var taskException = Assert.Throws<InvalidOperationException>(() =>
            builder.AddExecutable("task", "command", "workingdirectory")
                .WithDebugSupport(
                    (string mode) => Task.FromResult(new ExecutableLaunchConfiguration("go") { Mode = mode }),
                    "go"));
        Assert.Contains("Task", taskException.Message);

        var valueTaskException = Assert.Throws<InvalidOperationException>(() =>
            builder.AddExecutable("value-task", "command", "workingdirectory")
                .WithDebugSupport(
                    (string mode) => ValueTask.FromResult(new ExecutableLaunchConfiguration("go") { Mode = mode }),
                    "go"));
        Assert.Contains("ValueTask", valueTaskException.Message);
    }
#pragma warning restore CS0618

    [Fact]
    public async Task WithDebugSupportArgsCallbackRunsWhenItsAnnotationIsActive()
    {
        // A single WithDebugSupport call whose annotation is active (last) must run its
        // argument-rewriting callback. This verifies the normal single-integration path
        // independently of the multiple-annotation behavior below.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go"]
        });

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go",
                ctx => ctx.Args.Add("rewritten-arg"));

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("base-arg", arg),
            arg => Assert.Equal("rewritten-arg", arg));
    }

    [Fact]
    public async Task WithDebugSupportArgsCallbackDoesNotRunWhenLaterDebugSupportSupersedesIt()
    {
        // WithDebugSupport is append-only and SupportsDebugging() only consults the LAST
        // SupportsDebuggingAnnotation. A resource can gain debug support from more than one caller: e.g. a
        // Go/Python integration that rewrites the entrypoint args (rewritesArgumentsForDebugging: true),
        // followed by a second WithDebugSupport that does not. Once the later annotation supersedes the
        // first, the first call's arg-rewriting callback must NOT fire; otherwise it would strip/append
        // args while the active annotation reports RewritesArgumentsForDebugging == false and
        // ExecutableCreator would offer a Process fallback built from the mangled arguments.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go", "project"]
        });

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go",
                ctx => ctx.Args.Add("rewritten-arg"))
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("project")),
                "project");

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("base-arg", arg));
    }

    [Fact]
    public void WithDebugSupportReportsRewritesArgumentsWhenResourceSupportsArgs()
    {
        // A resource that carries command-line arguments (IResourceWithArgs) actually gets the
        // arg-rewriting callback attached, so the annotation must advertise that it rewrites args.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go",
                ctx => ctx.Args.Add("rewritten-arg"));

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Single();
        Assert.True(annotation.RewritesArgumentsForDebugging);
    }

    [Fact]
    public void WithDebugSupportDoesNotReportRewritesArgumentsWhenResourceHasNoArgs()
    {
        // If a caller supplies an argsCallback for a resource that is not IResourceWithArgs, the callback
        // is never registered and the arguments are left unchanged. The annotation must therefore report
        // RewritesArgumentsForDebugging == false so ExecutableCreator still offers the Process fallbacks.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var resource = builder.AddResource(new DebuggableResourceWithoutArgs("noargs"))
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go",
                ctx => ctx.Args.Add("rewritten-arg"));

        var annotation = resource.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Single();
        Assert.False(annotation.RewritesArgumentsForDebugging);
    }

    [Fact]
    public void WithDebugSupportDoesNotReportRewritesArgumentsWhenNoArgsCallbackProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(
                static _ => Task.FromResult(new ExecutableLaunchConfiguration("go")),
                "go");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Single();
        Assert.False(annotation.RewritesArgumentsForDebugging);
    }

    private sealed class DebuggableResourceWithoutArgs(string name) : Resource(name);
}
