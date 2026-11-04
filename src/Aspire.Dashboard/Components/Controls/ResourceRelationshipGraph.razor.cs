// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

public sealed partial class ResourceRelationshipGraph : ComponentBase, IAsyncDisposable
{
    private readonly string _markerId = $"relationship-arrow-{Guid.NewGuid():N}";
    private GraphLayout _layout = new(0, 0, [], []);
    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ResourceRelationshipGraph>? _reference;
    private Task? _initialization;
    private bool _disposed;
    private bool _centerAfterRender;
    private int _availableWidth;
    private int _revision;
    private string _mode = "Flow";
    private string _renderedMode = "Flow";

    [Parameter, EditorRequired]
    public required RelationshipGraphNode Resource { get; set; }

    [Parameter, EditorRequired]
    public required IReadOnlyList<RelationshipGraphNode> Incoming { get; set; }

    [Parameter, EditorRequired]
    public required IReadOnlyList<RelationshipGraphNode> Outgoing { get; set; }

    [Parameter]
    public RenderFragment? HeaderContent { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ILogger<ResourceRelationshipGraph> Logger { get; init; }

    private IReadOnlyList<IconToggleOption<string>> LayoutOptions =>
    [
        new("Flow", new Icons.Regular.Size20.ArrowRight(), Loc[nameof(Resources.Resources.RelationshipFlow)]),
        new("Hierarchy", new Icons.Regular.Size20.BranchFork(), Loc[nameof(Resources.Resources.RelationshipHierarchy)]),
        new("Topology", new Icons.Regular.Size20.ShareAndroid(), Loc[nameof(Resources.Resources.RelationshipTopology)])
    ];

    protected override Task OnParametersSetAsync() => UpdateLayoutAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _initialization = InitializeAsync();
            await _initialization;
        }
        if (_centerAfterRender && _module is not null)
        {
            _centerAfterRender = false;
            await _module.InvokeVoidAsync("center", _container);
        }
    }

    private async Task InitializeAsync()
    {
        _module = await JS.InvokeAsync<IJSObjectReference>("import", $"./{Assets["Components/Controls/ResourceRelationshipGraph.razor.js"]}");
        if (!_disposed)
        {
            _reference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("observe", _container, _reference);
        }
    }

    [JSInvokable]
    public Task ResizeAsync(int width) => InvokeAsync(async () =>
    {
        if (!_disposed && width > 0 && _availableWidth != width)
        {
            _availableWidth = width;
            await UpdateLayoutAsync();
            StateHasChanged();
        }
    });

    private async Task ChangeLayoutAsync(string value)
    {
        _mode = value;
        await UpdateLayoutAsync();
    }

    private async Task UpdateLayoutAsync()
    {
        if (_module is null || _availableWidth == 0 || _disposed)
        {
            return;
        }
        var revision = ++_revision;
        try
        {
            await using var reference = await _module.InvokeAsync<IJSStreamReference>("layout", _container, _mode, _availableWidth, Resource, Incoming, Outgoing);
            await using var stream = await reference.OpenReadStreamAsync();
            var layout = await JsonSerializer.DeserializeAsync(stream, GraphJsonContext.Default.GraphLayout)
                ?? throw new InvalidOperationException("The relationship graph layout was empty.");
            if (!_disposed && revision == _revision)
            {
                _layout = layout;
                _renderedMode = _mode;
                _centerAfterRender = true;
            }
        }
        catch (JSException exception)
        {
            Logger.LogError(exception, "Could not lay out the relationship graph.");
            throw;
        }
    }

    private static string EdgeClass(string type) => type switch
    {
        "Parent" => "parent",
        "Reference" => "reference",
        _ => "other"
    };

    private string MarkerId(string type) => $"{_markerId}-{_layout.Edges.FindIndex(edge => edge.Type == type)}";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_initialization is not null)
            {
                await _initialization;
            }
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("disconnect", _container);
            }
        }
        catch (JSDisconnectedException)
        {
            // Navigation can disconnect the circuit before the observer is released.
        }
        finally
        {
            _reference?.Dispose();
            if (_module is not null)
            {
                await JSInteropHelpers.SafeDisposeAsync(_module);
            }
        }
    }

    private sealed record GraphLayout(int Width, int Height, List<NodeLayout> Nodes, List<EdgeLayout> Edges);
    private sealed record NodeLayout(RelationshipGraphNode Node, int X, int Y, int Width, int Height, bool IsCenter, List<string> Lines);
    private sealed record EdgeLayout(string Id, string Path, string Type, string Source, string Target);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(GraphLayout))]
    private sealed partial class GraphJsonContext : JsonSerializerContext;
}

public sealed record RelationshipGraphNode(string Name, string Type, string? Url);
