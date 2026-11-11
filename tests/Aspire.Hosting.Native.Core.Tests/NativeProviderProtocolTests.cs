// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Rpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderProtocolTests
{
    [Theory]
    [InlineData("primitive")]
    [InlineData("handle")]
    [InlineData("Callback")]
    [InlineData("Dto")]
    public void CategoryIdentifiersAreExactAndUnsupportedShapesNeverBecomePrimitive(string category)
    {
        var metadata = JsonNode.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.protocol/value","method":"value",
            "parameters":[],"returnType":{"typeId":"string","category":"Primitive"}}]}
            """);
        metadata!["capabilities"]![0]!["returnType"]!["category"] = category;
        var fault = Assert.Throws<RpcFault>(() => Project(metadata));
        Assert.Equal("PROVIDER_METADATA_UNSUPPORTED", fault.FaultCode);
    }

    [Fact]
    public void OnlyValidatedResultAndCallbackProjectionsDeclareOwnedTypes()
    {
        var metadata = JsonNode.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.protocol/value","method":"value",
            "parameters":[{"name":"callback","isCallback":true,"type":{"typeId":"callback","category":"Callback"},
            "callbackParameters":[{"name":"value","type":{"typeId":"example.protocol/Argument","category":"Handle"}}],
            "callbackReturnType":{"typeId":"example.protocol/Writeback","category":"Handle"}}],
            "returnType":{"typeId":"example.protocol/Result","category":"Handle"}}]}
            """);
        var manifest = Project(metadata);
        Assert.Equal(["example.protocol/Argument", "example.protocol/Result", "example.protocol/Writeback"],
            manifest.OwnedTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DiscoveryExtensionsDoNotContributeUnvalidatedOwnedTypes()
    {
        var metadata = JsonNode.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.protocol/value","method":"value",
            "parameters":[],"returnType":{"typeId":"string","category":"Primitive"},
            "extension":{"typeId":"foreign/Resource","category":"Handle"}}]}
            """);
        var manifest = Project(metadata);
        Assert.Empty(manifest.OwnedTypes);
        Assert.Equal("foreign/Resource", Assert.Single(manifest.Capabilities).Value.Metadata["extension"]!["typeId"]!.GetValue<string>());
    }

    [Fact]
    public void ManifestProducesTypedReceiverAndCallbackProjections()
    {
        var metadata = JsonNode.Parse("""
            {"protocolVersion":2,"capabilities":[{
              "id":"example.protocol/attach","method":"attach",
              "targetTypeId":"example.protocol/Resource","targetParameterName":"resource",
              "parameters":[
                {"name":"resource","type":{"typeId":"example.protocol/Resource","category":"Handle"}},
                {"name":"callback","isCallback":true,"type":{"typeId":"callback","category":"Callback"},
                  "callbackParameters":[{"name":"resource","type":{"typeId":"example.protocol/Resource","category":"Handle"}}],
                  "callbackReturnType":{"typeId":"long","category":"Primitive"}}],
              "returnType":{"typeId":"bool","category":"Primitive"}
            }]}
            """)!;
        var manifest = Project(metadata);
        var capability = Assert.Single(manifest.Capabilities).Value;
        Assert.Equal("example.protocol/attach", capability.Id);
        Assert.Equal("example.protocol/Resource", capability.TargetTypeId);
        Assert.Equal("resource", capability.TargetParameterName);
        Assert.Null(capability.LifecyclePhase);
        Assert.Equal(NativeProviderValueKind.Boolean, capability.ReturnType.Kind);
        Assert.Collection(capability.Parameters,
            receiver =>
            {
                Assert.Equal("resource", receiver.Name);
                Assert.Equal(NativeProviderValueKind.Handle, receiver.Type.Kind);
                Assert.Null(receiver.Callback);
            },
            callback =>
            {
                Assert.Equal("callback", callback.Name);
                Assert.Equal(NativeProviderValueKind.Callback, callback.Type.Kind);
                Assert.NotNull(callback.Callback);
                Assert.Equal(NativeProviderValueKind.Handle, Assert.Single(callback.Callback.Parameters).Kind);
                Assert.Equal(NativeProviderValueKind.Int64, callback.Callback.ReturnType.Kind);
            });
        Assert.Equal("example.protocol/Resource", Assert.Single(manifest.OwnedTypes));
        metadata["capabilities"]![0]!["method"] = "changed";
        Assert.Equal("attach", capability.Metadata["method"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"protocolVersion":"2","capabilities":[]}""")]
    [InlineData("""{"protocolVersion":null,"capabilities":[]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":{}}""")]
    public void InvalidEnvelopeIsRejectedAtTheProtocolBoundary(string metadata)
    {
        var fault = Assert.Throws<RpcFault>(() => NativeProviderMetadata.Read(JsonNode.Parse(metadata)));
        Assert.Equal("PROVIDER_METADATA_INVALID", fault.FaultCode);
    }

    [Theory]
    [InlineData("""{"name":"value","type":{"typeId":"string","category":"Primitive"},"isOptional":"false"}""")]
    [InlineData("""{"name":"value","type":{"typeId":"callback","category":"Callback"},"isCallback":true,"callbackParameters":[42],"callbackReturnType":{"typeId":"string","category":"Primitive"}}""")]
    public void MalformedParametersProduceProtocolFaults(string parameter)
    {
        var metadata = JsonNode.Parse($$"""
            {"protocolVersion":2,"capabilities":[{
              "id":"example.protocol/invoke","method":"invoke","parameters":[{{parameter}}],
              "returnType":{"typeId":"string","category":"Primitive"}
            }]}
            """);
        var fault = Assert.Throws<RpcFault>(() => NativeProviderMetadata.Read(metadata));
        Assert.Equal("PROVIDER_METADATA_INVALID", fault.FaultCode);
    }

    [Theory]
    [InlineData("""{"$handle":"object","$type":"other/Resource","$lifetime":{}}""", "TYPE_MISMATCH")]
    [InlineData("""{"$handle":"","$type":"example.protocol/Resource","$lifetime":{}}""", "TYPE_MISMATCH")]
    [InlineData("""{"$handle":"object","$type":"example.protocol/Resource","$lifetime":null}""", "INVALID_HANDLE")]
    public void ReturnedHandleShapeIsCheckedWithoutResolvingAuthority(string wire, string code)
    {
        var contract = new NativeProviderContractProjection(NativeProviderMetadata.ReadCoreContract(NativeRpcConnection.CoreContract));
        var type = new NativeProviderType("example.protocol/Resource", NativeProviderValueKind.Handle);
        var fault = Assert.Throws<RpcFault>(() => NativeProviderValueProtocol.ParseReturnedHandle(JsonNode.Parse(wire), type, contract.CoreHandles));
        Assert.Equal(code, fault.FaultCode);
    }

    [Theory]
    [InlineData("isOptional")]
    [InlineData("isNullable")]
    public void ParameterFlagsAreParsedBeforeNativeProjectionPolicyIsApplied(string flag)
    {
        var metadata = JsonNode.Parse("""
            {"protocolVersion":2,"capabilities":[{
              "id":"example.protocol/invoke","method":"invoke",
              "parameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"void","category":"Primitive"}
            }]}
            """)!;
        metadata["capabilities"]![0]!["parameters"]![0]![flag] = true;
        var parsed = NativeProviderMetadata.Read(metadata);
        var parameter = Assert.Single(Assert.Single(parsed).Parameters);
        Assert.Equal(flag == "isOptional", parameter.IsOptional);
        Assert.Equal(flag == "isNullable", parameter.IsNullable);
        var contract = new NativeProviderContractProjection(NativeProviderMetadata.ReadCoreContract(NativeRpcConnection.CoreContract));
        var fault = Assert.Throws<RpcFault>(() => contract.Project("example.protocol", parsed));
        Assert.Equal("PROVIDER_METADATA_INVALID", fault.FaultCode);
    }

    private static NativeProviderManifest Project(JsonNode? metadata) =>
        new NativeProviderContractProjection(NativeProviderMetadata.ReadCoreContract(NativeRpcConnection.CoreContract))
            .Project("example.protocol", NativeProviderMetadata.Read(metadata));
}
