// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

/// <summary>
/// Annotation that specifies linker flags to pass to the <c>go run</c> command via <c>-ldflags</c>.
/// </summary>
internal sealed class GoLdFlagsAnnotation(string flags) : IResourceAnnotation
{
    public string Flags { get; } = flags;
}
