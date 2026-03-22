// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class RequiredCommandAnnotationTests
{
    [Fact]
    public void RequiredCommandAnnotation_StoresCommand()
    {
        var annotation = new RequiredCommandAnnotation("test-command");

        Assert.Equal("test-command", annotation.Command);
    }

    [Fact]
    public void RequiredCommandAnnotation_ThrowsOnNullCommand()
    {
        Assert.Throws<ArgumentNullException>(() => new RequiredCommandAnnotation(null!));
    }

    [Fact]
    public void RequiredCommandAnnotation_CanSetHelpLink()
    {
        var annotation = new RequiredCommandAnnotation("test-command")
        {
            HelpLink = "https://example.com/help"
        };

        Assert.Equal("https://example.com/help", annotation.HelpLink);
    }

    [Fact]
    public void RequiredCommandAnnotation_CanSetValidationCallback()
    {
        Func<RequiredCommandValidationContext, Task<RequiredCommandValidationResult>> callback =
            ctx => Task.FromResult(RequiredCommandValidationResult.Success());

        var annotation = new RequiredCommandAnnotation("test-command")
        {
            ValidationCallback = callback
        };

        Assert.NotNull(annotation.ValidationCallback);
        Assert.Same(callback, annotation.ValidationCallback);
    }

    [Fact]
    public void WithRequiredCommand_AddsAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("test", "image");

        resourceBuilder.WithRequiredCommand("test-command");

        var annotation = resourceBuilder.Resource.Annotations.OfType<RequiredCommandAnnotation>().Single();
        Assert.Equal("test-command", annotation.Command);
        Assert.Null(annotation.HelpLink);
        Assert.Null(annotation.ValidationCallback);
    }

    [Fact]
    public void WithRequiredCommand_AddsAnnotationWithHelpLink()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("test", "image");

        resourceBuilder.WithRequiredCommand("test-command", "https://example.com/help");

        var annotation = resourceBuilder.Resource.Annotations.OfType<RequiredCommandAnnotation>().Single();
        Assert.Equal("test-command", annotation.Command);
        Assert.Equal("https://example.com/help", annotation.HelpLink);
        Assert.Null(annotation.ValidationCallback);
    }

    [Fact]
    public void WithRequiredCommand_AddsAnnotationWithValidationCallback()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("test", "image");
        Func<RequiredCommandValidationContext, Task<RequiredCommandValidationResult>> callback =
            ctx => Task.FromResult(RequiredCommandValidationResult.Success());

        resourceBuilder.WithRequiredCommand("test-command", callback);

        var annotation = resourceBuilder.Resource.Annotations.OfType<RequiredCommandAnnotation>().Single();
        Assert.Equal("test-command", annotation.Command);
        Assert.Null(annotation.HelpLink);
        Assert.NotNull(annotation.ValidationCallback);
    }

    [Fact]
    public void WithRequiredCommand_CanAddMultipleAnnotations()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("test", "image");

        resourceBuilder
            .WithRequiredCommand("command1")
            .WithRequiredCommand("command2");

        var annotations = resourceBuilder.Resource.Annotations.OfType<RequiredCommandAnnotation>().ToList();
        Assert.Equal(2, annotations.Count);
        Assert.Equal("command1", annotations[0].Command);
        Assert.Equal("command2", annotations[1].Command);
    }

    [Fact]
    public void WithRequiredCommand_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RequiredCommandResourceExtensions.WithRequiredCommand<ContainerResource>(null!, "test"));
    }

    [Fact]
    public void WithRequiredCommand_ThrowsOnNullCommand()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("test", "image");

        Assert.Throws<ArgumentNullException>(() => resourceBuilder.WithRequiredCommand(null!));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_IsRegistered()
    {
        var builder = DistributedApplication.CreateBuilder();

        await using var app = builder.Build();

        var subscribers = app.Services.GetServices<IDistributedApplicationEventingSubscriber>();
        Assert.Contains(subscribers, s => s is RequiredCommandValidationLifecycleHook);
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_ValidatesExistingCommand()
    {
        var builder = DistributedApplication.CreateBuilder();
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";
        builder.AddContainer("test", "image").WithRequiredCommand(command);

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_LogsWarningForMissingCommand()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("test", "image").WithRequiredCommand("this-command-definitely-does-not-exist-12345");

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Should not throw - just logs a warning and allows the resource to attempt start
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_IncludesHelpLinkInWarning()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("test", "image")
            .WithRequiredCommand("missing-command", "https://example.com/install");

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Should not throw - just logs a warning and allows the resource to attempt start
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CallsValidationCallback()
    {
        var builder = DistributedApplication.CreateBuilder();
        var callbackInvoked = false;
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";

        builder.AddContainer("test", "image")
            .WithRequiredCommand(command, ctx =>
            {
                callbackInvoked = true;
                return Task.FromResult(RequiredCommandValidationResult.Success());
            });

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));

        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CallsValidationCallbackWithContext()
    {
        var builder = DistributedApplication.CreateBuilder();
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";
        string? capturedPath = null;
        IServiceProvider? capturedServices = null;

        builder.AddContainer("test", "image")
            .WithRequiredCommand(command, ctx =>
            {
                capturedPath = ctx.ResolvedPath;
                capturedServices = ctx.Services;
                return Task.FromResult(RequiredCommandValidationResult.Success());
            });

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));

        Assert.NotNull(capturedPath);
        Assert.NotNull(capturedServices);
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_LogsWarningOnFailedValidationCallback()
    {
        var builder = DistributedApplication.CreateBuilder();
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";

        builder.AddContainer("test", "image")
            .WithRequiredCommand(command, ctx =>
            {
                return Task.FromResult(RequiredCommandValidationResult.Failure("Custom validation failed"));
            });

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Should not throw - just logs a warning and allows the resource to attempt start
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_ValidatesMultipleAnnotations()
    {
        var builder = DistributedApplication.CreateBuilder();
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";

        builder.AddContainer("test", "image")
            .WithRequiredCommand(command)
            .WithRequiredCommand("missing-command-xyz");

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Should not throw - validates all annotations, logs warnings for missing ones
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CoalescesNotificationsForSameCommand()
    {
        var builder = DistributedApplication.CreateBuilder();
        const string missingCommand = "this-command-definitely-does-not-exist-coalesce-test";

        builder.AddContainer("test1", "image").WithRequiredCommand(missingCommand);
        builder.AddContainer("test2", "image").WithRequiredCommand(missingCommand);

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource1 = appModel.Resources.Single(r => r.Name == "test1");
        var resource2 = appModel.Resources.Single(r => r.Name == "test2");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Both should complete without throwing - warnings are logged and cached
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource1, app.Services));
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource2, app.Services));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CachesSuccessfulValidation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var command = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var callbackCount = 0;

        builder.AddContainer("test1", "image")
            .WithRequiredCommand(command, ctx =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.FromResult(RequiredCommandValidationResult.Success());
            });

        builder.AddContainer("test2", "image")
            .WithRequiredCommand(command, ctx =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.FromResult(RequiredCommandValidationResult.Success());
            });

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource1 = appModel.Resources.Single(r => r.Name == "test1");
        var resource2 = appModel.Resources.Single(r => r.Name == "test2");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource1, app.Services));
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource2, app.Services));

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CallsInteractionServiceForMissingCommand()
    {
        var builder = DistributedApplication.CreateBuilder();
        const string missingCommand = "this-command-does-not-exist-interaction-test";

        var testInteractionService = new TestInteractionService { IsAvailable = true };
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        builder.AddContainer("test", "image").WithRequiredCommand(missingCommand, "https://example.com/install");

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // Start publishing in background - it will write to the channel
        var publishTask = eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));

        // Read the notification from the channel
        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Complete the notification so publish can finish
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await publishTask;

        Assert.Equal("Missing command", interaction.Title);
        Assert.Contains(missingCommand, interaction.Message);
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_DoesNotCallInteractionServiceWhenUnavailable()
    {
        var builder = DistributedApplication.CreateBuilder();
        const string missingCommand = "this-command-does-not-exist-unavailable-test";

        var testInteractionService = new TestInteractionService { IsAvailable = false };
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        builder.AddContainer("test", "image").WithRequiredCommand(missingCommand);

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.Single(r => r.Name == "test");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services));

        // Channel should be empty since IsAvailable is false
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RequiredCommandValidationLifecycleHook_CoalescesInteractionServiceCalls()
    {
        var builder = DistributedApplication.CreateBuilder();
        const string missingCommand = "this-command-does-not-exist-coalesce-interaction-test";

        var testInteractionService = new TestInteractionService { IsAvailable = true };
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        builder.AddContainer("test1", "image").WithRequiredCommand(missingCommand);
        builder.AddContainer("test2", "image").WithRequiredCommand(missingCommand);

        await using var app = builder.Build();
        await SubscribeHooksAsync(app);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource1 = appModel.Resources.Single(r => r.Name == "test1");
        var resource2 = appModel.Resources.Single(r => r.Name == "test2");
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        // First publish - will trigger notification
        var publishTask1 = eventing.PublishAsync(new BeforeResourceStartedEvent(resource1, app.Services));
        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await publishTask1;

        // Second publish - should not trigger another notification due to coalescing
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource2, app.Services));

        // Channel should be empty since the second call was coalesced
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));
    }

    /// <summary>
    /// Helper method to subscribe all eventing subscribers (including RequiredCommandValidationLifecycleHook)
    /// to the eventing system. This simulates what happens during app.StartAsync().
    /// </summary>
    private static async Task SubscribeHooksAsync(DistributedApplication app)
    {
        var eventSubscribers = app.Services.GetServices<IDistributedApplicationEventingSubscriber>();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var execContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        foreach (var subscriber in eventSubscribers)
        {
            await subscriber.SubscribeAsync(eventing, execContext, CancellationToken.None);
        }
    }
}
