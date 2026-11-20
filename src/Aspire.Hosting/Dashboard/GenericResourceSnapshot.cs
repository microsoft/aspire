// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Hosting.Dashboard;

internal sealed class GenericResourceSnapshot(CustomResourceSnapshot state) : ResourceSnapshot
{
    public override string ResourceType => state.ResourceType;
    public override string ResourceTypeDisplayName => state.ResourceTypeDisplayName;

    protected override IEnumerable<(string Key, Value Value, bool IsSensitive, string? DisplayName, bool IsHighlighted, int? SortOrder)> GetProperties()
    {
        if (state.State?.IsInProgress is true)
        {
            // Resource properties are understood by older dashboards and persisted without a schema change.
            yield return (KnownProperties.Resource.StateIsInProgress, Value.ForBool(true), false, null, false, null);
        }

        foreach (var property in state.Properties)
        {
            var result = ConvertToValue(property.Value);

            yield return (property.Name, result, property.IsSensitive, property.DisplayName, property.IsHighlighted, property.SortOrder);
        }
    }
}
