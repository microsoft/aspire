// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Provides constants for well-known ASP.NET Core configuration names.
/// </summary>
internal static class KnownAspNetCoreConfigNames
{
    public const string Environment = "ASPNETCORE_ENVIRONMENT";
    public const string DotNetEnvironment = "DOTNET_ENVIRONMENT";
    public const string ForwardedHeadersEnabled = "ASPNETCORE_FORWARDEDHEADERS_ENABLED";
    public const string HttpsPort = "ASPNETCORE_HTTPS_PORT";
    public const string Urls = "ASPNETCORE_URLS";
    public const string KestrelCertificatesDefaultPath = "Kestrel__Certificates__Default__Path";
    public const string KestrelCertificatesDefaultPassword = "Kestrel__Certificates__Default__Password";
}
