// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class CargoTestingExportTests
{
    [Fact]
    public async Task GeneratesExperimentalCargoTesting()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(CargoTestingExtensions).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Rust/withCargoTesting");
        Assert.True(capability.IsExperimental);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var executable = Regex.Match(code, @"(?ms)^export interface ExecutableResourcePromise\b.*?^}").Value;
        Assert.NotEmpty(executable);
        var method = Regex.Match(executable, @"(?m)^    withCargoTesting\([^\r\n]*;").Value;
        Assert.NotEmpty(method);
        var contextMethod = Regex.Match(code, @"(?m)^    testingContext\(\): TestingContextPromise;").Value;
        Assert.NotEmpty(contextMethod);
        var contextGetter = Assert.Single(scan.Capabilities, c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/ResourceTestingContext.testingContext");
        var rootOutputGetter = Assert.Single(scan.Capabilities, c =>
            c.CapabilityId == "Aspire.Hosting.ApplicationModel/TestingContext.outputPath");
        Assert.Equal(AtsCapabilityKind.PropertyGetter, contextGetter.CapabilityKind);
        Assert.Equal("Aspire.Hosting/Aspire.Hosting.ApplicationModel.TestingContext", contextGetter.ReturnType!.TypeId);
        Assert.Equal(AtsCapabilityKind.PropertyGetter, rootOutputGetter.CapabilityKind);
        await Verify(new
        {
            Method = method,
            Context = contextMethod,
            ContextGetter = contextGetter.CapabilityId,
            RootOutputGetter = rootOutputGetter.CapabilityId
        });
    }
}
