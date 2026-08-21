// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for <see cref="MauiBuildArgumentsExtensions"/> covering annotation registration,
/// callback invocation, ordering, and argument mutation.
/// </summary>
public class MauiBuildArgumentsExtensionsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void WithMauiBuildArguments_RegistersAnnotationWithBuildStep()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        emulator.WithMauiBuildArguments(_ => { });

        var annotation = Assert.Single(emulator.Resource.Annotations.OfType<MauiBuildArgumentsCallbackAnnotation>());
        Assert.Equal(MauiBuildStep.Build, annotation.Step);
    }

    [Fact]
    public void WithMauiLaunchArguments_RegistersAnnotationWithLaunchStep()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        emulator.WithMauiLaunchArguments(_ => { });

        var annotation = Assert.Single(emulator.Resource.Annotations.OfType<MauiBuildArgumentsCallbackAnnotation>());
        Assert.Equal(MauiBuildStep.Launch, annotation.Step);
    }

    [Fact]
    public void WithMauiBuildArguments_ReturnsSameBuilder()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        var result = emulator.WithMauiBuildArguments(_ => { });

        Assert.Same(emulator, result);
    }

    [Fact]
    public void WithMauiLaunchArguments_ReturnsSameBuilder()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        var result = emulator.WithMauiLaunchArguments(_ => { });

        Assert.Same(emulator, result);
    }

    [Fact]
    public void WithMauiBuildArguments_NullBuilder_Throws()
    {
        IResourceBuilder<MauiAndroidEmulatorResource> builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.WithMauiBuildArguments(_ => { }));
    }

    [Fact]
    public void WithMauiBuildArguments_NullAsyncCallback_Throws()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        Assert.Throws<ArgumentNullException>(() => emulator.WithMauiBuildArguments((Func<MauiBuildArgumentsCallbackContext, Task>)null!));
    }

    [Fact]
    public void WithMauiBuildArguments_NullSyncCallback_Throws()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        Assert.Throws<ArgumentNullException>(() => emulator.WithMauiBuildArguments((Action<MauiBuildArgumentsCallbackContext>)null!));
    }

    [Fact]
    public void WithMauiLaunchArguments_NullBuilder_Throws()
    {
        IResourceBuilder<MauiAndroidEmulatorResource> builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.WithMauiLaunchArguments(_ => { }));
    }

    [Fact]
    public void WithMauiLaunchArguments_NullAsyncCallback_Throws()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        Assert.Throws<ArgumentNullException>(() => emulator.WithMauiLaunchArguments((Func<MauiBuildArgumentsCallbackContext, Task>)null!));
    }

    [Fact]
    public void WithMauiLaunchArguments_NullSyncCallback_Throws()
    {
        var emulator = CreateAndroidEmulator(outputHelper);

        Assert.Throws<ArgumentNullException>(() => emulator.WithMauiLaunchArguments((Action<MauiBuildArgumentsCallbackContext>)null!));
    }

    [Fact]
    public async Task WithMauiBuildArguments_SyncCallback_MutatesArguments()
    {
        var emulator = CreateAndroidEmulator(outputHelper);
        emulator.WithMauiBuildArguments(context => context.Arguments.Add("-p:MyProperty=Value"));

        var arguments = new List<string>();
        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Build, arguments);

        Assert.Contains("-p:MyProperty=Value", arguments);
    }

    [Fact]
    public async Task WithMauiBuildArguments_AsyncCallback_MutatesArguments()
    {
        var emulator = CreateAndroidEmulator(outputHelper);
        emulator.WithMauiBuildArguments(context =>
        {
            context.Arguments.Add("-p:AsyncProperty=Value");
            return Task.CompletedTask;
        });

        var arguments = new List<string>();
        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Build, arguments);

        Assert.Contains("-p:AsyncProperty=Value", arguments);
    }

    [Fact]
    public async Task MultipleCallbacks_InvokedInRegistrationOrder()
    {
        var emulator = CreateAndroidEmulator(outputHelper);
        emulator.WithMauiBuildArguments(context => context.Arguments.Add("first"));
        emulator.WithMauiBuildArguments(context => context.Arguments.Add("second"));
        emulator.WithMauiBuildArguments(context => context.Arguments.Add("third"));

        var arguments = new List<string>();
        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Build, arguments);

        Assert.Equal(["first", "second", "third"], arguments);
    }

    [Fact]
    public async Task BuildAndLaunchCallbacks_AreScopedToTheirOwnStep()
    {
        var emulator = CreateAndroidEmulator(outputHelper);
        emulator.WithMauiBuildArguments(context => context.Arguments.Add("build-only"));
        emulator.WithMauiLaunchArguments(context => context.Arguments.Add("launch-only"));

        var buildArgs = new List<string>();
        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Build, buildArgs);

        var launchArgs = new List<string>();
        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Launch, launchArgs);

        Assert.Equal(["build-only"], buildArgs);
        Assert.Equal(["launch-only"], launchArgs);
    }

    [Fact]
    public async Task Callback_ReceivesResourceAndStepInContext()
    {
        var emulator = CreateAndroidEmulator(outputHelper);
        MauiBuildArgumentsCallbackContext? captured = null;
        emulator.WithMauiLaunchArguments(context => captured = context);

        await InvokeCallbacksAsync(emulator.Resource, MauiBuildStep.Launch, new List<string>());

        Assert.NotNull(captured);
        Assert.Same(emulator.Resource, captured.Resource);
        Assert.Equal(MauiBuildStep.Launch, captured.Step);
    }

    private static IResourceBuilder<MauiAndroidEmulatorResource> CreateAndroidEmulator(ITestOutputHelper outputHelper)
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        return maui.AddAndroidEmulator("emulator");
    }

    private static async Task InvokeCallbacksAsync(IResource resource, MauiBuildStep step, IList<string> arguments)
    {
        var context = new MauiBuildArgumentsCallbackContext(step, arguments, resource, CancellationToken.None);
        foreach (var annotation in resource.Annotations.OfType<MauiBuildArgumentsCallbackAnnotation>().Where(a => a.Step == step))
        {
            await annotation.Callback(context);
        }
    }
}
