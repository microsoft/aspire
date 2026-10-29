// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;

internal static class VerifyExtensions
{
    /// <summary>
    /// Normalizes the ServiceA fixture's project paths for manifest snapshots.
    /// </summary>
    /// <remarks>
    /// The manifest writer emits relative paths when the fixture and output share a
    /// filesystem root, but absolute paths across Windows drives. This expands the
    /// known fixture-directory prefix before Verify scrubs absolute locations, so
    /// snapshots do not depend on the checkout and temporary-directory layout.
    /// </remarks>
    public static SettingsTask ScrubProjectPaths(this SettingsTask task, string manifestPath)
    {
        var projectDirectory = Path.GetDirectoryName(new Projects.ServiceA().ProjectPath)!;
        var relativeDirectory = Path.GetRelativePath(Path.GetDirectoryName(manifestPath)!, projectDirectory).Replace('\\', '/');

        return task.AddScrubber(text => text.Replace(relativeDirectory, projectDirectory.Replace('\\', '/')));
    }
}
