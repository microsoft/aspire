// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TestUtilities;

[Flags]
public enum TestFeature
{
    SSLCertificate = 1 << 0,
    Playwright = 1 << 1,
    DevCert = 1 << 2,
    Docker = 1 << 3,
    DockerPluginBuildx = 1 << 4
}
