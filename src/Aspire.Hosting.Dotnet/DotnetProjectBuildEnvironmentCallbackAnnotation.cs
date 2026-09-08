// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Provides build-only environment variables for a .NET project resource.
/// </summary>
internal sealed class DotnetProjectBuildEnvironmentCallbackAnnotation(
    Func<EnvironmentCallbackContext, Task> callback) : IDotnetProgramBuildEnvironmentProvider
{
    public Func<EnvironmentCallbackContext, Task> Callback { get; } =
        callback ?? throw new ArgumentNullException(nameof(callback));

    public Task ApplyAsync(EnvironmentCallbackContext context) => Callback(context);
}

internal static class DotnetProjectBuildEnvironment
{
    public static Task<MsBuildResponseFile?> CreateResponseFileAsync(
        IReadOnlyDictionary<string, string> environment,
        ILogger logger,
        CancellationToken cancellationToken) =>
        MsBuildResponseFileFactory.CreateAsync(environment, logger, cancellationToken);

    public static string CreateMsBuildPropertyArgument(string name, string value) =>
        MsBuildResponseFileFactory.CreatePropertyArgument(name, value);

    internal static void TryDeleteDirectory(DirectoryInfo directory, ILogger logger)
        => MsBuildResponseFileFactory.TryDeleteDirectory(directory, logger);
}
