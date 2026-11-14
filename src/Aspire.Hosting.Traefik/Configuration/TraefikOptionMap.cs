// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A tree of Traefik dynamic configuration options, such as the options of a middleware.
/// </summary>
/// <remarks>
/// <para>
/// Options are set by dotted paths, which is also how the Docker provider spells them in labels. For a
/// <c>headers</c> middleware:
/// <code>
/// customRequestHeaders.X-Script-Name = test
/// accessControlAllowMethods          = [GET, OPTIONS]
/// stsSeconds                         = 31536000
/// </code>
/// </para>
/// <para>
/// A value is a <see cref="string"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="ReferenceExpression"/>, or a
/// list of strings and expressions. The same tree renders to YAML for the file provider (run mode) and to flat labels
/// for the Docker provider (publish).
/// </para>
/// </remarks>
internal sealed partial class TraefikOptionMap
{
    // Traefik matches option names case-insensitively in every provider, so `prefixes` and `Prefixes` are one key.
    private readonly OrderedDictionary<string, object> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of top-level options.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets the top-level options in the order they were first set. Nested options are <see cref="TraefikOptionMap"/>s.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object>> Entries => _entries;

    /// <summary>
    /// Sets the value at a dotted path, creating the maps on the way. Setting a path again replaces the value.
    /// </summary>
    /// <param name="path">The dotted path, for example <c>customRequestHeaders.X-Script-Name</c>.</param>
    /// <param name="value">The value: a string, a boolean, a number, an expression, or a list of strings and expressions.</param>
    /// <exception cref="ArgumentException">The path is invalid, or it conflicts with a value set before.</exception>
    public void Set(string path, object value)
    {
        var segments = path.Split('.');
        if (segments.Any(s => !PathSegmentRegex().IsMatch(s)))
        {
            throw new ArgumentException($"The option path '{path}' is invalid. Use names of letters, digits, '-' and '_', separated by '.'.", nameof(path));
        }

        // Check the whole path before creating any map, so a conflicting path leaves the tree unchanged.
        var existingMap = this;
        for (var i = 0; i < segments.Length && existingMap is not null; i++)
        {
            var isLast = i == segments.Length - 1;
            if (!existingMap._entries.TryGetValue(segments[i], out var existing))
            {
                break;
            }

            if (!isLast && existing is not TraefikOptionMap)
            {
                throw new ArgumentException($"The option path '{path}' conflicts with the value already set at '{string.Join('.', segments[..(i + 1)])}'.", nameof(path));
            }

            if (isLast && existing is TraefikOptionMap)
            {
                throw new ArgumentException($"The option path '{path}' conflicts with the options already set below it.", nameof(path));
            }

            existingMap = existing as TraefikOptionMap;
        }

        var map = this;
        foreach (var segment in segments[..^1])
        {
            if (!map._entries.TryGetValue(segment, out var existing))
            {
                existing = new TraefikOptionMap();
                map._entries[segment] = existing;
            }

            map = (TraefikOptionMap)existing;
        }

        map._entries[segments[^1]] = value;
    }

    /// <summary>
    /// Sets the value at a dotted path unless it is <see langword="null"/>, which leaves Traefik's default.
    /// </summary>
    /// <param name="path">The dotted path.</param>
    /// <param name="value">The value, or <see langword="null"/> to leave the option unset.</param>
    public void SetIfNotNull(string path, object? value)
    {
        if (value is not null)
        {
            Set(path, value);
        }
    }

    /// <summary>
    /// Sets a list at a dotted path unless it is empty, which leaves Traefik's default.
    /// </summary>
    /// <param name="path">The dotted path.</param>
    /// <param name="values">The items, or none to leave the option unset.</param>
    public void SetIfNotEmpty(string path, IList<string> values)
    {
        if (values.Count > 0)
        {
            Set(path, values.Cast<object>().ToArray());
        }
    }

    /// <summary>
    /// Gets every expression in the tree, including those in lists.
    /// </summary>
    /// <returns>The expressions, in the order the options were set.</returns>
    public IEnumerable<ReferenceExpression> GetExpressions()
    {
        foreach (var (_, value) in _entries)
        {
            switch (value)
            {
                case ReferenceExpression expression:
                    yield return expression;
                    break;
                case IReadOnlyList<object> list:
                    foreach (var expression in list.OfType<ReferenceExpression>())
                    {
                        yield return expression;
                    }
                    break;
                case TraefikOptionMap map:
                    foreach (var expression in map.GetExpressions())
                    {
                        yield return expression;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Renders the tree for the file provider.
    /// </summary>
    /// <param name="renderExpression">Renders an expression: its resolved value in run mode, a Docker Compose placeholder at publish time.</param>
    /// <param name="renderLiteral">Renders literal text, which Docker Compose needs escaped at publish time.</param>
    /// <returns>A tree of dictionaries and lists for the YAML serializer.</returns>
    /// <remarks>
    /// Synchronous so the same rendering serves run mode, where the expressions are resolved beforehand, and publish
    /// time, where the Docker Compose callbacks that render them are synchronous.
    /// </remarks>
    public Dictionary<string, object> ToFileConfiguration(Func<ReferenceExpression, string> renderExpression, Func<string, string> renderLiteral)
    {
        var result = new Dictionary<string, object>(_entries.Count);

        foreach (var (key, value) in _entries)
        {
            result[key] = value switch
            {
                TraefikOptionMap map => map.ToFileConfiguration(renderExpression, renderLiteral),
                IReadOnlyList<object> list => list.Select(item => item is ReferenceExpression expression ? renderExpression(expression) : renderLiteral((string)item)).ToList(),
                ReferenceExpression expression => renderExpression(expression),
                string text => renderLiteral(text),
                _ => value,
            };
        }

        return result;
    }

    /// <summary>
    /// Renders the tree as Docker labels under <paramref name="prefix"/>.
    /// </summary>
    /// <param name="labels">The labels to add to.</param>
    /// <param name="prefix">The label key of the tree, for example <c>traefik.http.middlewares.strip.stripPrefix</c>.</param>
    /// <param name="renderExpression">Renders an expression for Docker Compose.</param>
    /// <remarks>
    /// Nested keys are joined with dots and lists with commas, as the Docker provider reads them:
    /// <code>
    /// traefik.http.middlewares.strip.stripPrefix.prefixes: /v1,/v2
    /// traefik.http.middlewares.sec.headers.customRequestHeaders.X-Script-Name: test
    /// </code>
    /// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/"/>.
    /// </remarks>
    public void AddLabels(IDictionary<string, string> labels, string prefix, Func<ReferenceExpression, string> renderExpression)
    {
        foreach (var (key, value) in _entries)
        {
            var label = $"{prefix}.{key}";

            if (value is TraefikOptionMap map)
            {
                map.AddLabels(labels, label, renderExpression);
                continue;
            }

            labels[label] = value switch
            {
                IReadOnlyList<object> list => string.Join(",", list.Select(item => ToListItem(label, item, renderExpression))),
                _ => ToLabelValue(value, renderExpression),
            };
        }

        static string ToListItem(string label, object item, Func<ReferenceExpression, string> renderExpression)
        {
            // The Docker provider splits list values on commas, and there is no escape for a comma inside an item.
            if (item is string text && text.Contains(','))
            {
                throw new InvalidOperationException($"The value '{text}' of the Traefik option '{label}' contains ',', which Docker labels use to separate list items.");
            }

            return ToLabelValue(item, renderExpression);
        }
    }

    /// <summary>
    /// Gets the expressions in the tree that reference an endpoint, such as a forward authentication address, in a
    /// stable order.
    /// </summary>
    /// <returns>The expressions, in the order the options were set.</returns>
    public IEnumerable<ReferenceExpression> GetEndpointExpressions() => GetExpressions().Where(ReferencesEndpoint);

    /// <summary>
    /// Gets the resources whose endpoints the tree references.
    /// </summary>
    /// <returns>Each resource once.</returns>
    public IEnumerable<IResource> GetReferencedResources() =>
        GetEndpointExpressions().SelectMany(GetEndpointResources).Distinct();

    /// <summary>
    /// Gets a value indicating whether an expression references an endpoint, whose address differs per mode.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <returns><see langword="true"/> when the expression or a nested one references an endpoint; otherwise, <see langword="false"/>.</returns>
    public static bool ReferencesEndpoint(ReferenceExpression expression) => GetEndpointResources(expression).Any();

    private static string ToLabelValue(object value, Func<ReferenceExpression, string> renderExpression) => value switch
    {
        string text => TraefikDockerLabels.EscapeLiteral(text),
        bool flag => TraefikNames.ToLiteral(flag),
        long number => number.ToString(CultureInfo.InvariantCulture),
        ReferenceExpression expression => renderExpression(expression),
        _ => throw new InvalidOperationException($"Unsupported Traefik option value '{value}'."),
    };

    private static IEnumerable<IResource> GetEndpointResources(ReferenceExpression expression) =>
        expression.ValueProviders.SelectMany(provider => provider switch
        {
            EndpointReference endpoint => [endpoint.Resource],
            EndpointReferenceExpression property => [property.Endpoint.Resource],
            ReferenceExpression nested => GetEndpointResources(nested),
            _ => Enumerable.Empty<IResource>(),
        });

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex PathSegmentRegex();
}
