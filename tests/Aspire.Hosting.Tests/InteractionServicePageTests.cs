// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[Trait("Partition", "2")]
public class InteractionServicePageTests
{
    [Fact]
    public void RegisterPage_StartPageInteraction_AddsInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterPage("my-page", new ContentPageOptions
        {
            Title = "My Page",
            OnVisit = _ => Task.CompletedTask
        });

        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.Equal("My Page", interaction.Title);
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("my-page", pageInfo.Route);
        Assert.Equal("My Page", pageInfo.PageOptions.Title);

        registration.Dispose();
    }

    [Fact]
    public void RegisterPage_Dispose_RemovesRegistrationAndActiveInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var registration = interactionService.RegisterPage("my-page", new ContentPageOptions
        {
            Title = "My Page"
        });
        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);
        Assert.Single(interactionService.GetCurrentInteractions());

        // Act
        registration.Dispose();

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());
        Assert.Null(interactionService.StartPageInteraction("my-page", "session-2", new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public void RegisterPage_NullRoute_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterPage(null!, new ContentPageOptions()));
    }

    [Fact]
    public void RegisterPage_NullContext_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterPage("route", null!));
    }

    [Fact]
    public void RegisterPage_WithoutTitle_UseRouteAsTitle()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterPage("my-route", new ContentPageOptions());
        var startedPage = interactionService.StartPageInteraction("my-route", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.NotNull(startedPage);
        Assert.Equal("my-route", interaction.Title);

        registration.Dispose();
    }

    [Fact]
    public async Task StartPageInteraction_InvokesOnVisitCallback()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var visitCalled = new TaskCompletionSource<PageVisitContext>();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Title = "Test",
            OnVisit = ctx =>
            {
                visitCalled.TrySetResult(ctx);
                return Task.CompletedTask;
            }
        });

        var queryParams = new Dictionary<string, string> { ["key"] = "value" };

        // Act
        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", queryParams, CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        var context = await visitCalled.Task.DefaultTimeout();
        Assert.Equal("session-1", context.SessionId);
        Assert.Equal("value", context.QueryParameters["key"]);
    }

    [Fact]
    public void StartPageInteraction_UnknownRoute_ReturnsNull()
    {
        var interactionService = CreateInteractionService();

        var result = interactionService.StartPageInteraction("missing-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task StartPageInteraction_SendMarkdown_StoresContent()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        Func<string, CancellationToken, Task>? capturedRender = null;
        var markdownSent = new TaskCompletionSource();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                capturedRender = ctx.RenderAsync;
                await ctx.RenderAsync("# Hello", ctx.CancellationToken);
                markdownSent.SetResult();
            }
        });

        // Act
        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        await markdownSent.Task.DefaultTimeout();
        Assert.NotNull(capturedRender);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = (Interaction.PageInteractionInfo)interaction.InteractionInfo;
        Assert.Equal("# Hello", pageInfo.Content);
    }

    [Fact]
    public async Task ProcessInteractionFromClientAsync_CompletesPageInteraction_CancelsVisitorToken()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var visitCallbackReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitTokenCancelled = new TaskCompletionSource<bool>();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                ctx.CancellationToken.Register(() => visitTokenCancelled.TrySetResult(true));
                visitCallbackReady.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        await visitCallbackReady.Task.DefaultTimeout();

        // Act
        await interactionService.ProcessInteractionFromClientAsync(
            startedPage.InteractionId,
            (_, _, _) => new InteractionCompletionState { Complete = true },
            CancellationToken.None);

        // Assert
        Assert.True(await visitTokenCancelled.Task.DefaultTimeout());
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task ProcessPageActionFromClientAsync_InvokesRegisteredActionWithContext()
    {
        var interactionService = CreateInteractionService();
        var actionCalled = new TaskCompletionSource<ActionContext>();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Actions = new Dictionary<string, Func<ActionContext, Task>>
            {
                ["save"] = context =>
                {
                    actionCalled.SetResult(context);
                    return Task.CompletedTask;
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        await interactionService.ProcessPageActionFromClientAsync(
            startedPage.InteractionId,
            "save",
            new Dictionary<string, string> { ["id"] = "42", ["name"] = "test" },
            CancellationToken.None);

        var context = await actionCalled.Task.DefaultTimeout();
        Assert.Equal("session-1", context.SessionId);
        Assert.Equal("42", context.Arguments["id"]);
        Assert.Equal("test", context.Arguments["name"]);
        Assert.False(context.CancellationToken.IsCancellationRequested);
        Assert.Single(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task ProcessPageActionFromClientAsync_MissingAction_DoesNotCompletePageInteraction()
    {
        var interactionService = CreateInteractionService();
        var called = false;

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Actions = new Dictionary<string, Func<ActionContext, Task>>
            {
                ["save"] = _ =>
                {
                    called = true;
                    return Task.CompletedTask;
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        await interactionService.ProcessPageActionFromClientAsync(
            startedPage.InteractionId,
            "missing",
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(called);
        Assert.Single(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task ProcessPageActionFromClientAsync_PageCompletion_CancelsActionToken()
    {
        var interactionService = CreateInteractionService();
        var actionStarted = new TaskCompletionSource();
        var actionCancelled = new TaskCompletionSource();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Actions = new Dictionary<string, Func<ActionContext, Task>>
            {
                ["wait"] = async context =>
                {
                    actionStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, context.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        actionCancelled.SetResult();
                        throw;
                    }
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        var actionTask = interactionService.ProcessPageActionFromClientAsync(
            startedPage.InteractionId,
            "wait",
            new Dictionary<string, string>(),
            CancellationToken.None);

        await actionStarted.Task.DefaultTimeout();

        await interactionService.ProcessInteractionFromClientAsync(
            startedPage.InteractionId,
            (_, _, _) => new InteractionCompletionState { Complete = true },
            CancellationToken.None);

        await actionCancelled.Task.DefaultTimeout();
        await actionTask.DefaultTimeout();
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task ProcessPageActionFromClientAsync_MultipleSessions_UsesInteractionSession()
    {
        var interactionService = CreateInteractionService();
        var sessions = new List<string>();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Actions = new Dictionary<string, Func<ActionContext, Task>>
            {
                ["record"] = context =>
                {
                    sessions.Add(context.SessionId);
                    return Task.CompletedTask;
                }
            }
        });

        var startedPage1 = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        var startedPage2 = interactionService.StartPageInteraction("test-page", "session-2", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage1);
        Assert.NotNull(startedPage2);

        await interactionService.ProcessPageActionFromClientAsync(startedPage1.InteractionId, "record", new Dictionary<string, string>(), CancellationToken.None);
        await interactionService.ProcessPageActionFromClientAsync(startedPage2.InteractionId, "record", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(["session-1", "session-2"], sessions);
        Assert.Equal(2, interactionService.GetCurrentInteractions().Count);
    }

    [Fact]
    public void RegisterMenuButton_AddsInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Go Home",
            Url = "/pages/home"
        });

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.Equal("Go Home", interaction.Title);
        var menuInfo = Assert.IsType<Interaction.MenuButtonInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("Home", menuInfo.Options.IconName);
        Assert.Equal("Go Home", menuInfo.Options.Text);
        Assert.Equal("/pages/home", menuInfo.Options.Url);

        registration.Dispose();
    }

    [Fact]
    public void RegisterMenuButton_Dispose_RemovesInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var registration = interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Go Home",
            Url = "/pages/home"
        });
        Assert.Single(interactionService.GetCurrentInteractions());

        // Act
        registration.Dispose();

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public void RegisterMenuButton_NullOptions_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterMenuButton(null!));
    }

    [Fact]
    public void RegisterPage_DashboardDisabled_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterPage("route", new ContentPageOptions()));
    }

    [Fact]
    public void RegisterMenuButton_DashboardDisabled_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Home",
            Tooltip = "Home",
            Url = "/pages/home"
        }));
    }

    [Fact]
    public void RegisterPage_MultiplePages_AllTracked()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var reg1 = interactionService.RegisterPage("page-1", new ContentPageOptions { Title = "Page 1" });
        var reg2 = interactionService.RegisterPage("page-2", new ContentPageOptions { Title = "Page 2" });

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());

        var page1 = interactionService.StartPageInteraction("page-1", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        var page2 = interactionService.StartPageInteraction("page-2", "session-2", new Dictionary<string, string>(), CancellationToken.None);

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.Equal(2, interactionService.GetCurrentInteractions().Count);

        reg1.Dispose();
        Assert.Single(interactionService.GetCurrentInteractions());

        reg2.Dispose();
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public void RegisterPage_DuplicateRoute_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        interactionService.RegisterPage("my-page", new ContentPageOptions { Title = "First" });

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            interactionService.RegisterPage("my-page", new ContentPageOptions { Title = "Second" }));
        Assert.Contains("my-page", ex.Message);
    }

    [Fact]
    public void RegisterPage_DuplicateRoute_CaseInsensitive_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        interactionService.RegisterPage("My-Page", new ContentPageOptions { Title = "First" });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            interactionService.RegisterPage("my-page", new ContentPageOptions { Title = "Second" }));
    }

    [Fact]
    public void RegisterPage_SameRouteAfterDispose_Succeeds()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var reg = interactionService.RegisterPage("my-page", new ContentPageOptions { Title = "First" });
        reg.Dispose();

        // Act — should not throw since the first registration was disposed.
        var reg2 = interactionService.RegisterPage("my-page", new ContentPageOptions { Title = "Second" });
        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.NotNull(startedPage);
        Assert.Equal("Second", interaction.Title);
        reg2.Dispose();
    }

    [Fact]
    public async Task RegisterAsset_OnGet_WritesToStream()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        using var registration = interactionService.RegisterAsset("scripts/app.js", "application/javascript", new AssetContext
        {
            OnGet = async context =>
            {
                await context.WriteAsync("console.log('hello');"u8.ToArray());
            }
        });

        var chunks = new List<byte>();

        // Act
        var found = await interactionService.WriteAssetAsync("scripts/app.js", chunk =>
        {
            chunks.AddRange(chunk.ToArray());
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("console.log('hello');", System.Text.Encoding.UTF8.GetString(chunks.ToArray()));
    }

    [Fact]
    public async Task RegisterAsset_ByteArrayOverload_UsesRegisteredContent()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var content = "body { color: red; }"u8.ToArray();
        using var registration = interactionService.RegisterAsset("styles/site.css", "text/css", content);

        var chunks = new List<byte>();

        // Act
        var found = await interactionService.WriteAssetAsync("styles/site.css", chunk =>
        {
            chunks.AddRange(chunk.ToArray());
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("body { color: red; }", System.Text.Encoding.UTF8.GetString(chunks.ToArray()));
    }

    [Fact]
    public void RegisterAsset_DuplicateRoute_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        using var registration = interactionService.RegisterAsset("logo.svg", "image/svg+xml", ReadOnlyMemory<byte>.Empty);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterAsset("logo.svg", "image/svg+xml", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task RegisterAsset_SameRouteAfterDispose_Succeeds()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var reg1 = interactionService.RegisterAsset("downloads/file.txt", "text/plain", "first"u8.ToArray());
        reg1.Dispose();

        // Act
        using var reg2 = interactionService.RegisterAsset("downloads/file.txt", "text/plain", "second"u8.ToArray());
        var chunks = new List<byte>();
        var found = await interactionService.WriteAssetAsync("downloads/file.txt", chunk =>
        {
            chunks.AddRange(chunk.ToArray());
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(chunks.ToArray()));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("assets/../secret")]
    [InlineData("..")]
    [InlineData("foo/../../bar")]
    public void RegisterAsset_PathTraversal_ThrowsArgumentException(string route)
    {
        var interactionService = CreateInteractionService();

        var ex = Assert.Throws<ArgumentException>(() => interactionService.RegisterAsset(route, "text/plain", ReadOnlyMemory<byte>.Empty));
        Assert.Contains("..", ex.Message);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("assets/../secret")]
    public async Task WriteAssetAsync_PathTraversal_ReturnsFalse(string route)
    {
        var interactionService = CreateInteractionService();

        var found = await interactionService.WriteAssetAsync(route, _ => Task.CompletedTask, CancellationToken.None);

        Assert.False(found);
    }

    [Fact]
    public async Task StartPageInteraction_MultipleSessionsSendMarkdown_AllSessionsStored()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var session1Ready = new TaskCompletionSource();
        var session2Ready = new TaskCompletionSource();
        var session1MarkdownSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session2MarkdownSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSignal = new TaskCompletionSource();

        interactionService.RegisterPage("test-page", new ContentPageOptions
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                if (ctx.SessionId == "session-1")
                {
                    session1Ready.SetResult();
                }
                else
                {
                    session2Ready.SetResult();
                }

                await continueSignal.Task;
                await ctx.RenderAsync($"# Content from {ctx.SessionId}", ctx.CancellationToken);
                if (ctx.SessionId == "session-1")
                {
                    session1MarkdownSent.SetResult();
                }
                else
                {
                    session2MarkdownSent.SetResult();
                }
            }
        });

        // Act — start two visits concurrently (fire-and-forget, like the real code does).
        var startedPage1 = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        var startedPage2 = interactionService.StartPageInteraction("test-page", "session-2", new Dictionary<string, string>(), CancellationToken.None);

        // Wait for both callbacks to start.
        Assert.NotNull(startedPage1);
        Assert.NotNull(startedPage2);
        await session1Ready.Task.DefaultTimeout();
        await session2Ready.Task.DefaultTimeout();

        // Let both sessions send markdown concurrently.
        continueSignal.SetResult();

        await session1MarkdownSent.Task.DefaultTimeout();
        await session2MarkdownSent.Task.DefaultTimeout();

        // Assert — both sessions should have their content stored.
        var interactions = interactionService.GetCurrentInteractions();
        var pageInfo1 = Assert.IsType<Interaction.PageInteractionInfo>(interactions.Single(i => i.InteractionId == startedPage1.InteractionId).InteractionInfo);
        var pageInfo2 = Assert.IsType<Interaction.PageInteractionInfo>(interactions.Single(i => i.InteractionId == startedPage2.InteractionId).InteractionInfo);
        Assert.Equal("# Content from session-1", pageInfo1.Content);
        Assert.Equal("# Content from session-2", pageInfo2.Content);
    }

    [Fact]
    public async Task StartPageInteraction_ConcurrentSendMarkdown_DoesNotCorruptState()
    {
        // Arrange — stress test to verify the lock protects state under concurrent writes.
        var interactionService = CreateInteractionService();
        const int writesPerSession = 50;
        var visitReady = new TaskCompletionSource<PageVisitContext>();

        interactionService.RegisterPage("stress-page", new ContentPageOptions
        {
            Title = "Stress",
            OnVisit = async ctx =>
            {
                visitReady.SetResult(ctx);

                // Keep the visit alive until cancelled.
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("stress-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);
        var ctx = await visitReady.Task.DefaultTimeout();

        // Act — concurrent updates for the same active session should not corrupt state.
        var tasks = new List<Task>();
        for (var i = 0; i < writesPerSession; i++)
        {
            var update = i;
            tasks.Add(Task.Run(async () =>
            {
                await ctx.RenderAsync($"Update {update}", ctx.CancellationToken);
            }));
        }

        await Task.WhenAll(tasks).DefaultTimeout();

        // Assert — no exception was thrown and the session has one of the updates.
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.StartsWith("Update ", pageInfo.Content);
    }

    [Fact]
    public async Task StartPageInteraction_IFrameWithStaticUrl_SetsIframeUrl()
    {
        var interactionService = CreateInteractionService();
        var iframeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = interactionService.SubscribeInteractionUpdates();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                var info = interaction.InteractionInfo as Interaction.PageInteractionInfo;
                if (info?.IframeUrl is not null)
                {
                    iframeReady.TrySetResult();
                    break;
                }
            }
        });

        interactionService.RegisterPage("iframe-page", new IFramePageOptions
        {
            Title = "My IFrame",
            IFrameUrl = "http://localhost:5000"
        });

        var startedPage = interactionService.StartPageInteraction("iframe-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        await iframeReady.Task.DefaultTimeout();

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("http://localhost:5000", pageInfo.IframeUrl);
        Assert.True(pageInfo.IframePersistent);
    }

    [Fact]
    public async Task StartPageInteraction_IFrameWithStaticUrl_NonPersistent_SetsIframePersistentFalse()
    {
        var interactionService = CreateInteractionService();
        var iframeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = interactionService.SubscribeInteractionUpdates();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                var info = interaction.InteractionInfo as Interaction.PageInteractionInfo;
                if (info?.IframeUrl is not null)
                {
                    iframeReady.TrySetResult();
                    break;
                }
            }
        });

        interactionService.RegisterPage("iframe-page", new IFramePageOptions
        {
            Title = "My IFrame",
            IFrameUrl = "http://localhost:5000",
            Persistent = false
        });

        var startedPage = interactionService.StartPageInteraction("iframe-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        await iframeReady.Task.DefaultTimeout();

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("http://localhost:5000", pageInfo.IframeUrl);
        Assert.False(pageInfo.IframePersistent);
    }

    [Fact]
    public async Task StartPageInteraction_IFrameWithEndpoint_WaitsForHealthThenSetsIframeUrl()
    {
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var serviceProvider = new TestServiceProvider()
            .AddService(resourceNotificationService);

        var interactionService = CreateInteractionService(serviceProvider: serviceProvider);

        var resource = new TestResourceWithEndpoints("my-resource");
        var endpointAnnotation = new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var endpoint = new EndpointReference(resource, endpointAnnotation);

        var waitingContentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iframeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = interactionService.SubscribeInteractionUpdates();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                var info = interaction.InteractionInfo as Interaction.PageInteractionInfo;
                if (info is null)
                {
                    continue;
                }

                if (info.IframeUrl is not null)
                {
                    iframeReady.TrySetResult();
                    break;
                }
                else if (info.IsWaitingForEndpoint)
                {
                    waitingContentReady.TrySetResult();
                }
            }
        });

        interactionService.RegisterPage("iframe-page", new IFramePageOptions
        {
            Title = "My IFrame",
            IFrameEndpoint = endpoint
        });

        var startedPage = interactionService.StartPageInteraction("iframe-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        // Before the resource is healthy, the iframe URL should not be set yet.
        await waitingContentReady.Task.DefaultTimeout();
        var interactionBeforeHealthy = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfoBeforeHealthy = Assert.IsType<Interaction.PageInteractionInfo>(interactionBeforeHealthy.InteractionInfo);
        Assert.Null(pageInfoBeforeHealthy.IframeUrl);
        Assert.True(pageInfoBeforeHealthy.IsWaitingForEndpoint);

        // Publish the resource as running + healthy.
        await resourceNotificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            ResourceType = "Container",
            Properties = [],
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        });

        await iframeReady.Task.DefaultTimeout();

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("http://localhost:8080", pageInfo.IframeUrl);
        Assert.True(pageInfo.IframePersistent);
    }

    [Fact]
    public async Task StartPageInteraction_IFrameWithEndpoint_ResourceBecomesUnhealthy_RemovesIframeAndWaitsAgain()
    {
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var serviceProvider = new TestServiceProvider()
            .AddService(resourceNotificationService);

        var interactionService = CreateInteractionService(serviceProvider: serviceProvider);

        var resource = new TestResourceWithEndpoints("my-resource");
        var endpointAnnotation = new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var endpoint = new EndpointReference(resource, endpointAnnotation);

        var updateCount = 0;
        var iframeSetSecondTime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iframeSetFirstTime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iframeRemovedAfterUnhealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = interactionService.SubscribeInteractionUpdates();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                var info = interaction.InteractionInfo as Interaction.PageInteractionInfo;
                if (info is null)
                {
                    continue;
                }

                if (info.IframeUrl is not null)
                {
                    updateCount++;
                    if (updateCount == 1)
                    {
                        iframeSetFirstTime.TrySetResult();
                    }
                    else if (updateCount == 2)
                    {
                        iframeSetSecondTime.TrySetResult();
                        break;
                    }
                }
                else if (updateCount == 1 && info.IsWaitingForEndpoint)
                {
                    iframeRemovedAfterUnhealthy.TrySetResult();
                }
            }
        });

        interactionService.RegisterPage("iframe-page", new IFramePageOptions
        {
            Title = "My IFrame",
            IFrameEndpoint = endpoint
        });

        var startedPage = interactionService.StartPageInteraction("iframe-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        // Make the resource healthy.
        await resourceNotificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            ResourceType = "Container",
            Properties = [],
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        });

        await iframeSetFirstTime.Task.DefaultTimeout();

        // Make the resource unhealthy — should trigger re-waiting.
        await resourceNotificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Starting
        });

        await iframeRemovedAfterUnhealthy.Task.DefaultTimeout();

        // Verify the iframe URL was cleared and is waiting for the endpoint.
        var interactionWhileWaiting = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfoWhileWaiting = Assert.IsType<Interaction.PageInteractionInfo>(interactionWhileWaiting.InteractionInfo);
        Assert.Null(pageInfoWhileWaiting.IframeUrl);
        Assert.True(pageInfoWhileWaiting.IsWaitingForEndpoint);

        // Make the resource healthy again.
        await resourceNotificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        });

        await iframeSetSecondTime.Task.DefaultTimeout();

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("http://localhost:8080", pageInfo.IframeUrl);
    }

    [Fact]
    public async Task StartPageInteraction_IFrameWithEndpoint_CancellationStopsMonitoring()
    {
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var serviceProvider = new TestServiceProvider()
            .AddService(resourceNotificationService);

        var interactionService = CreateInteractionService(serviceProvider: serviceProvider);

        var resource = new TestResourceWithEndpoints("my-resource");
        var endpointAnnotation = new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http");
        resource.Annotations.Add(endpointAnnotation);
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 8080);

        var endpoint = new EndpointReference(resource, endpointAnnotation);

        var waitingContentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = interactionService.SubscribeInteractionUpdates();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                var info = interaction.InteractionInfo as Interaction.PageInteractionInfo;
                if (info is { IsWaitingForEndpoint: true })
                {
                    waitingContentReady.TrySetResult();
                    break;
                }
            }
        });

        interactionService.RegisterPage("iframe-page", new IFramePageOptions
        {
            Title = "My IFrame",
            IFrameEndpoint = endpoint
        });

        var startedPage = interactionService.StartPageInteraction("iframe-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        // Verify the interaction is waiting for the resource.
        await waitingContentReady.Task.DefaultTimeout();
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.True(pageInfo.IsWaitingForEndpoint);

        // Complete the page interaction (simulates visitor leaving).
        await interactionService.ProcessInteractionFromClientAsync(
            startedPage.InteractionId,
            (_, _, _) => new InteractionCompletionState { Complete = true },
            CancellationToken.None);

        // The interaction should be removed after completion.
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    private static InteractionService CreateInteractionService(DistributedApplicationOptions? options = null, IServiceProvider? serviceProvider = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new InteractionService(
            NullLogger<InteractionService>.Instance,
            options ?? new DistributedApplicationOptions(),
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            configuration);
    }

    private sealed class TestResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints
    {
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
