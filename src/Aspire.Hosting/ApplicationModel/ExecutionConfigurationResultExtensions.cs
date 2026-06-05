// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Extension methods for <see cref="IExecutionConfigurationResult"/>.
/// </summary>
public static class ExecutionConfigurationResultExtensions
{
    /// <summary>
    /// Tries to get additional data of the specified type from the resource execution configuration.
    /// This is additional data added by configuration gatherers beyond the standard arguments and environment variables.
    /// </summary>
    /// <typeparam name="T">The type of additional data to retrieve.</typeparam>
    /// <param name="configuration">The resource execution configuration.</param>
    /// <param name="additionalData">The additional data if found.</param>
    /// <returns>True if the additional data was found; otherwise, false.</returns>
    public static bool TryGetAdditionalData<T>(this IExecutionConfigurationResult configuration, [NotNullWhen(true)] out T? additionalData) where T : IExecutionConfigurationData
    {
        foreach (var item in configuration.AdditionalConfigurationData)
        {
            if (item is T typedItem)
            {
                additionalData = typedItem;
                return true;
            }
        }

        additionalData = default;
        return false;
    }
}
