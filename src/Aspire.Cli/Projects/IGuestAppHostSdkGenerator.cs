// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Generates SDK artifacts for a guest AppHost project.
/// </summary>
internal interface IGuestAppHostSdkGenerator
{
    /// <summary>
    /// Builds any required server components and generates guest SDK artifacts.
    /// </summary>
    /// <param name="directory">The AppHost project directory.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> if SDK generation succeeded; otherwise, <see langword="false"/>.</returns>
    Task<bool> BuildAndGenerateSdkAsync(DirectoryInfo directory, CancellationToken cancellationToken);
}