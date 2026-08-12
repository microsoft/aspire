// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

internal sealed class GoAppArgsAnnotation(object[] args) : IResourceAnnotation
{
    public object[] Args { get; } = args;
}
