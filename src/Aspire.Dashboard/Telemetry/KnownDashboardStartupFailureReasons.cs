// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Telemetry;

internal static class KnownDashboardStartupFailureReasons
{
    public const string ConfigurationValidation = "invalid_config";
    public const string AddressInUse = "address_in_use";
    public const string Canceled = "canceled";
}
