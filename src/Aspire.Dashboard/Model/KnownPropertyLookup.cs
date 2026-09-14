// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Model;

public interface IKnownPropertyLookup
{
    (int SortOrder, KnownProperty? KnownProperty) FindProperty(string uid);
}

public sealed class KnownPropertyLookup : IKnownPropertyLookup
{
    private readonly List<(KnownProperty Property, int SortOrder)> _resourceProperties;

    public KnownPropertyLookup()
    {
        _resourceProperties =
        [
            new(new(KnownProperties.Resource.DisplayName, loc => loc[nameof(ResourcesDetailsDisplayNameProperty)]), KnownResourcePropertySortOrder.DisplayName),
            new(new(KnownProperties.Resource.State, loc => loc[nameof(ResourcesDetailsStateProperty)]), KnownResourcePropertySortOrder.State),
            new(new(KnownProperties.Resource.HealthState, loc => loc[nameof(ResourcesDetailsHealthStateProperty)]), KnownResourcePropertySortOrder.HealthState),
            new(new(KnownProperties.Resource.StartTime, loc => loc[nameof(ResourcesDetailsStartTimeProperty)]), KnownResourcePropertySortOrder.StartTime),
            new(new(KnownProperties.Resource.StopTime, loc => loc[nameof(ResourcesDetailsStopTimeProperty)]), KnownResourcePropertySortOrder.StopTime),
            new(new(KnownProperties.Resource.ExitCode, loc => loc[nameof(ResourcesDetailsExitCodeProperty)]), KnownResourcePropertySortOrder.ExitCode),
            new(new(KnownProperties.Resource.ConnectionString, loc => loc[nameof(ResourcesDetailsConnectionStringProperty)]), KnownResourcePropertySortOrder.ConnectionString)
        ];
    }

    public (int SortOrder, KnownProperty? KnownProperty) FindProperty(string uid)
    {
        foreach (var property in _resourceProperties)
        {
            if (property.Property.Key == uid)
            {
                return (property.SortOrder, property.Property);
            }
        }

        return (int.MaxValue, null);
    }
}
