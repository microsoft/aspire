// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Go;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

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
        var testingMethod = Regex.Match(code, @"(?m)^    withTesting\([^\r\n]*: GoTestResourcePromise;").Value;
        var waitMethod = Regex.Match(code, @"(?m)^    waitForCompletionAsync\([^\r\n]*;").Value;
        var testingOptions = Regex.Match(code, @"(?ms)^export interface WithTestingOptions \{.*?^\}").Value;
        var waitOptions = Regex.Match(code, @"(?ms)^export interface WaitForCompletionAsyncOptions \{.*?^\}").Value;
        var contextMethod = Regex.Match(code, @"(?m)^    testingContext\(\): TestingContextPromise;").Value;
        var contextGetter = Assert.Single(scan.Capabilities, c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/ResourceTestingContext.testingContext");
        var rootOutputGetter = Assert.Single(scan.Capabilities, c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/TestingContext.outputPath");
        Assert.Equal(AtsCapabilityKind.PropertyGetter, contextGetter.CapabilityKind);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.TestingContext", contextGetter.ReturnType!.TypeId);
        Assert.Equal(AtsCapabilityKind.PropertyGetter, rootOutputGetter.CapabilityKind);
        Assert.NotEmpty(add);
        Assert.NotEmpty(captureMethod);
        Assert.NotEmpty(testingMethod);
        Assert.NotEmpty(waitMethod);
        Assert.NotEmpty(testingOptions);
        Assert.NotEmpty(waitOptions);
        Assert.NotEmpty(contextMethod);
        var methods = new
        {
            Add = add, Capture = captureMethod, Testing = testingMethod, Wait = waitMethod, testingOptions, waitOptions,
            Context = contextMethod, ContextGetter = contextGetter.CapabilityId, RootOutputGetter = rootOutputGetter.CapabilityId
        };
        await Verify(methods);
    }
}
