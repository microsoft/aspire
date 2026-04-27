// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

internal interface IFeatures
{
    bool IsFeatureEnabled(string featureFlag, bool defaultValue);
    void LogFeatureState();
}
