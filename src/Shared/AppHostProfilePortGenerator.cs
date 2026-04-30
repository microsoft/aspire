// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

internal static class AppHostProfilePortGenerator
{
    internal const int WindowsEphemeralPortMin = 49152;
    internal const int DashboardHttpPortMin = 15000;
    internal const int DashboardHttpPortMaxExclusive = 15300;
    internal const int DashboardHttpsPortMin = 17000;
    internal const int DashboardHttpsPortMaxExclusive = 17300;
    internal const int OtlpHttpPortMin = 19000;
    internal const int OtlpHttpPortMaxExclusive = 19300;
    internal const int OtlpHttpsPortMin = 21000;
    internal const int OtlpHttpsPortMaxExclusive = 21300;
    internal const int ResourceServiceHttpPortMin = 20000;
    internal const int ResourceServiceHttpPortMaxExclusive = 20300;
    internal const int ResourceServiceHttpsPortMin = 22000;
    internal const int ResourceServiceHttpsPortMaxExclusive = 22300;

    internal static AppHostProfilePorts Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        return new AppHostProfilePorts(
            DashboardHttpPort: random.Next(DashboardHttpPortMin, DashboardHttpPortMaxExclusive),
            DashboardHttpsPort: random.Next(DashboardHttpsPortMin, DashboardHttpsPortMaxExclusive),
            OtlpHttpPort: random.Next(OtlpHttpPortMin, OtlpHttpPortMaxExclusive),
            OtlpHttpsPort: random.Next(OtlpHttpsPortMin, OtlpHttpsPortMaxExclusive),
            ResourceServiceHttpPort: random.Next(ResourceServiceHttpPortMin, ResourceServiceHttpPortMaxExclusive),
            ResourceServiceHttpsPort: random.Next(ResourceServiceHttpsPortMin, ResourceServiceHttpsPortMaxExclusive));
    }
}

internal readonly record struct AppHostProfilePorts(
    int DashboardHttpPort,
    int DashboardHttpsPort,
    int OtlpHttpPort,
    int OtlpHttpsPort,
    int ResourceServiceHttpPort,
    int ResourceServiceHttpsPort);
