// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Hosting.Dashboard;

internal sealed class GenericResourceSnapshot(CustomResourceSnapshot state) : ResourceSnapshot
{
    public override string ResourceType => state.ResourceType;

    protected override IEnumerable<(string Key, Value Value, bool IsSensitive, string? DisplayName, bool IsHighlighted, int? SortOrder)> GetProperties()
    {
        foreach (var property in state.Properties)
        {
            var result = ConvertToValue(property.Value);

            yield return (property.Name, result, property.IsSensitive, property.DisplayName, property.IsHighlighted, property.SortOrder);
        }
    }
}
