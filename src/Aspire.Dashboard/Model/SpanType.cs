// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Resources;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

public sealed class SpanType
{
    public string Name { get; }
    public TelemetryFilter Filter { get; }

    private SpanType(string name, TelemetryFilter filter)
    {
        Name = name;
        Filter = filter;
    }

    // https://opentelemetry.io/docs/specs/semconv/http/http-spans/
    public static readonly SpanType Http = new SpanType(
        "http",
        new SpanHasAttributeTelemetryFilter(["http.request.method"]));

    // https://opentelemetry.io/docs/specs/semconv/database/database-spans/
    public static readonly SpanType Database = new SpanType(
        "database",
        new SpanHasAttributeTelemetryFilter(["db.system.name", "db.system"]));

    // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
    public static readonly SpanType Messaging = new SpanType(
        "messaging",
        new SpanHasAttributeTelemetryFilter(["messaging.system"]));

    // https://opentelemetry.io/docs/specs/semconv/rpc/rpc-spans/
    public static readonly SpanType Rpc = new SpanType(
        "rpc",
        new SpanHasAttributeTelemetryFilter(["rpc.system"]));

    // https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
    public static readonly SpanType GenAI = new SpanType(
        "genai",
        new SpanHasAttributeTelemetryFilter(["gen_ai.system", "gen_ai.provider.name", "gen_ai.operation.name"]));

    // AWSSDK: https://github.com/aws/aws-sdk-net/blob/3acb8af23e9df891b4e37cf4cda9c5131be0389c/sdk/src/Core/Amazon.Runtime/Telemetry/TelemetryConstants.cs#L26
    // Azure: https://github.com/Azure/azure-sdk-for-net/blob/1af7c19fe8dcb7f7f843730117accbd6927ad2a4/sdk/ai/Azure.AI.Inference/src/Telemetry/OpenTelemetryConstants.cs#L46
    public static readonly SpanType Cloud = new SpanType(
        "cloud",
        new SpanScopePrefixTelemetryFilter(["Azure", "AWSSDK"]));

    public static readonly SpanType Other = new SpanType(
        "other",
        new SpanNoMatchTelemetryFilter([
            Http.Filter,
            Database.Filter,
            Messaging.Filter,
            Rpc.Filter,
            GenAI.Filter,
            Cloud.Filter
        ]));

    public static List<SelectViewModel<SpanType>> CreateKnownSpanTypes(IStringLocalizer<ControlsStrings> loc)
    {
        return new List<SelectViewModel<SpanType>>
        {
            new() { Id = null, Name = loc[nameof(ControlsStrings.LabelAll)] },
            new() { Id = Http, Name = loc[nameof(ControlsStrings.SpanTypeHttp)] },
            new() { Id = Database, Name = loc[nameof(ControlsStrings.SpanTypeDatabase)] },
            new() { Id = Messaging, Name = loc[nameof(ControlsStrings.SpanTypeMessaging)] },
            new() { Id = Rpc, Name = loc[nameof(ControlsStrings.SpanTypeRpc)] },
            new() { Id = GenAI, Name = loc[nameof(ControlsStrings.SpanTypeGenAI)] },
            new() { Id = Cloud, Name = loc[nameof(ControlsStrings.SpanTypeCloud)] },
            new() { Id = Other, Name = loc[nameof(ControlsStrings.LabelOther)] },
        };
    }
}

public sealed class SpanHasAttributeTelemetryFilter : TelemetryFilter
{
    private readonly string[] _attributeNames;

    internal IReadOnlyList<string> AttributeNames => _attributeNames;

    public SpanHasAttributeTelemetryFilter(string[] attributeNames)
    {
        ArgumentNullException.ThrowIfNull(attributeNames);
        if (attributeNames.Length == 0)
        {
            throw new ArgumentException("At least one attribute name is required.", nameof(attributeNames));
        }

        _attributeNames = attributeNames;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        return false;
    }
}

public sealed class SpanNoMatchTelemetryFilter : TelemetryFilter
{
    private readonly TelemetryFilter[] _filters;

    internal IReadOnlyList<TelemetryFilter> Filters => _filters;

    public SpanNoMatchTelemetryFilter(TelemetryFilter[] filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Length == 0)
        {
            throw new ArgumentException("At least one filter is required.", nameof(filters));
        }

        _filters = filters;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        return false;
    }
}

public sealed class SpanScopePrefixTelemetryFilter : TelemetryFilter
{
    private readonly string[] _scopePrefixes;

    internal IReadOnlyList<string> ScopePrefixes => _scopePrefixes;

    public SpanScopePrefixTelemetryFilter(string[] scopePrefixes)
    {
        ArgumentNullException.ThrowIfNull(scopePrefixes);
        if (scopePrefixes.Length == 0)
        {
            throw new ArgumentException("At least one scope prefix is required.", nameof(scopePrefixes));
        }

        _scopePrefixes = scopePrefixes;
    }

    public override bool Equals(TelemetryFilter? other)
    {
        return false;
    }
}
