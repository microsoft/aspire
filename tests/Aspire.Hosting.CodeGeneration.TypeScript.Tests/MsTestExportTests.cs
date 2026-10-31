// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001 // Verifies the experimental MSTest generated API.

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class MsTestExportTests
{
    [Fact]
    public async Task GeneratesOneAddMsTestMethodWithTypedOptionsAndExecutableCapabilities()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(MsTestResource).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Dotnet/addMsTest");
        Assert.Equal("Aspire.Hosting.Dotnet/Aspire.Hosting.Dotnet.MsTestResource", capability.ReturnType!.TypeId);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var method = Regex.Match(code, @"(?m)^    addMsTest\([^\r\n]*;").Value;
        var options = Regex.Match(code, @"(?ms)^export interface MsTestOptions \{.*?^\}").Value;
        Assert.NotEmpty(method);
        Assert.NotEmpty(options);

        await Verify(new { Method = method, Options = options });
    }
}
