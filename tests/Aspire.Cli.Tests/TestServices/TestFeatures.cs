// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestFeatures : IFeatures
{
    private readonly Dictionary<string, bool> _features = new();

    public TestFeatures SetFeature(string featureName, bool value)
    {
        _features[featureName] = value;
        return this;
    }

    public bool IsFeatureEnabled(string featureFlag, bool defaultValue)
    {
        return _features.TryGetValue(featureFlag, out var value) ? value : defaultValue;
    }

    public void LogFeatureState() { }
}
