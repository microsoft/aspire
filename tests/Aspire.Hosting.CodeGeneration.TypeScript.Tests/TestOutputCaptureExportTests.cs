// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class TestOutputCaptureExportTests
{
    [Fact]
    public async Task GeneratesExperimentalCaptureOutputWithoutIntegrationAssemblies()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly]);
        var capture = Assert.Single(scan.Capabilities, c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/captureOutputAsync");
        Assert.True(capture.IsExperimental);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var resourceContext = Regex.Match(code, @"(?ms)^export interface ResourceTestingContext \{.*?^\}").Value;
        var captureMethod = Regex.Match(code, @"(?m)^    captureOutputAsync\([^\r\n]*;").Value;
        Assert.NotEmpty(resourceContext);
        Assert.NotEmpty(captureMethod);

        await Verify(new { ResourceContext = resourceContext, Capture = captureMethod });
    }
}
