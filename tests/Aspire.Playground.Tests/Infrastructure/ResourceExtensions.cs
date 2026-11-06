// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace SamplesIntegrationTests.Infrastructure;

internal static class ResourceExtensions
{
    /// <summary>
    /// Gets the name of a .NET program based on its project file path.
    /// </summary>
    public static string GetName(this IDotnetProgramResource project)
    {
        var metadata = project.Annotations.OfType<IProjectMetadata>().Single();
        return Path.GetFileNameWithoutExtension(metadata.ProjectPath);
    }
}
