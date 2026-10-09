// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Draws a small graph of a resource's direct relationships: resources that reference it on the left, the resource in
/// the middle, and the resources it references on the right.
/// </summary>
/// <remarks>
/// The graph is rendered on the server as inline SVG. Coordinates are integers so attribute values don't depend on
/// the current culture.
/// </remarks>
public sealed partial class ResourceRelationshipGraph : ComponentBase
{
    // A graph with both sides is sized to fit an overview card without being scaled down. Nodes are wider when only
    // one side has relationships, so the graph fills about the same width.
    private const int ThreeColumnNodeWidth = 124;
    private const int TwoColumnNodeWidth = 170;
    private const int NodeHeight = 30;
    private const int RowGap = 10;
    private const int ThreeColumnGap = 28;
    private const int TwoColumnGap = 44;
    private const int Padding = 2;

    // At 12px, text fits in a node with about one character per this many pixels, after the color dot.
    private const int PixelsPerCharacter = 7;
    private const int LabelStart = 22;

    private GraphLayout _layout = null!;
    private int _nodeWidth;
    private int _maximumLabelLength;

    [Parameter, EditorRequired]
    public required RelationshipGraphNode Resource { get; set; }

    /// <summary>
    /// Gets or sets the resources that reference <see cref="Resource"/>.
    /// </summary>
    [Parameter, EditorRequired]
    public required IReadOnlyList<RelationshipGraphNode> Incoming { get; set; }

    /// <summary>
    /// Gets or sets the resources that <see cref="Resource"/> references.
    /// </summary>
    [Parameter, EditorRequired]
    public required IReadOnlyList<RelationshipGraphNode> Outgoing { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    protected override void OnParametersSet()
    {
        // Every relationship is drawn so each related resource can be reached from the graph.
        var incoming = Incoming;
        var outgoing = Outgoing;
        var rows = Math.Max(1, Math.Max(incoming.Count, outgoing.Count));
        var height = (rows * NodeHeight) + ((rows - 1) * RowGap) + (Padding * 2);

        // A side without relationships doesn't take space, so the graph is as large as possible in narrow cards.
        var columnCount = 1 + (incoming.Count > 0 ? 1 : 0) + (outgoing.Count > 0 ? 1 : 0);
        _nodeWidth = columnCount == 3 ? ThreeColumnNodeWidth : TwoColumnNodeWidth;
        var columnGap = columnCount == 3 ? ThreeColumnGap : TwoColumnGap;
        _maximumLabelLength = (_nodeWidth - LabelStart - 8) / PixelsPerCharacter;
        var centerX = incoming.Count > 0 ? Padding + _nodeWidth + columnGap : Padding;
        var width = (columnCount * _nodeWidth) + ((columnCount - 1) * columnGap) + (Padding * 2);

        var center = new NodeLayout(Resource, centerX, (height - NodeHeight) / 2, IsCenter: true);
        var left = LayoutColumn(incoming, Padding, height);
        var right = LayoutColumn(outgoing, centerX + _nodeWidth + columnGap, height);

        var edges = new List<EdgeLayout>();
        foreach (var node in left)
        {
            edges.Add(CreateEdge(node.X + _nodeWidth, node.Y + NodeHeight / 2, center.X, center.Y + NodeHeight / 2, node));
        }
        foreach (var node in right)
        {
            edges.Add(CreateEdge(center.X + _nodeWidth, center.Y + NodeHeight / 2, node.X, node.Y + NodeHeight / 2, node));
        }

        _layout = new GraphLayout(width, height, [.. left, center, .. right], edges);
    }

    private static List<NodeLayout> LayoutColumn(IReadOnlyList<RelationshipGraphNode> nodes, int x, int height)
    {
        var columnHeight = (nodes.Count * NodeHeight) + (Math.Max(0, nodes.Count - 1) * RowGap);
        var y = (height - columnHeight) / 2;
        var result = new List<NodeLayout>(nodes.Count);
        foreach (var node in nodes)
        {
            result.Add(new NodeLayout(node, x, y, IsCenter: false));
            y += NodeHeight + RowGap;
        }

        return result;
    }

    private static EdgeLayout CreateEdge(int x1, int y1, int x2, int y2, NodeLayout node)
    {
        // A horizontal S-curve between the facing sides of the two nodes. The end stops short of the target so the
        // arrow head isn't drawn under the node's border.
        var targetX = x2 - 3;
        var controlOffset = (targetX - x1) / 2;
        var path = string.Create(CultureInfo.InvariantCulture, $"M{x1},{y1} C{x1 + controlOffset},{y1} {targetX - controlOffset},{y2} {targetX},{y2}");
        return new EdgeLayout(path, node.Node.Type);
    }

    /// <summary>
    /// Shortens a long name by replacing its middle with an ellipsis. Keeping the end distinguishes names that only
    /// differ by a suffix, such as replicas: <c>catalogservice-kpjvzdsd</c> becomes <c>catalog…kpjvzdsd</c>.
    /// </summary>
    private string GetLabel(string name)
    {
        if (name.Length <= _maximumLabelLength)
        {
            return name;
        }

        var endLength = (_maximumLabelLength - 1) / 2;
        var startLength = _maximumLabelLength - 1 - endLength;
        return string.Concat(name.AsSpan(0, startLength), "…", name.AsSpan(name.Length - endLength));
    }

    private sealed record GraphLayout(int Width, int Height, List<NodeLayout> Nodes, List<EdgeLayout> Edges);

    private sealed record NodeLayout(RelationshipGraphNode Node, int X, int Y, bool IsCenter);

    private sealed record EdgeLayout(string Path, string Type);
}

/// <summary>
/// A resource in a <see cref="ResourceRelationshipGraph"/>.
/// </summary>
/// <param name="Name">The resource name displayed in the graph.</param>
/// <param name="Type">The relationship type, such as <c>Reference</c>, or the resource type for the center resource.</param>
/// <param name="Url">The URL the node links to, or <c>null</c> for the center resource.</param>
public sealed record RelationshipGraphNode(string Name, string Type, string? Url);
