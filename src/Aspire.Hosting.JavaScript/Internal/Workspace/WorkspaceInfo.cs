// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal sealed record WorkspaceInfo(
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> RootDirs,
    IReadOnlyList<string> WorkspaceDirs,
    string AppName);
