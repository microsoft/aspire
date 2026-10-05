// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class TerminalsTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialResources_PreservesExplicitOrSavedSelection(bool savedSelection)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = new List<ResourceViewModel>();
        var client = new TestDashboardClient(isEnabled: true, initialResources: resources, whenResourcesReady: ready.Task,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState
                ? (true, new Terminals.TerminalsPageState("shell"))
                : (false, null)
        };
        TerminalsSetupHelpers.SetupPage(this, client, storage);
        var cut = RenderPage(savedSelection ? null : "shell");
        var navigation = Services.GetRequiredService<NavigationManager>();

        Assert.True(client.WhenConnected.IsCompleted);
        Assert.Equal(savedSelection ? "http://localhost/terminals" : "http://localhost/terminals/resource/shell", navigation.Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal(0, client.ResourceSubscriptionCount);

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.LocationChanged += (_, _) => restored.TrySetResult();
        resources.Add(TerminalSetupHelpers.CreateTerminalResource("shell-instance", displayName: "shell"));
        ready.SetResult();
        if (savedSelection)
        {
            await restored.Task.WaitAsync(DefaultWaitTimeout);
            // bUnit doesn't run the Router after restoring a saved selection.
            cut.Render(builder => builder.Add(p => p.ResourceName, "shell"));
        }
        cut.WaitForAssertion(() =>
        {
            var viewer = cut.FindComponent<TerminalView>().Instance;
            Assert.Equal("shell-instance", viewer.ResourceName);
            Assert.Equal("shell", viewer.WindowResourceName);
            Assert.Equal("http://localhost/terminals/resource/shell", navigation.Uri);
        });
    }

    [Fact]
    public async Task InitialResources_EmptySnapshotRedirectsOnlyAfterReadiness()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true, whenResourcesReady: ready.Task,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var redirected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.LocationChanged += (_, _) => redirected.TrySetResult();

        Assert.Equal("http://localhost/terminals/resource/shell", navigation.Uri);
        ready.SetResult();
        await redirected.Task.WaitAsync(DefaultWaitTimeout);
        Assert.Equal("http://localhost/", navigation.Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("resources")]
    [InlineData("subscription")]
    public async Task DisposalDuringInitialization_DoesNotStartWatchOrNavigate(string phase)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true,
            whenResourcesReady: phase == "resources" ? pending.Task : Task.CompletedTask,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>())
        {
            // Model a repository read already in flight that completes after cancellation.
            BeforeResourceSubscriptionAsync = _ => phase == "subscription" ? pending.Task : Task.CompletedTask
        };
        var storage = new TestSessionStorage
        {
            OnGetTaskAsync = async _ =>
            {
                if (phase == "storage")
                {
                    await pending.Task;
                }
                return (false, null);
            }
        };
        TerminalsSetupHelpers.SetupPage(this, client, storage);
        var cut = RenderPage("shell");
        var renderCount = cut.RenderCount;

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        pending.SetResult();
        cut.WaitForState(() => cut.RenderCount > renderCount);
        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(client.ClosedTerminals);
    }

    [Fact]
    public void InitialConnection_DoesNotRedirectBeforeSnapshotLoads()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true, whenConnected: connected.Task,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
        connected.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("shell", cut.FindComponent<TerminalView>().Instance.ResourceName));
    }

    [Theory]
    [InlineData("shell", true)]
    [InlineData("shell", false)]
    [InlineData("shell-gdkfxyqe", true)]
    [InlineData("shell-gdkfxyqe", false)]
    public async Task SingletonRoute_UsesDisplayNameLikeConsoleLogs(string resourceName, bool isDesktop)
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this,
            CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell-gdkfxyqe", displayName: "shell")), storage);
        var cut = RenderPage(resourceName, isDesktop);

        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("shell-gdkfxyqe", cut.Instance.PageViewModel.SelectedResource!.Id!.InstanceId);
        Assert.Equal("shell-gdkfxyqe", cut.FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal("shell", cut.FindComponent<TerminalView>().Instance.WindowResourceName);
        Assert.Equal("shell", cut.Find(".terminal-title").TextContent);
        Assert.Equal("shell", saved?.SelectedResource);
        Assert.Equal("/terminals/resource/shell",
            cut.Instance.GetUrlFromSerializableViewModel(new("shell-gdkfxyqe")));

        await cut.InvokeAsync(() => cut.FindComponent<TerminalView>().Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        Assert.Equal("http://localhost/terminal-window/resource/shell?fontSize=17",
            cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-url"));
        Assert.Equal("resource:shell", cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-key"));
    }

    [Fact]
    public async Task SelectingSingleton_PersistsDisplayNameAndEscapesUrl()
    {
        const string displayName = "shell #1/?%+";
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this,
            CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell-gdkfxyqe", displayName: displayName)), storage);
        var cut = RenderPage(null);
        var selector = cut.FindComponent<ResourceSelect>();

        await cut.InvokeAsync(() => selector.Instance.SelectedResourceChanged.InvokeAsync(selector.Instance.Resources!.Last()));

        Assert.Equal("http://localhost/terminals/resource/shell%20%231%2F%3F%25%2B",
            Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(displayName, saved?.SelectedResource);
        Assert.Equal("shell-gdkfxyqe", cut.FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal(displayName, cut.FindComponent<TerminalView>().Instance.WindowResourceName);
        Assert.Equal(displayName, cut.Find(".terminal-title").TextContent);
        await cut.InvokeAsync(() => cut.FindComponent<TerminalView>().Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        Assert.Equal("http://localhost/terminal-window/resource/shell%20%231%2F%3F%25%2B?fontSize=17",
            cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-url"));
        Assert.Equal($"resource:{displayName}", cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-key"));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("shell-gdkfxyqe")]
    public void SavedSingleton_RestoresDisplayNameOrLegacyInstanceId(string storedResource)
    {
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState
                ? (true, new Terminals.TerminalsPageState(storedResource)) : (false, null)
        };
        TerminalsSetupHelpers.SetupPage(this,
            CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell-gdkfxyqe", displayName: "shell")), storage);
        var cut = RenderPage(null);

        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        // bUnit does not run the Router when restoring the saved route.
        cut.Render(builder => builder.Add(p => p.ResourceName, "shell"));
        Assert.Equal("shell-gdkfxyqe", cut.Instance.PageViewModel.SelectedResource!.Id!.InstanceId);
        Assert.Equal("shell", cut.Instance.ConvertViewModelToSerializable().SelectedResource);
    }

    [Fact]
    public async Task SingletonSharingDisplayNameWithNonTerminal_UsesInstanceIdLikeConsoleLogs()
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("shell-terminal", displayName: "shell"),
            ModelTestHelpers.CreateResource("shell-other", displayName: "shell")));
        var cut = RenderPage("shell-terminal");

        Assert.Equal("http://localhost/terminals/resource/shell-terminal", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("shell-terminal", cut.Instance.ConvertViewModelToSerializable().SelectedResource);
        await cut.InvokeAsync(() => cut.FindComponent<TerminalView>().Instance.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        Assert.Equal("http://localhost/terminal-window/resource/shell-terminal?fontSize=17",
            cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-url"));
        Assert.Equal("resource:shell-terminal", cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-key"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selector_OnlyTerminalResourcesAndIndividualReplicas(bool isDesktop)
    {
        var client = CreateClient(
            ModelTestHelpers.CreateResource("ordinary"),
            TerminalSetupHelpers.CreateTerminalResource("shell-1", displayName: "shell", replicaCount: 2),
            TerminalSetupHelpers.CreateTerminalResource("shell-2", displayName: "shell", replicaIndex: 1, replicaCount: 2, state: KnownResourceState.Waiting),
            TerminalSetupHelpers.CreateTerminalResource("hidden", hidden: true));
        TerminalsSetupHelpers.SetupPage(this, client);
        var dialogProvider = Render<CascadingValue<ViewportInformation>>(builder => builder
            .Add(p => p.Value, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false))
            .AddChildContent<FluentDialogProvider>());
        var cut = RenderPage("shell-2", isDesktop);
        if (!isDesktop)
        {
            cut.Find(".mobile-toolbar").Click();
            dialogProvider.WaitForAssertion(() => Assert.Single(dialogProvider.FindComponents<ResourceSelect>()));
        }
        var selector = isDesktop ? cut.FindComponent<ResourceSelect>().Instance : dialogProvider.FindComponent<ResourceSelect>().Instance;
        Assert.False(selector.CanSelectGrouping);
        Assert.Equal(new string?[] { null, null, "shell-1", "shell-2" }, selector.Resources!.Select(r => r.Id?.InstanceId));
        Assert.Equal(Resources.ControlsStrings.LabelNone, selector.Resources!.First().Name);
        Assert.All(selector.Resources!.Skip(1), r => Assert.NotNull(r.Id));
        var terminal = cut.FindComponent<TerminalView>().Instance;
        Assert.Equal("shell-2", terminal.ResourceName);
        Assert.Equal("shell-2", terminal.WindowResourceName);
        Assert.Equal("shell-2", cut.Find(".terminal-title").TextContent);
        Assert.True(terminal.ShowOpenInWindow);
        Assert.NotNull(terminal.ResourceIcon);
        Assert.Equal("http://localhost/terminals/resource/shell-2", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("shell-2", cut.Instance.ConvertViewModelToSerializable().SelectedResource);
        await cut.InvokeAsync(() => terminal.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        Assert.Equal("http://localhost/terminal-window/resource/shell-2?fontSize=17",
            cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-url"));
        Assert.Equal("resource:shell-2", cut.Find(".terminal-open-window").GetAttribute("data-terminal-window-key"));
        Assert.Empty(cut.FindComponents<LogViewer>());
    }

    [Fact]
    public void TerminalWithoutReplicaMetadata_RemainsSelectableAndConnectsByInstanceName()
    {
        var terminal = TerminalSetupHelpers.CreateTerminalResource("shell-instance", displayName: "shell");
        var resource = ModelTestHelpers.CreateResource("shell-instance", displayName: "shell",
            properties: terminal.Properties.Where(p => p.Key == KnownProperties.Terminal.Enabled).ToDictionary());
        TerminalsSetupHelpers.SetupPage(this, CreateClient(resource));
        var cut = RenderPage("shell");

        Assert.Equal("http://localhost/terminals/resource/shell", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("shell-instance", cut.FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal(new string?[] { null, "shell-instance" },
            cut.FindComponent<ResourceSelect>().Instance.Resources!.Select(r => r.Id?.InstanceId));
        TerminalSetupHelpers.AssertSingleTerminalConnection(this, "ws://localhost/api/terminal?resource=shell-instance");
    }

    [Theory]
    [InlineData(KnownResourceState.Waiting)]
    [InlineData(KnownResourceState.Starting)]
    [InlineData(KnownResourceState.Finished)]
    public void TerminalResources_NotRunning_RemainSelectable(KnownResourceState state)
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell", state: state)));
        var cut = RenderPage("shell");
        Assert.Equal("shell", cut.FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal(new[] { Resources.ControlsStrings.LabelNone, $"shell ({state})" },
            cut.FindComponent<ResourceSelect>().Instance.Resources!.Select(r => r.Name));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void BaseRoute_DefaultsToNoneAndPromptsForResource(bool isDesktop, bool singleResource)
    {
        var resources = singleResource
            ? new[] { TerminalSetupHelpers.CreateTerminalResource("shell") }
            : new[] { TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last") };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(resources));
        var dialogProvider = Render<CascadingValue<ViewportInformation>>(builder => builder
            .Add(p => p.Value, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false))
            .AddChildContent<FluentDialogProvider>());
        var cut = RenderPage(null, isDesktop);

        Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
        Assert.Null(cut.Instance.PageViewModel.SelectedResource.Id);
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal(Resources.TerminalStrings.TerminalsSelectAResource, cut.Find(".aspire-empty-content").TextContent.Trim());
        Assert.Equal("WindowConsole", cut.FindComponent<FluentIcon<Icons.Regular.Size20.WindowConsole>>().Instance.Value.Name);
        if (!isDesktop)
        {
            cut.Find(".aspire-empty-content a").Click();
            dialogProvider.WaitForAssertion(() => Assert.Equal(Resources.ControlsStrings.LabelNone,
                dialogProvider.FindComponent<ResourceSelect>().Instance.SelectedResource!.Name));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionsMenu_OpensAnchoredToToolbarButton(bool isDesktop)
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("shell"),
            TerminalSetupHelpers.CreateTerminalResource("hidden", hidden: true)));
        var dialogProvider = Render<CascadingValue<ViewportInformation>>(builder => builder
            .Add(p => p.Value, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false))
            .AddChildContent<FluentDialogProvider>());
        var cut = RenderPage("shell", isDesktop);
        if (!isDesktop)
        {
            cut.Find(".mobile-toolbar").Click();
            dialogProvider.WaitForAssertion(() => Assert.Single(dialogProvider.FindComponents<ResourceSelect>()));
        }
        var buttons = isDesktop ? cut.FindComponents<AspireMenuButton>() : dialogProvider.FindComponents<AspireMenuButton>();
        var options = buttons.Single(b => b.Instance.Title == Resources.TerminalStrings.TerminalsSettings);
        Assert.Empty(options.FindComponents<AspireMenu>());

        options.Find($"#{options.Instance.MenuButtonId}").Click();

        var menu = options.FindComponent<AspireMenu>().Instance;
        Assert.True(menu.Open);
        Assert.True(menu.Anchored);
        Assert.Equal(options.Instance.MenuButtonId, menu.Anchor);
        Assert.Equal(Resources.ControlsStrings.ShowHiddenResources, Assert.Single(menu.Items).Text);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("last", "last")]
    [InlineData("deleted", null)]
    public void BaseRoute_RestoresSelectionOrDefaultsToNone(string? storedResource, string? expected)
    {
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState && storedResource is not null
                ? (true, new Terminals.TerminalsPageState(storedResource)) : (false, null)
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        var cut = RenderPage(null);
        Assert.Equal($"http://localhost{DashboardUrls.TerminalsUrl(expected)}", Services.GetRequiredService<NavigationManager>().Uri);
        if (expected is not null)
        {
            // bUnit does not run the Router when NavigateTo changes the address.
            cut.Render(builder => builder.Add(p => p.ResourceName, expected));
            Assert.Equal(expected, cut.FindComponent<TerminalView>().Instance.ResourceName);
        }
        else
        {
            Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
            Assert.Empty(cut.FindComponents<TerminalView>());
        }
    }

    [Fact]
    public void ExplicitRoute_TakesPrecedenceOverSavedSelection()
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.TerminalsPageState
                ? (true, new Terminals.TerminalsPageState("first")) : (false, null),
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        Assert.Equal("last", RenderPage("last").FindComponent<TerminalView>().Instance.ResourceName);
        Assert.Equal("last", saved?.SelectedResource);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void UnavailablePage_RedirectsToRoot(bool enabled, bool readOnly, bool hasTerminal)
    {
        var client = new TestDashboardClient(isEnabled: enabled, isReadOnly: readOnly,
            initialResources: hasTerminal ? [TerminalSetupHelpers.CreateTerminalResource("shell")] : [],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Empty(cut.FindComponents<TerminalView>());
    }

    [Fact]
    public async Task HiddenOnlyResources_StayAvailableAndCanBeRevealed()
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource("hidden", hidden: true)));
        var cut = RenderPage(null);
        Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(Resources.ControlsStrings.LabelNone, Assert.Single(cut.FindComponent<ResourceSelect>().Instance.Resources!).Name);
        Assert.Equal(Resources.TerminalStrings.TerminalsSelectAResource, cut.Find(".aspire-empty-content").TextContent.Trim());
        var options = cut.FindComponents<AspireMenuButton>().Single(b => b.Instance.Title == Resources.TerminalStrings.TerminalsSettings);
        var showHidden = Assert.Single(options.Instance.ItemsProvider(), i => i.Text == Resources.ControlsStrings.ShowHiddenResources);
        await cut.InvokeAsync(showHidden.OnClick!);
        var selector = cut.FindComponent<ResourceSelect>();
        Assert.Equal(new string?[] { null, "hidden" }, selector.Instance.Resources!.Select(r => r.Id?.InstanceId));
        Assert.Equal(Resources.ControlsStrings.LabelNone, selector.Instance.SelectedResource!.Name);
        Assert.Empty(cut.FindComponents<TerminalView>());
        await cut.InvokeAsync(() => selector.Instance.SelectedResourceChanged.InvokeAsync(selector.Instance.Resources!.Last()));
        Assert.Equal("hidden", cut.FindComponent<TerminalView>().Instance.ResourceName);
    }

    [Fact]
    public async Task ResourceUpdates_PreserveViewerThenFallbackAndRedirect()
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var first = TerminalSetupHelpers.CreateTerminalResource("first");
        var last = TerminalSetupHelpers.CreateTerminalResource("last");
        var client = new TestDashboardClient(isEnabled: true, initialResources: [first, last], resourceChannelProvider: () => updates);
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("last");
        var viewer = cut.FindComponent<TerminalView>().Instance;
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert,
            TerminalSetupHelpers.CreateTerminalResource("last", state: KnownResourceState.Finished))]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("last (Finished)", cut.Instance.PageViewModel.SelectedResource!.Name);
            Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        });
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Delete, last)]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        });
        var navigation = Services.GetRequiredService<NavigationManager>();
        var redirected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LocationChangedEventArgs> onRedirect = (_, args) => redirected.TrySetResult(args.Location);
        navigation.LocationChanged += onRedirect;
        try
        {
            // Redirecting returns without rendering, so a render-driven wait can miss the navigation.
            await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("first"))]);
            Assert.Equal("http://localhost/", await redirected.Task.WaitAsync(DefaultWaitTimeout));
            Assert.Equal("http://localhost/", navigation.Uri);
        }
        finally
        {
            navigation.LocationChanged -= onRedirect;
        }
    }

    [Fact]
    public async Task SelectingResource_PersistsAndReconnectsExistingViewer()
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        TerminalsSetupHelpers.SetupPage(this, CreateClient(
            TerminalSetupHelpers.CreateTerminalResource("first"), TerminalSetupHelpers.CreateTerminalResource("last")), storage);
        var cut = RenderPage("first");
        var viewer = cut.FindComponent<TerminalView>().Instance;
        var selector = cut.FindComponent<ResourceSelect>();
        await cut.InvokeAsync(() => selector.Instance.SelectedResourceChanged.InvokeAsync(selector.Instance.Resources!.Last()));
        Assert.Equal("last", saved?.SelectedResource);
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        Assert.Equal("last", viewer.ResourceName);
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "reconnectTerminal");
    }

    [Fact]
    public async Task SelectingNone_PersistsAndUnmountsViewerWithoutClosingProducer()
    {
        Terminals.TerminalsPageState? saved = null;
        var storage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.TerminalsPageState)
                {
                    saved = Assert.IsType<Terminals.TerminalsPageState>(value);
                }
            }
        };
        var client = CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell"));
        TerminalsSetupHelpers.SetupPage(this, client, storage);
        var cut = RenderPage("shell");
        var selector = cut.FindComponent<ResourceSelect>();

        await cut.InvokeAsync(() => selector.Instance.SelectedResourceChanged.InvokeAsync(selector.Instance.Resources!.First()));

        Assert.NotNull(saved);
        Assert.Null(saved.SelectedResource);
        Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Empty(client.ClosedTerminals);
    }

    [Fact]
    public async Task ResourceUpdates_PreserveNoneSelection()
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")], resourceChannelProvider: () => updates);
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage(null);

        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, TerminalSetupHelpers.CreateTerminalResource("added"))]);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(new string?[] { null, "added", "shell" },
                cut.FindComponent<ResourceSelect>().Instance.Resources!.Select(r => r.Id?.InstanceId));
            Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        });
    }

    [Fact]
    public void InvalidResourceRoute_DefaultsToNone()
    {
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell")));
        var cut = RenderPage("deleted");

        Assert.Equal("http://localhost/terminals", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(Resources.ControlsStrings.LabelNone, cut.Instance.PageViewModel.SelectedResource!.Name);
        Assert.Empty(cut.FindComponents<TerminalView>());
    }

    [Theory]
    [InlineData("", "shell", "shell", 0)]
    [InlineData("/aspire/nested", "shell", "shell", 0)]
    [InlineData("", "terminal #1/?%+", "terminal%20%231%2F%3F%25%2B", 2)]
    public async Task OpenWindow_CarriesFontAndKeepsInlineViewer(string pathBase, string name, string escapedName, int replicaIndex)
    {
        Services.AddSingleton<NavigationManager>(new TestNavigationManager($"http://localhost{pathBase}/"));
        TerminalsSetupHelpers.SetupPage(this, CreateClient(TerminalSetupHelpers.CreateTerminalResource(name, replicaIndex, replicaIndex + 1)), pathBase: pathBase);
        var cut = RenderPage(name);
        var viewer = cut.FindComponent<TerminalView>().Instance;
        await cut.InvokeAsync(() => viewer.OnTerminalStateChanged(new TerminalToolbarState
        {
            TerminalId = 1, Generation = 1, Connected = true, FontPx = 17
        }));
        var open = cut.Find(".terminal-titlebar .terminal-open-window");
        Assert.Equal($"http://localhost{pathBase}/terminal-window/resource/{escapedName}?fontSize=17",
            open.GetAttribute("data-terminal-window-url"));
        Assert.Equal(Resources.TerminalStrings.TerminalToolbarOpenInWindow, open.GetAttribute("aria-label"));
        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync($"resource:{name}", "opened"));
        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
    }

    [Fact]
    public async Task Disposal_CancelsResourceWatchWithoutClosingProducer()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateClient(TerminalSetupHelpers.CreateTerminalResource("shell"));
        client.OnResourceSubscriptionDisposed = () => disposed.TrySetResult();
        TerminalsSetupHelpers.SetupPage(this, client);
        var cut = RenderPage("shell");
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        await disposed.Task.WaitAsync(DefaultWaitTimeout);
        Assert.Empty(client.ClosedTerminals);
    }

    private static TestDashboardClient CreateClient(params ResourceViewModel[] resources)
        => new(isEnabled: true, initialResources: resources,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());

    private IRenderedComponent<Terminals> RenderPage(string? resourceName, bool isDesktop = true)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(new Uri(new Uri(navigation.BaseUri), DashboardUrls.TerminalsUrl(resourceName).TrimStart('/')).AbsoluteUri);
        return Render<Terminals>(builder => builder.Add(p => p.ResourceName, resourceName).Add(p => p.ViewportInformation, viewport));
    }
}
