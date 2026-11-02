// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class JUnitTestingExportTests
{
    [Fact]
    public async Task GeneratesExperimentalJUnitTestingOnJavaResources()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(JUnitTestingExtensions).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Java/withJUnitTesting");
        Assert.True(capability.IsExperimental);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var java = Regex.Match(code, @"(?ms)^export interface JavaAppResourcePromise\b.*?^}").Value;
        Assert.NotEmpty(java);
        var method = Regex.Match(java, @"(?m)^    withJUnitTesting\([^\r\n]*;").Value;
        Assert.NotEmpty(method);
        await Verify(method);
    }
}
