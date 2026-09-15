// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

internal sealed class BrowserDebuggerResource(string name, string browser, string workingDirectory)
    : ExecutableResource(name, browser, workingDirectory)
{
}
