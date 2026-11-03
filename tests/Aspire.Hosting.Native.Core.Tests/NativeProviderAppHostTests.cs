// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderAppHostTests
{
    [Fact]
    public async Task OrdinaryCSharpAppHostUsesRealCSharpLibraryAndGeneratedIntegrationApi()
    {
        var apphost = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_PROVIDER_CSHARP_APPHOST");
        var provider = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_PROVIDER_CSHARP");
        if (apphost is null || provider is null)
        {
            Assert.Skip("The dedicated native lane builds the real C# integration provider and ordinary AppHost.");
        }
        await using var host = await NativeClientTestHost.StartAsync();
        var output = await NativeIntegrationAppHostRunner.RunAsync(apphost, [host.Endpoint.ToString()!, provider], host.Token, TimeProvider.System);
        Assert.Equal("catalog", output.Trim());
    }

    [Fact]
    public async Task OrdinaryTypeScriptAppHostUsesRealTypeScriptLibraryAndGeneratedIntegrationApi()
    {
        var apphost = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_PROVIDER_TYPESCRIPT_APPHOST");
        var provider = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_PROVIDER_TYPESCRIPT");
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (apphost is null || provider is null || node is null)
        {
            Assert.Skip("The dedicated native lane compiles the real TS integration provider and ordinary AppHost.");
        }
        await using var host = await NativeClientTestHost.StartAsync();
        var port = Assert.IsType<IPEndPoint>(host.Endpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var output = await NativeIntegrationAppHostRunner.RunAsync(node, [apphost, port, provider], host.Token, TimeProvider.System);
        Assert.Equal("catalog", output.Trim());
    }
}
