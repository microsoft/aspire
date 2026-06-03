// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Ats;

/// <summary>
/// Options for creating or updating files and directories in a container from polyglot apphosts.
/// </summary>
[AspireDto]
internal sealed class ContainerFilesOptions
{
    /// <summary>
    /// The default owner UID for the created or updated file system entries. Defaults to 0 for root if not set.
    /// </summary>
    public int? DefaultOwner { get; init; }

    /// <summary>
    /// The default group ID for the created or updated file system entries. Defaults to 0 for root if not set.
    /// </summary>
    public int? DefaultGroup { get; init; }

    /// <summary>
    /// The Unix umask to apply to files or directories without explicit permissions.
    /// Use octal literals in JavaScript or TypeScript, for example <c>0o022</c>.
    /// </summary>
    public int? Umask { get; init; }
}
