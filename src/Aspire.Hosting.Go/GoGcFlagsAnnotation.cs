// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

internal sealed class GoGcFlagsAnnotation(string flags) : IResourceAnnotation
{
    public string Flags { get; } = flags;
}
