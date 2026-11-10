// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Associates a resource with a named launch group for local application runs.
/// </summary>
/// <param name="launchGroupName">The launch group name. Names are compared case-insensitively.</param>
/// <remarks>
/// A resource can have multiple launch group annotations. Use <c>WithLaunchGroup</c> or
/// <c>WithLaunchGroups</c> to configure membership.
/// </remarks>
/// <exception cref="ArgumentNullException"><paramref name="launchGroupName"/> is null.</exception>
/// <exception cref="ArgumentException">
/// <paramref name="launchGroupName"/> is empty, contains only whitespace, has surrounding whitespace, or contains ',' or ';'.
/// </exception>
[Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class LaunchGroupAnnotation(string launchGroupName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the launch group.
    /// </summary>
    public string LaunchGroupName { get; } = ValidateName(launchGroupName, nameof(launchGroupName));

    internal static string ValidateName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (name != name.Trim() || name.Contains(',') || name.Contains(';'))
        {
            throw new ArgumentException("A launch group name cannot have surrounding whitespace or contain ',' or ';'.", paramName);
        }

        return name;
    }
}

/// <summary>
/// Marks a declared dependency as optional for initial launch-group validation.
/// </summary>
internal sealed class OptionalLaunchGroupDependencyAnnotation(IResource resource) : IResourceAnnotation
{
    public IResource Resource { get; } = resource;
}
