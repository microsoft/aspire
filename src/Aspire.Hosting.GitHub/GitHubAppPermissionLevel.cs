// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies the level of access that a GitHub App is granted to a permission.
/// </summary>
/// <remarks>
/// The levels match the values GitHub uses for app permissions. Not every permission supports every level; for example,
/// <c>metadata</c> is read-only and only a few permissions accept <see cref="Admin"/>.
/// See https://docs.github.com/rest/apps/apps#create-a-github-app-from-a-manifest.
/// </remarks>
public enum GitHubAppPermissionLevel
{
    /// <summary>
    /// The app can read the resources the permission covers.
    /// </summary>
    Read,

    /// <summary>
    /// The app can read and change the resources the permission covers.
    /// </summary>
    Write,

    /// <summary>
    /// The app can read, change and administer the resources the permission covers.
    /// </summary>
    Admin
}
