// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

namespace Aspire.Hosting.Tests.Utils;

public static class LaunchConfigurationTestHelpers
{
    public static LaunchConfigurationCallbackContext CreateCallbackContext(
        IResource resource,
        string mode = ExecutableLaunchMode.Debug,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new LaunchConfigurationCallbackContext(
            mode,
            resource,
            environmentVariables ?? new Dictionary<string, string>(),
            cancellationToken);
    }

    public static Task<object> InvokeLaunchConfigurationProducerAsync(
        IResource resource,
        LaunchConfigurationCallbackContext callbackContext)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(callbackContext);

        return resource.CreateLaunchConfigurationAsync(callbackContext);
    }
}
