// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

/// <summary>
/// Annotation that specifies Go build tags to pass to the <c>go run</c> command via <c>-tags</c>.
/// </summary>
internal sealed class GoBuildTagsAnnotation(IReadOnlyList<string> tags) : IResourceAnnotation
{
    public IReadOnlyList<string> Tags { get; } = tags;
}
