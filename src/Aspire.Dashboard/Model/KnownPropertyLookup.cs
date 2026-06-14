// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Model;

public interface IKnownPropertyLookup
{
    (int priority, KnownProperty? knownProperty) FindProperty(string resourceType, string uid);
}

public sealed class KnownPropertyLookup : IKnownPropertyLookup
{
    private readonly List<KnownProperty> _resourceProperties;

    public KnownPropertyLookup()
    {
        _resourceProperties =
        [
            new(KnownProperties.Resource.DisplayName, loc => loc[nameof(ResourcesDetailsDisplayNameProperty)]),
            new(KnownProperties.Resource.State, loc => loc[nameof(ResourcesDetailsStateProperty)]),
            new(KnownProperties.Resource.HealthState, loc => loc[nameof(ResourcesDetailsHealthStateProperty)]),
            new(KnownProperties.Resource.StartTime, loc => loc[nameof(ResourcesDetailsStartTimeProperty)]),
            new(KnownProperties.Resource.StopTime, loc => loc[nameof(ResourcesDetailsStopTimeProperty)]),
            new(KnownProperties.Resource.ExitCode, loc => loc[nameof(ResourcesDetailsExitCodeProperty)]),
            new(KnownProperties.Resource.ConnectionString, loc => loc[nameof(ResourcesDetailsConnectionStringProperty)])
        ];
    }

    public (int priority, KnownProperty? knownProperty) FindProperty(string resourceType, string uid)
    {
        for (var i = 0; i < _resourceProperties.Count; i++)
        {
            var kp = _resourceProperties[i];
            if (kp.Key == uid)
            {
                return (i, kp);
            }
        }

        return (int.MaxValue, null);
    }
}
