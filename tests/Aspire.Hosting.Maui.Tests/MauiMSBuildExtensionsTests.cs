// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Lifecycle;
using Aspire.Hosting.Maui.Utilities;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for the MAUI build/run MSBuild property injection extensions.
/// </summary>
public class MauiMSBuildExtensionsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void WithBuildProperty_AddsPropertyToAnnotation()
    {
        var device = CreateAndroidDevice();

        device.WithBuildProperty("AuthClientId", "client-id");

        Assert.True(device.Resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation));
        Assert.Equal("client-id", annotation.BuildProperties["AuthClientId"]);
        Assert.Empty(annotation.RunProperties);
    }

    [Fact]
    public void WithBuildProperty_CalledTwiceWithSameName_ReplacesValue()
    {
        var device = CreateAndroidDevice();

        device.WithBuildProperty("AuthClientId", "first")
              .WithBuildProperty("AuthClientId", "second");

        Assert.True(device.Resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation));
        Assert.Equal("second", annotation.BuildProperties["AuthClientId"]);
    }

    [Fact]
    public void WithRunProperty_AddsPropertyToAnnotation()
    {
        var device = CreateAndroidDevice();

        device.WithRunProperty("RunFlag", "on");

        Assert.True(device.Resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation));
        Assert.Equal("on", annotation.RunProperties["RunFlag"]);
        Assert.Empty(annotation.BuildProperties);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithBuildProperty_ThrowsWhenNameIsNullOrWhiteSpace(string? name)
    {
        var device = CreateAndroidDevice();

        Assert.ThrowsAny<ArgumentException>(() => device.WithBuildProperty(name!, "value"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("1StartsWithDigit")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    public void WithBuildProperty_ThrowsWhenNameIsNotValidMSBuildProperty(string name)
    {
        var device = CreateAndroidDevice();

        Assert.Throws<ArgumentException>(() => device.WithBuildProperty(name, "value"));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("_Name")]
    [InlineData("Name123")]
    public void WithBuildProperty_AcceptsValidMSBuildPropertyName(string name)
    {
        var device = CreateAndroidDevice();

        device.WithBuildProperty(name, "value");

        Assert.True(device.Resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation));
        Assert.Equal("value", annotation.BuildProperties[name]);
    }

    [Theory]
    [InlineData("MSBuildProjectDirectory")]
    [InlineData("MSBuildToolsVersion")]
    [InlineData("msbuildprojectfile")]
    public void WithBuildProperty_ThrowsWhenNameIsReserved(string name)
    {
        var device = CreateAndroidDevice();

        Assert.Throws<ArgumentException>(() => device.WithBuildProperty(name, "value"));
    }

    [Fact]
    public void WithRunProperty_ThrowsWhenNameIsReserved()
    {
        var device = CreateAndroidDevice();

        Assert.Throws<ArgumentException>(() => device.WithRunProperty("MSBuildProjectFullPath", "value"));
    }

    [Fact]
    public void WithBuildProperty_ThrowsWhenValueIsNull()
    {
        var device = CreateAndroidDevice();

        Assert.Throws<ArgumentNullException>(() => device.WithBuildProperty("name", null!));
    }

    [Fact]
    public async Task RunArgs_ImportBuildPropsFileAndPassRunPropertiesAsArguments()
    {
        var device = CreateAndroidDevice();

        device.WithBuildProperty("AuthClientId", "client-id")
              .WithRunProperty("RunFlag", "on");

        var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource).DefaultTimeout();

        var importArg = Assert.Single(args, a => a.StartsWith("-p:CustomBeforeMicrosoftCommonProps=", StringComparison.Ordinal));
        var propsFilePath = importArg["-p:CustomBeforeMicrosoftCommonProps=".Length..];
        Assert.True(File.Exists(propsFilePath));

        // The shared build .props file carries build properties only (imported by both build and run).
        var content = File.ReadAllText(propsFilePath);
        Assert.Contains("<AuthClientId>client-id</AuthClientId>", content);
        Assert.DoesNotContain("RunFlag", content);

        // Run properties are launch selectors passed as -p: args on the launch command only.
        Assert.Contains("-p:RunFlag=on", args);
    }

    [Fact]
    public async Task RunArgs_WithoutProperties_DoNotImportPropsFile()
    {
        var device = CreateAndroidDevice();

        var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource).DefaultTimeout();

        Assert.DoesNotContain(args, a => a.StartsWith("-p:CustomBeforeMicrosoftCommonProps=", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBuildPropsArgument_OnlyContainsBuildProperties()
    {
        var annotation = new MauiMSBuildPropertiesAnnotation();
        annotation.BuildProperties["Build"] = "b";
        annotation.RunProperties["Run"] = "r";

        var argument = annotation.CreateBuildPropsArgument("resource");

        Assert.NotNull(argument);
        var content = ReadPropsFileFromArgument(argument);
        Assert.Contains("<Build>b</Build>", content);
        Assert.DoesNotContain("<Run>r</Run>", content);
    }

    [Fact]
    public void CreateBuildPropsArgument_WithoutBuildProperties_ReturnsNull()
    {
        var annotation = new MauiMSBuildPropertiesAnnotation();
        annotation.RunProperties["Run"] = "r";

        Assert.Null(annotation.CreateBuildPropsArgument("resource"));
    }

    [Fact]
    public void CreateRunPropertyArguments_ReturnsRunPropertiesAsMSBuildArguments()
    {
        var annotation = new MauiMSBuildPropertiesAnnotation();
        annotation.BuildProperties["Shared"] = "build";
        annotation.RunProperties["Shared"] = "run";
        annotation.RunProperties["Device"] = "emulator-5554";

        var arguments = annotation.CreateRunPropertyArguments().ToList();

        // Only run properties are emitted, ordered by name and passed as -p: args. Command-line properties
        // override the same-named build property coming from the imported build .props file.
        Assert.Equal(["-p:Device=emulator-5554", "-p:Shared=run"], arguments);
    }

    [Fact]
    public void CreateBuildPropsArgument_ReturnsSameFileAcrossCalls()
    {
        var annotation = new MauiMSBuildPropertiesAnnotation();
        annotation.BuildProperties["Build"] = "b";

        // Build and run both import this argument; the file path (and content) must be identical so the
        // launch does not see different build inputs and rebuild.
        var first = annotation.CreateBuildPropsArgument("resource");
        var second = annotation.CreateBuildPropsArgument("resource");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GeneratePropsFileContent_EscapesXmlAndPreservesSpecialCharacters()
    {
        var content = MauiMSBuildPropsFileHelper.GeneratePropsFileContent(new Dictionary<string, string>
        {
            ["Constants"] = "A;B%C",
            ["Markup"] = "<x> & </x>"
        });

        // Scalar property values keep ';' and '%' literally; XML content escaping handles '<', '>', '&'.
        Assert.Contains("<Constants>A;B%C</Constants>", content);
        Assert.Contains("<Markup>&lt;x&gt; &amp; &lt;/x&gt;</Markup>", content);
    }

    [Fact]
    public async Task BuildProperty_SetInOnBeforeResourceStarted_IsVisibleToPreBuild()
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var device = appBuilder.AddMauiProject("mauiapp", tempFile).AddAndroidDevice();

        // Config-time subscription mutates the annotation from inside the event handler.
        // It registers before the build-queue subscriber (which subscribes at startup),
        // so under blocking-sequential dispatch it must run first — before the pre-build.
        device.OnBeforeResourceStarted((resource, evt, ct) =>
        {
            device.WithBuildProperty("FromEvent", "value");
            return Task.CompletedTask;
        });

        await using var app = appBuilder.Build();

        var subscriber = new CapturingBuildQueueSubscriber(
            app.Services.GetRequiredService<ResourceNotificationService>(),
            app.Services.GetRequiredService<ResourceLoggerService>(),
            app.Services.GetRequiredService<ResourceCommandService>());

        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var execContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        // Subscribe after the config-time handler above, mirroring startup ordering.
        await subscriber.SubscribeAsync(eventing, execContext, CancellationToken.None).DefaultTimeout();

        await eventing.PublishAsync(
            new BeforeResourceStartedEvent(device.Resource, app.Services),
            CancellationToken.None).DefaultTimeout();

        Assert.NotNull(subscriber.CapturedBuildPropsArgument);
        var content = ReadPropsFileFromArgument(subscriber.CapturedBuildPropsArgument);
        Assert.Contains("<FromEvent>value</FromEvent>", content);
    }

    private static string ReadPropsFileFromArgument(string argument)
    {
        const string prefix = "-p:CustomBeforeMicrosoftCommonProps=";
        Assert.StartsWith(prefix, argument);
        var propsFilePath = argument[prefix.Length..];
        Assert.True(File.Exists(propsFilePath));
        return File.ReadAllText(propsFilePath);
    }

    private IResourceBuilder<Maui.MauiAndroidDeviceResource> CreateAndroidDevice()
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        return maui.AddAndroidDevice();
    }

    /// <summary>
    /// A build-queue subscriber that captures the build props argument produced from the
    /// resource's annotation at the moment <see cref="MauiBuildQueueEventSubscriber.RunBuildAsync"/>
    /// runs, without launching a real <c>dotnet build</c>.
    /// </summary>
    private sealed class CapturingBuildQueueSubscriber(
        ResourceNotificationService notificationService,
        ResourceLoggerService loggerService,
        ResourceCommandService resourceCommandService)
        : MauiBuildQueueEventSubscriber(notificationService, loggerService, resourceCommandService)
    {
        public string? CapturedBuildPropsArgument { get; private set; }

        internal override Task RunBuildAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
        {
            if (resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation))
            {
                CapturedBuildPropsArgument = annotation.CreateBuildPropsArgument(resource.Name);
            }

            return Task.CompletedTask;
        }

        // No DCP launch phase in the test, so release the queue semaphore immediately.
        internal override Task ReleaseSemaphoreAfterLaunchAsync(IResource resource, SemaphoreSlim semaphore, string? stateAtCallTime, bool releaseOnRunning, ILogger logger, CancellationToken cancellationToken)
        {
            semaphore.Release();
            return Task.CompletedTask;
        }
    }
}
