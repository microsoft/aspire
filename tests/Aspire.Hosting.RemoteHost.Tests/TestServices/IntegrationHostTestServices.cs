// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Tests;

public static class IntegrationHostTestServices
{
    internal static IntegrationHostLauncher CreateLauncher(
        LanguageSupportResolver resolver, ExternalCapabilityRegistry registry, IConfiguration configuration,
        IHostApplicationLifetime lifetime, ILogger<IntegrationHostLauncher> logger)
    {
        var settings = new IntegrationHostConfiguration(configuration);
        var telemetry = RemoteHostProfilingTelemetry.Disabled;

        return new IntegrationHostLauncher(
            new IntegrationHostProcessLauncher(resolver, settings, telemetry, logger, new ChildProcessFactory(), TimeProvider.System),
            registry, settings, lifetime, logger, telemetry, TimeProvider.System);
    }
}
