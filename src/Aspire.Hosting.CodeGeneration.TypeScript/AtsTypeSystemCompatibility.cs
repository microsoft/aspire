// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript;

internal static class AtsTypeSystemCompatibility
{
    private static readonly PropertyInfo? s_isExperimentalProperty =
        typeof(AtsCapabilityInfo).GetProperty(nameof(AtsCapabilityInfo.IsExperimental), BindingFlags.Instance | BindingFlags.Public);

    public static bool IsExperimental(AtsCapabilityInfo capability)
    {
        // The CLI force-shares Aspire.TypeSystem with code generators across SDK version skew.
        // Probe additive contract members so a newer generator still runs with an older CLI bundle.
        return s_isExperimentalProperty?.GetValue(capability) is true;
    }
}
