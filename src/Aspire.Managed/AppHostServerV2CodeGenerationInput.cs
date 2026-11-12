// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Native.Tooling;
using Aspire.TypeSystem;

namespace Aspire.Managed;

internal sealed record AppHostServerV2ProviderInput(string PackageName, IReadOnlyList<NativeProviderExport> Capabilities,
    IReadOnlyDictionary<string, string> SdkFiles, IReadOnlyList<string> Exports);

/// <summary>Loads request files and provider metadata before contract merging or SDK generation.</summary>
internal sealed record AppHostServerV2CodeGenerationInput(AtsContext Core, IReadOnlyList<AppHostServerV2ProviderInput> Providers, string Output)
{
    public static async Task<AppHostServerV2CodeGenerationInput> ReadAsync(string requestPath)
    {
        // CLI preparation writes {"coreContract":".../ats-contract.json","output":".../sdk",
        // "providers":[{"packageName":"example.provider","metadata":".../contract.json",
        // "sdkFiles":{"provider.mts":".../provider.mts"},"exports":["provider.mts"]}]}.
        var request = JsonSerializer.Deserialize<GenerationRequest>(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("The SDK generation request is missing.");
        if (string.IsNullOrWhiteSpace(request.CoreContract) || string.IsNullOrWhiteSpace(request.Output) || request.Providers is null)
        {
            throw new InvalidDataException("SDK generation requires a core contract, output directory and provider list.");
        }
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var core = JsonSerializer.Deserialize<AtsContext>(
            await File.ReadAllTextAsync(request.CoreContract).ConfigureAwait(false), options)
            ?? throw new InvalidDataException("AppHost Server v2 is missing its core ATS contract.");
        var providers = new List<AppHostServerV2ProviderInput>();
        foreach (var provider in request.Providers)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.PackageName) || string.IsNullOrWhiteSpace(provider.Metadata) ||
                provider.SdkFiles is null || provider.Exports is null)
            {
                throw new InvalidDataException("SDK generation requires complete provider descriptors.");
            }
            foreach (var name in provider.SdkFiles.Keys)
            {
                if (Path.GetFileName(name) != name || !name.EndsWith(".mts", StringComparison.Ordinal) ||
                    name is "native-builder.mts" or "native-client.mts" or "native-types.mts" or "native-pipeline.mts")
                {
                    throw new InvalidDataException($"Provider SDK file '{name}' is invalid.");
                }
            }
            foreach (var module in provider.Exports)
            {
                if (module is null || !provider.SdkFiles.ContainsKey(module))
                {
                    throw new InvalidDataException($"Provider entry point '{module}' is not an SDK file.");
                }
            }
            var capabilities = NativeProviderContractReader.Read(
                await File.ReadAllTextAsync(provider.Metadata).ConfigureAwait(false));
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, path) in provider.SdkFiles)
            {
                files.Add(name, await File.ReadAllTextAsync(path).ConfigureAwait(false));
            }
            providers.Add(new(provider.PackageName, capabilities, files, provider.Exports));
        }

        return new(core, providers, request.Output);
    }

    private sealed class GenerationRequest
    {
        public required string CoreContract { get; init; }
        public required string Output { get; init; }
        public required ProviderRequest[] Providers { get; init; }
    }

    private sealed class ProviderRequest
    {
        public required string PackageName { get; init; }
        public required string Metadata { get; init; }
        public required Dictionary<string, string> SdkFiles { get; init; }
        public required string[] Exports { get; init; }
    }
}
