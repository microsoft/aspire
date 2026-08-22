// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.
#pragma warning disable IDE0005 // Using directive is unnecessary.

using System.Text.Json;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

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
            .WithDebugSupport(_ => launchConfig, "ms-python.python");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        var producedLaunchConfig = Assert.IsType<ExecutableLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(
                executable.Resource,
                LaunchConfigurationTestHelpers.CreateCallbackContext(
                    executable.Resource,
                    ExecutableLaunchMode.NoDebug)));
        Assert.Equal("ms-python.python", annotation.LaunchConfigurationType);

        Assert.Equal(launchConfig.Mode, producedLaunchConfig.Mode);
        Assert.Equal(launchConfig.Type, producedLaunchConfig.Type);
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

    [Fact]
    public async Task WithDebugSupportAsynchronousProducerProducesTheSameAnnotationAsTheSynchronousOne()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var syncExecutable = builder.AddExecutable("sync", "command", "workingdirectory")
            .WithDebugSupport(mode => new ExecutableLaunchConfiguration("go") { Mode = mode }, "go");
        var asyncExecutable = builder.AddExecutable("async", "command", "workingdirectory")
            .WithDebugSupport(async (mode, _) =>
            {
                await Task.Yield();
                return new ExecutableLaunchConfiguration("go") { Mode = mode };
            }, "go");

        var syncConfiguration = Assert.IsType<ExecutableLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(
                syncExecutable.Resource,
                LaunchConfigurationTestHelpers.CreateCallbackContext(syncExecutable.Resource)));
        var asyncConfiguration = Assert.IsType<ExecutableLaunchConfiguration>(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(
                asyncExecutable.Resource,
                LaunchConfigurationTestHelpers.CreateCallbackContext(asyncExecutable.Resource)));

        Assert.Equal(asyncConfiguration.Type, syncConfiguration.Type);
        Assert.Equal(asyncConfiguration.Mode, syncConfiguration.Mode);
    }

    [Fact]
    public void WithDebugSupportRejectsATaskReturningSynchronousProducer()
    {
        // `mode => Task.FromResult(...)` binds to the synchronous overload (overload resolution only
        // looks at the lambda's parameter count) with TLaunchConfiguration inferred as Task<T>, so the
        // task itself would be serialized as the launch configuration. It must be rejected up front.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory");

        var exception = Assert.Throws<ArgumentException>(
            () => executable.WithDebugSupport(mode => Task.FromResult(new ExecutableLaunchConfiguration("go") { Mode = mode }), "go"));

        Assert.Equal("launchConfigurationProducer", exception.ParamName);
        Assert.Equal(CreateAsyncProducerGuardMessage(typeof(Task<ExecutableLaunchConfiguration>), "launchConfigurationProducer"), exception.Message);
    }

    [Fact]
    public void WithDebugSupportRejectsAValueTaskReturningSynchronousProducer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var executable = builder.AddExecutable("myexe", "command", "workingdirectory");

        var exception = Assert.Throws<ArgumentException>(
            () => executable.WithDebugSupport(mode => ValueTask.FromResult(new ExecutableLaunchConfiguration("go") { Mode = mode }), "go"));

        Assert.Equal("launchConfigurationProducer", exception.ParamName);
        Assert.Equal(CreateAsyncProducerGuardMessage(typeof(ValueTask<ExecutableLaunchConfiguration>), "launchConfigurationProducer"), exception.Message);
    }

    [Fact]
    public async Task WithLaunchToolArgsLeadTheCommandLine()
    {
        // The launch tool arguments are the tool-invocation prefix, so they always come first and the program's own
        // arguments follow, regardless of the launch configuration in effect.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("launch-tool-arg"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go");

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("launch-tool-arg", arg),
            arg => Assert.Equal("base-arg", arg));
    }

    [Fact]
    public async Task ProcessArgumentValuesAsyncIncludesLaunchToolArgsForExecutables()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithLaunchToolArgs(context => context.Args.Add("launch-tool-arg"));
        var args = new List<string>();

#pragma warning disable CS0618 // Type or member is obsolete
        await executable.Resource.ProcessArgumentValuesAsync(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            (_, value, exception, _) =>
            {
                Assert.Null(exception);
                args.Add(Assert.IsType<string>(value));
            },
            NullLogger.Instance);
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Equal(["launch-tool-arg", "base-arg"], args);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithLaunchToolArgsAreOrderIndependent(bool launchToolArgsFirst)
    {
        // Regression coverage for https://github.com/microsoft/aspire/issues/18929: the launch tool arguments used to
        // be applied by an ordinary WithArgs callback that *removed* the prefix, which only worked when
        // WithDebugSupport happened to be called after the callback that added it. Declaring the prefix separately
        // instead of subtracting it must produce the same command line either way.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go"]
        });

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go");

        if (launchToolArgsFirst)
        {
            executable
                .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go")
                .WithArgs("base-arg");
        }
        else
        {
            executable
                .WithArgs("base-arg")
                .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go");
        }

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("base-arg", arg));
    }

    [Fact]
    public async Task WithLaunchToolArgsSurviveAnArgsCallbackThatClearsTheList()
    {
        // No WithArgs callback can observe or clear the tool-invocation prefix: it is evaluated separately and resolved
        // ahead of the arguments those callbacks produce.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("launch-tool-arg"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go")
            .WithArgs("discarded")
            .WithArgs(ctx =>
            {
                ctx.Args.Clear();
                ctx.Args.Add("only-arg");
            });

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("launch-tool-arg", arg),
            arg => Assert.Equal("only-arg", arg));
    }

    [Fact]
    public async Task WithLaunchToolArgsRemainInTheAppModelDuringADebugSession()
    {
        // Withholding the prefix is a DCP-level concern. The application model must keep describing the real
        // command line so the dashboard, the manifest and GetArgumentValuesAsync() consumers stay accurate.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["go"]
        });

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go");

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("base-arg", arg));
    }

    [Fact]
    public void WithLaunchToolArgsAreOwnedByMatchingLaunchConfigurationType()
    {
        // The tool invocation is owned by the launch configuration type it was declared with, so a launch configuration
        // of a different type does not claim it.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("project"), "project");

        var annotations = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().ToList();
        Assert.Collection(annotations,
            annotation => Assert.True(executable.Resource.HasLaunchToolArgsOwnedBy(annotation)),
            annotation => Assert.False(executable.Resource.HasLaunchToolArgsOwnedBy(annotation)));
    }

    private static string CreateAsyncProducerGuardMessage(Type producerReturnType, string parameterName)
    {
        var guidance = $"The launch configuration producer returns '{producerReturnType}'. An asynchronous producer must bind to an asynchronous {nameof(ResourceBuilderExtensions.WithDebugSupport)} overload either by accepting the launch mode and a {nameof(CancellationToken)} or by accepting a {nameof(LaunchConfigurationCallbackContext)}; otherwise the task itself is used as the launch configuration.";

        return new ArgumentException(guidance, parameterName).Message;
    }

    [Fact]
    public void WithDebugSupportDoesNotOwnLaunchToolArgsWithoutWithLaunchToolArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go");

        var annotation = executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().Single();
        Assert.False(executable.Resource.HasLaunchToolArgsOwnedBy(annotation));
    }

    [Fact]
    public async Task WithLaunchToolArgsAreRegisteredInPublishMode()
    {
        // Debug support is run-mode only, but the launch tool arguments describe how the resource is invoked in general and
        // must survive into the manifest and generated container images.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go");

        Assert.Empty(executable.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("base-arg", arg));
    }

    [Fact]
    public async Task WithLaunchToolArgsAreNotCarriedIntoAPublishedContainer()
    {
        // PublishAsDockerFile reuses the executable's annotations for the generated container resource, but a
        // container invokes the program through the image's ENTRYPOINT, so repeating the tool prefix in the
        // container's arguments would run the wrong command.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddExecutable("myexe", "command", "workingdirectory")
            .WithArgs("base-arg")
            .WithLaunchToolArgs(ctx => ctx.Args.Add("run"), ownedByLaunchConfigurationType: "go")
            .WithDebugSupport(_ => new ExecutableLaunchConfiguration("go"), "go")
            .PublishAsDockerFile();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(container);

        Assert.Empty(args);

        var processedArgs = new List<string>();
#pragma warning disable CS0618 // Type or member is obsolete
        await container.ProcessArgumentValuesAsync(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            (_, value, exception, _) =>
            {
                Assert.Null(exception);
                processedArgs.Add(Assert.IsType<string>(value));
            },
            NullLogger.Instance);
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Empty(processedArgs);
    }
}
