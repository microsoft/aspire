// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Model;

internal enum SymbolicValueKind { Literal, SecretParameter, Endpoint, Composite, UriEncoded, Parameter }
internal enum EndpointValueProperty { Host, Port, HostAndPort, Scheme, Url }
internal enum ValueNetworkContext { Host, Container }
internal enum SymbolicValueFailure { UnsupportedNetworkContext, EndpointUnavailable, StaleRevision, ConfigurationUnavailable, SecretHistoryLimit }

/// <summary>Reports a closed, payload-free symbolic resolution failure.</summary>
internal sealed class SymbolicValueException(SymbolicValueFailure failure) : InvalidOperationException(failure switch
{
    SymbolicValueFailure.UnsupportedNetworkContext => "The endpoint reference has no supported address in the requested workload network context.",
    SymbolicValueFailure.EndpointUnavailable => "The referenced workload endpoint is not available.",
    SymbolicValueFailure.StaleRevision => "The symbolic workload configuration revision is stale.",
    SymbolicValueFailure.SecretHistoryLimit => "The symbolic secret redaction history exceeds the runtime capacity.",
    _ => "The symbolic configuration binding is not available."
})
{
    public string Code => failure switch
    {
        SymbolicValueFailure.UnsupportedNetworkContext => "VALUE_NETWORK_CONTEXT_UNSUPPORTED",
        SymbolicValueFailure.EndpointUnavailable => "VALUE_ENDPOINT_UNAVAILABLE",
        SymbolicValueFailure.StaleRevision => "VALUE_REVISION_STALE",
        SymbolicValueFailure.SecretHistoryLimit => "VALUE_SECRET_HISTORY_LIMIT",
        _ => "VALUE_CONFIGURATION_UNAVAILABLE"
    };
}

/// <summary>Represents an authoring reference expression as immutable server-owned values, secret classification and authoritative resource references.</summary>
internal sealed class SymbolicValue
{
    private readonly string _text;
    private readonly ImmutableArray<SymbolicValue> _parts;
    public Guid GenerationId { get; }
    public SymbolicValueKind Kind { get; }
    public string ParameterName { get; }
    public ResourceHandle? Resource { get; }
    public string EndpointName { get; }
    public EndpointValueProperty EndpointProperty { get; }
    public bool IsSecret { get; }
    public bool HasValue { get; }
    private int Depth { get; }
    private int Nodes { get; }

    private SymbolicValue(Guid generationId, SymbolicValueKind kind, string text, string parameterName,
        ResourceHandle? resource, string endpointName, EndpointValueProperty property, ImmutableArray<SymbolicValue> parts, bool hasValue)
    {
        if (text.Length > 8192 || text.Contains('\0') || parts.IsDefault ||
            parts.Any(part => part.GenerationId != generationId))
        {
            throw new ArgumentException("Invalid symbolic value.");
        }
        GenerationId = generationId;
        Kind = kind;
        _text = text;
        ParameterName = parameterName;
        Resource = resource;
        EndpointName = endpointName;
        EndpointProperty = property;
        _parts = parts;
        HasValue = hasValue;
        IsSecret = kind == SymbolicValueKind.SecretParameter || parts.Any(part => part.IsSecret);
        Depth = parts.IsEmpty ? 1 : parts.Max(part => part.Depth) + 1;
        Nodes = parts.Sum(part => part.Nodes) + 1;
        if (Depth > 32 || Nodes > 256)
        {
            throw new ArgumentException("The symbolic value exceeds the expression capacity.");
        }
    }

    public static SymbolicValue Literal(Guid generationId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new(generationId, SymbolicValueKind.Literal, text, "", null, "", default, [], true);
    }

    public static SymbolicValue SecretParameter(Guid generationId, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ValidateParameterName(name);

        return new(generationId, SymbolicValueKind.SecretParameter, value, name, null, "", default, [], true);
    }

    public static SymbolicValue Parameter(Guid generationId, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateParameterName(name);

        return new(generationId, SymbolicValueKind.Parameter, value, name, null, "", default, [], true);
    }

    public static SymbolicValue UnboundParameter(Guid generationId, string name, bool secret)
    {
        ValidateParameterName(name);

        return new(generationId, secret ? SymbolicValueKind.SecretParameter : SymbolicValueKind.Parameter,
            "", name, null, "", default, [], false);
    }

    private static void ValidateParameterName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || !char.IsAsciiLetter(name[0]) ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException("Invalid parameter name.");
        }
    }

    public static SymbolicValue Endpoint(ResourceHandle resource, string name, EndpointValueProperty property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || !Enum.IsDefined(property))
        {
            throw new ArgumentException("Invalid symbolic endpoint reference.");
        }

        return new(resource.GenerationId, SymbolicValueKind.Endpoint, "", "", resource, name, property, [], true);
    }

    public SymbolicValue Concat(SymbolicValue suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        var prefixParts = Kind == SymbolicValueKind.Composite ? _parts : [this];
        var suffixParts = suffix.Kind == SymbolicValueKind.Composite ? suffix._parts : [suffix];

        return new(GenerationId, SymbolicValueKind.Composite, "", "", null, "", default, prefixParts.AddRange(suffixParts), true);
    }

    /// <summary>Escapes one value segment using RFC 3986 URI data escaping without losing its secret/reference identity.</summary>
    public SymbolicValue UriEncode() => new(GenerationId, SymbolicValueKind.UriEncoded, "", "", null, "", default, [this], true);

    public IEnumerable<ResourceHandle> References => Enumerate().Where(value => value.Resource is not null)
        .Select(value => value.Resource!.Value).Distinct();
    internal IEnumerable<SymbolicValue> SecretParameters => Enumerate().Where(value => value.Kind == SymbolicValueKind.SecretParameter);
    internal IEnumerable<SymbolicValue> Parameters => Enumerate().Where(value => value.Kind is SymbolicValueKind.SecretParameter or SymbolicValueKind.Parameter);
    public IEnumerable<string> ParameterReferences => Parameters
        .Select(value => value.ParameterName).Distinct(StringComparer.Ordinal);
    internal IEnumerable<string> SecretRedactions => Enumerate().Where(value => value.Kind == SymbolicValueKind.SecretParameter && value.HasValue)
        .SelectMany(value => new[] { value._text, Uri.EscapeDataString(value._text) }).Distinct(StringComparer.Ordinal);

    /// <summary>Rebinds a committed workspace expression to stable execution identities.</summary>
    public SymbolicValue Rebind(Guid generationId, Func<ResourceHandle, ResourceHandle> map) =>
        new(generationId, Kind, _text, ParameterName, Resource is { } resource ? map(resource) : null,
            EndpointName, EndpointProperty, _parts.Select(part => part.Rebind(generationId, map)).ToImmutableArray(), HasValue);

    public bool EquivalentTo(SymbolicValue other) => Kind == other.Kind && _text == other._text &&
        HasValue == other.HasValue && ParameterName == other.ParameterName && Resource == other.Resource && EndpointName == other.EndpointName &&
        EndpointProperty == other.EndpointProperty && _parts.Length == other._parts.Length &&
        _parts.Zip(other._parts).All(pair => pair.First.EquivalentTo(pair.Second));

    /// <summary>Projects portable expression syntax without exposing secret payloads or resolving runtime addresses.</summary>
    public T Project<T>(Func<string, T> literal, Func<string, T> parameter,
        Func<ResourceHandle, string, EndpointValueProperty, T> endpoint,
        Func<IEnumerable<T>, T> composite, Func<T, T> uriEncoded) => Kind switch
    {
        SymbolicValueKind.Literal => literal(_text),
        SymbolicValueKind.SecretParameter or SymbolicValueKind.Parameter => parameter(ParameterName),
        SymbolicValueKind.Endpoint => endpoint(Resource!.Value, EndpointName, EndpointProperty),
        SymbolicValueKind.Composite => composite(_parts.Select(part => part.Project(literal, parameter, endpoint, composite, uriEncoded))),
        SymbolicValueKind.UriEncoded => uriEncoded(_parts[0].Project(literal, parameter, endpoint, composite, uriEncoded)),
        _ => throw new InvalidOperationException("Unsupported symbolic value.")
    };

    public string Resolve(ValueNetworkContext context, Func<ResourceHandle, string, EndpointValueProperty, string> endpoint)
    {
        if (!Enum.IsDefined(context))
        {
            throw new SymbolicValueException(SymbolicValueFailure.UnsupportedNetworkContext);
        }
        var value = Kind switch
        {
            SymbolicValueKind.SecretParameter or SymbolicValueKind.Parameter when !HasValue =>
                throw new SymbolicValueException(SymbolicValueFailure.ConfigurationUnavailable),
            SymbolicValueKind.Literal or SymbolicValueKind.SecretParameter or SymbolicValueKind.Parameter => _text,
            SymbolicValueKind.Endpoint when context != ValueNetworkContext.Host =>
                throw new SymbolicValueException(SymbolicValueFailure.UnsupportedNetworkContext),
            SymbolicValueKind.Endpoint => endpoint(Resource!.Value, EndpointName, EndpointProperty),
            SymbolicValueKind.Composite => string.Concat(_parts.Select(part => part.Resolve(context, endpoint))),
            SymbolicValueKind.UriEncoded => Uri.EscapeDataString(_parts[0].Resolve(context, endpoint)),
            _ => throw new ArgumentException("Unsupported symbolic value.")
        };
        if (value.Length > 8192 || value.Contains('\0'))
        {
            throw new ArgumentException("The resolved symbolic value exceeds the launch capacity.");
        }

        return value;
    }

    public override string ToString() => IsSecret ? "[secret symbolic value]" : "[symbolic value]";

    private IEnumerable<SymbolicValue> Enumerate()
    {
        yield return this;
        foreach (var part in _parts)
        {
            foreach (var value in part.Enumerate())
            {
                yield return value;
            }
        }
    }
}

/// <summary>Stores a named immutable symbolic expression in a resource declaration.</summary>
internal sealed record SymbolicConfigurationEntry(string Name, SymbolicValue Value);
