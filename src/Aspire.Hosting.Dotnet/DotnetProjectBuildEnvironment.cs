// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures build-only environment variables for a coordinated .NET project build.
/// </summary>
internal sealed class DotnetProjectBuildEnvironmentCallbackAnnotation(
    Func<EnvironmentCallbackContext, Task> callback) : IResourceAnnotation
{
    public Func<EnvironmentCallbackContext, Task> Callback { get; } =
        callback ?? throw new ArgumentNullException(nameof(callback));
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
