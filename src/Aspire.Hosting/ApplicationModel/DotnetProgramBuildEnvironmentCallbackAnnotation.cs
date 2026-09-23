// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures build-only environment variables for a .NET program.
/// </summary>
internal sealed class DotnetProgramBuildEnvironmentCallbackAnnotation(
    Func<EnvironmentCallbackContext, Task> callback) : IResourceAnnotation
{
    public Func<EnvironmentCallbackContext, Task> Callback { get; } =
        callback ?? throw new ArgumentNullException(nameof(callback));

    public Task ApplyAsync(EnvironmentCallbackContext context) => Callback(context);
}
