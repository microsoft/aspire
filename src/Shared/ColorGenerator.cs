// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire;

internal sealed class AccentColor
{
    internal AccentColor(string variableName)
    {
        VariableName = variableName;
        ReferencedVariableName = $"var({variableName})";
    }

    internal string VariableName { get; }
    internal string ReferencedVariableName { get; }
}

/// <summary>
/// Provides a stable color for a named element. When <see cref="GetColorVariableByKey(string)" />
/// is invoked a new color is returned if the key was not used previously. An instance of this class
/// is thread-safe and multiple threads can query colors concurrently without collisions.
/// The palette of CSS variable names is shared between the dashboard and the CLI so that a given
/// resource name always receives the same color regardless of where it is displayed.
/// </summary>
internal class ColorGenerator
{
    /// <summary>
    /// The ordered list of CSS accent variable names used as the color palette.
    /// </summary>
    internal static readonly string[] s_variableNames =
    [
        "--accent-teal",
        "--accent-marigold",
        "--accent-brass",
        "--accent-peach",
        "--accent-coral",
        "--accent-royal-blue",
        "--accent-orchid",
        "--accent-brand-blue",
        "--accent-seafoam",
        "--accent-mink",
        "--accent-cyan",
        "--accent-gold",
        "--accent-bronze",
        "--accent-orange",
        "--accent-rust",
        "--accent-navy",
        "--accent-berry",
        "--accent-ocean",
        "--accent-jade",
        "--accent-olive"
    ];

    public static readonly ColorGenerator Instance = new ColorGenerator();

    private readonly List<AccentColor> _colors;
    private readonly ConcurrentDictionary<string, Lazy<int>> _colorIndexByKey;
    private int _currentIndex;

    internal ColorGenerator()
    {
        _colors = new List<AccentColor>();
        _colorIndexByKey = new ConcurrentDictionary<string, Lazy<int>>(StringComparer.OrdinalIgnoreCase);
        _currentIndex = 0;

        foreach (var name in s_variableNames)
        {
            _colors.Add(new AccentColor(name));
        }
    }

    internal int GetColorIndex(string key)
    {
        return _colorIndexByKey.GetOrAdd(key, k =>
        {
            // GetOrAdd is run outside of the lock.
            // Use lazy to ensure that the index is only calculated once for an app.
            return new Lazy<int>(() =>
            {
                var i = _currentIndex;
                _currentIndex = ++_currentIndex % _colors.Count;
                return i;
            });
        }).Value;
    }

    public string GetColorVariableByKey(string key)
    {
        var i = GetColorIndex(key);
        return _colors[i].ReferencedVariableName;
    }

    /// <summary>
    /// Pre-resolves colors for all provided keys in sorted order so that
    /// color assignment is deterministic regardless of encounter order.
    /// </summary>
    public void ResolveAll(IEnumerable<string> keys)
    {
        foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            GetColorIndex(key);
        }
    }

    public void Clear()
    {
        _colorIndexByKey.Clear();
        _currentIndex = 0;
    }
}
