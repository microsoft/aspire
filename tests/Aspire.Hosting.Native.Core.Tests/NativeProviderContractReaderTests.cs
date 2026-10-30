// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.CodeGeneration;
using Aspire.Hosting.Native.Tooling;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderContractReaderTests
{
    [Theory]
    [InlineData("""{"protocolVersion":"2","capabilities":[]}""")]
    [InlineData("""{"protocolVersion":null,"capabilities":[]}""")]
    [InlineData("""{"protocolVersion":1,"capabilities":[]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"method":"greet","parameters":[],"returnType":{"typeId":"void","category":"Primitive"}}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example/greet","method":"greet","parameters":[],"returnType":{"typeId":"void","category":"Primitive"},"lifecyclePhase":42}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example/greet","method":"greet","parameters":[null],"returnType":{"typeId":"void","category":"Primitive"}}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example/greet","method":"greet","parameters":[{"name":"value","type":null}],"returnType":{"typeId":"void","category":"Primitive"}}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example/configure","method":"configure","parameters":[{"name":"callback","type":{"typeId":"callback","category":"Callback"},"isCallback":true,"callbackParameters":[]}],"returnType":{"typeId":"void","category":"Primitive"}}]}""")]
    public void MalformedToolingMetadataIsReportedAtTheReaderBoundary(string json)
    {
        Assert.Throws<InvalidDataException>(() => NativeProviderContractReader.Read(json));
    }

    [Fact]
    public void AuthoringOnlyIntegrationsNeedNoRuntimeCapabilities()
    {
        Assert.Empty(NativeProviderContractReader.Read("""{"protocolVersion":2,"capabilities":[]}"""));
    }

    [Fact]
    public async Task PortableIdentityAliasesPreserveAtsTypesAndCallbackMetadata()
    {
        var exports = NativeProviderContractReader.Read("""
            {"protocolVersion":2,"capabilities":[{"id":"example/configure","method":"configure",
            "description":"Configure a resource.","isExperimental":true,
            "targetTypeId":"example/Resource","targetParameterName":"resource",
            "parameters":[{"name":"resource","type":{"typeId":"example/Resource","category":"Handle"}},
              {"name":"callback","type":{"typeId":"callback","category":"Callback"},"isCallback":true,
                "callbackParameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
                "callbackReturnType":{"typeId":"string","category":"Primitive"}}],
            "returnType":{"typeId":"example/Resource","category":"Handle"}}]}
            """);
        await Verify(exports).UseDirectory("Snapshots");
    }

    [Fact]
    public async Task StandaloneProviderProjectionConsumesParsedContractsAndOmitsLifecycleMethods()
    {
        var exports = NativeProviderContractReader.Read("""
            {"protocolVersion":2,"capabilities":[
              {"id":"example/greet","method":"greet","parameters":[
                {"name":"name","type":{"typeId":"string","category":"Primitive"}}],
                "returnType":{"typeId":"string","category":"Primitive"}},
              {"id":"example/initialize","method":"initialize","lifecyclePhase":"initialize",
                "parameters":[],"returnType":{"typeId":"void","category":"Primitive"}}]}
            """);
        await Verify(NativeProviderTypeScriptProjection.Generate(exports, []), "ts").UseDirectory("Snapshots");
    }

    [Fact]
    public async Task CoreHandleImportsUseTheContractInsteadOfInferringAnAssemblyPrefix()
    {
        var exports = NativeProviderContractReader.Read("""
            {"protocolVersion":2,"capabilities":[{"id":"example/getName","method":"getName",
              "parameters":[{"name":"resource","type":{"typeId":"core/Resource","category":"Handle"}}],
              "returnType":{"typeId":"string","category":"Primitive"}}]}
            """);
        await Verify(NativeProviderTypeScriptProjection.Generate(exports, [new() { AtsTypeId = "core/Resource" }]), "ts")
            .UseDirectory("Snapshots");
    }
}
