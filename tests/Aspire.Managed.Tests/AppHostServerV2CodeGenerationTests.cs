// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Tooling;
using Aspire.TypeSystem;

namespace Aspire.Managed.Tests;

public class AppHostServerV2CodeGenerationTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ContractConflictsAreRejectedWithoutCreatingAnOutputDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = Path.Combine(workspace.Path, "generated");
        var capabilities = NativeProviderContractReader.Read("""
            {"protocolVersion":2,"capabilities":[{"id":"example.provider/greet","method":"greet",
              "parameters":[],"returnType":{"typeId":"string","category":"Primitive"}}]}
            """);
        var core = new AtsContext
        {
            Capabilities = [capabilities[0].Capability],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = []
        };
        var input = new AppHostServerV2CodeGenerationInput(core,
            [new("example.provider", capabilities, new Dictionary<string, string>(), [])], output);
        Assert.Throws<InvalidDataException>(() => AppHostServerV2CodeGeneration.Generate(input));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerationUsesLoadedInputsWithoutReopeningTheirFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var core = Path.Combine(workspace.Path, "core.json");
        var request = Path.Combine(workspace.Path, "request.json");
        var output = Path.Combine(workspace.Path, "generated");
        await File.WriteAllTextAsync(core, """{"Capabilities":[],"HandleTypes":[],"DtoTypes":[],"EnumTypes":[]}""");
        await File.WriteAllTextAsync(request, new JsonObject
        {
            ["coreContract"] = core,
            ["output"] = output,
            ["providers"] = new JsonArray()
        }.ToJsonString());
        var input = await AppHostServerV2CodeGenerationInput.ReadAsync(request);
        File.Delete(core);
        File.Delete(request);
        Assert.Contains("aspire.mts", AppHostServerV2CodeGeneration.Generate(input).Keys);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ProviderCannotExportAnotherProvidersSdkFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var core = Path.Combine(workspace.Path, "core.json");
        var metadata = Path.Combine(workspace.Path, "metadata.json");
        var source = Path.Combine(workspace.Path, "provider.mts");
        var request = Path.Combine(workspace.Path, "request.json");
        var output = Path.Combine(workspace.Path, "generated");
        await File.WriteAllTextAsync(core, """{"Capabilities":[],"HandleTypes":[],"DtoTypes":[],"EnumTypes":[]}""");
        await File.WriteAllTextAsync(metadata, """
            {"protocolVersion":2,"capabilities":[{"id":"example.provider/greet","method":"greet",
              "parameters":[],"returnType":{"typeId":"string","category":"Primitive"}}]}
            """);
        await File.WriteAllTextAsync(source, "export const providerName = 'example.provider';\n");
        await File.WriteAllTextAsync(request, new JsonObject
        {
            ["coreContract"] = core,
            ["output"] = output,
            ["providers"] = new JsonArray(
                new JsonObject
                {
                    ["packageName"] = "example.provider",
                    ["metadata"] = metadata,
                    ["sdkFiles"] = new JsonObject { ["provider.mts"] = source },
                    ["exports"] = new JsonArray("provider.mts")
                },
                new JsonObject
                {
                    ["packageName"] = "another.provider",
                    ["metadata"] = metadata,
                    ["sdkFiles"] = new JsonObject(),
                    ["exports"] = new JsonArray("provider.mts")
                })
        }.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AppHostServerV2CodeGeneration.GenerateAsync(request));
        Assert.Equal("Provider entry point 'provider.mts' is not an SDK file.", exception.Message);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("provider-client.mts", true)]
    [InlineData("aspire.mts", false)]
    [InlineData("native-builder.mts", false)]
    [InlineData("native-pipeline.mts", false)]
    [InlineData("../outside.mts", false)]
    public async Task PortableProviderUsesSharedProjectionWithoutReplacingTheFramework(string sdkFile, bool supported)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var core = Path.Combine(workspace.Path, "core.json");
        var metadata = Path.Combine(workspace.Path, "metadata.json");
        var source = Path.Combine(workspace.Path, "provider.mts");
        var request = Path.Combine(workspace.Path, "request.json");
        var output = Path.Combine(workspace.Path, "generated");
        await File.WriteAllTextAsync(core, """{"Capabilities":[],"HandleTypes":[],"DtoTypes":[],"EnumTypes":[]}""");
        await File.WriteAllTextAsync(metadata, """
            {"protocolVersion":2,"capabilities":[{"id":"example.provider/greet","method":"greet",
              "parameters":[{"name":"name","type":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"string","category":"Primitive"}}]}
            """);
        await File.WriteAllTextAsync(source, "export const providerName = 'example.provider';\n");
        await File.WriteAllTextAsync(request, new JsonObject
        {
            ["coreContract"] = core,
            ["output"] = output,
            ["providers"] = new JsonArray((JsonNode)new JsonObject
            {
                ["packageName"] = "example.provider",
                ["metadata"] = metadata,
                ["sdkFiles"] = new JsonObject { [sdkFile] = source },
                ["exports"] = new JsonArray(sdkFile)
            })
        }.ToJsonString());

        if (!supported)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => AppHostServerV2CodeGeneration.GenerateAsync(request));
            Assert.False(Directory.Exists(output));
            return;
        }
        Assert.Equal(0, await AppHostServerV2CodeGeneration.GenerateAsync(request));
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(Path.Combine(output, sdkFile)));
        await Verify(await File.ReadAllTextAsync(Path.Combine(output, "aspire.mts")), extension: "ts")
            .UseDirectory("Snapshots")
            .UseFileName("PortableProviderSdk");
    }
}
