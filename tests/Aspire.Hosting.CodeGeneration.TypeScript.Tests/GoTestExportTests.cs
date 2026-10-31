// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Go;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class GoTestExportTests
{
    [Fact]
    public async Task GeneratesExperimentalGoTestAndCaptureOutput()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(GoTestResource).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Go/addGoTest");
        Assert.True(capability.IsExperimental);
        Assert.Equal("Aspire.Hosting.Go/Aspire.Hosting.Go.GoTestResource", capability.ReturnType!.TypeId);
        var capture = Assert.Single(scan.Capabilities, c => c.CapabilityId.Contains("captureOutput", StringComparison.Ordinal));
        Assert.True(capture.IsExperimental);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var add = Regex.Match(code, @"(?m)^    addGoTest\([^\r\n]*;").Value;
        var captureMethod = Regex.Match(code, @"(?m)^    captureOutputAsync\([^\r\n]*;").Value;
        Assert.NotEmpty(add);
        Assert.NotEmpty(captureMethod);
        var methods = new { Add = add, Capture = captureMethod };
        await Verify(methods);
    }
}
