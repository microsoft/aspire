// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ResourceStartAbortedException : DistributedApplicationException
{
    public ResourceStartAbortedException(string resourceName, string? state)
        : base(string.Format(CultureInfo.InvariantCulture, "Startup for resource '{0}' was canceled while waiting for dependencies. Current state is '{1}'.", resourceName, state ?? "<none>"))
    {
    }
}
