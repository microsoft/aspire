// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Rpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeLanguageCatalogTests
{
    [Fact]
    public void RuntimeMetadataAndSdkSelectionAreProviderDataNotTypeScriptPolicy()
    {
        var registrations = JsonSerializer.Deserialize("""
            [{"runtimeSpec":{"language":"example/custom-runtime","codeGenLanguage":"Custom",
                "execute":{"command":"custom","args":["{appHostFile}"]}},"sdkResourcePrefix":"CustomSdk."},
             {"runtimeSpec":{"language":"example/other-runtime","codeGenLanguage":"Other"},
                "sdkResourcePrefix":"OtherSdk."}]
            """, NativeLanguageJsonContext.Default.NativeLanguageRegistrationArray)!;
        var catalog = new NativeLanguageCatalog(registrations,
            prefix => new JsonObject { ["library.custom"] = prefix });
        var spec = catalog.RuntimeSpec("example/custom-runtime");
        Assert.Equal("custom", spec["execute"]!["command"]!.GetValue<string>());
        Assert.Equal("CustomSdk.", catalog.GeneratedSdk("Custom")["library.custom"]!.GetValue<string>());
        Assert.Equal("OtherSdk.", catalog.GeneratedSdk("example/other-runtime")["library.custom"]!.GetValue<string>());
        spec["language"] = "mutated";
        Assert.Equal("example/custom-runtime", catalog.RuntimeSpec("example/custom-runtime")["language"]!.GetValue<string>());
        Assert.Throws<NotSupportedException>(() => catalog.RuntimeSpec("typescript/nodejs"));
    }

    [Fact]
    public void AmbiguousCodeGenerationTargetsAreRejected()
    {
        var registrations = JsonSerializer.Deserialize("""
            [{"runtimeSpec":{"language":"one","codeGenLanguage":"Shared"},"sdkResourcePrefix":"One."},
             {"runtimeSpec":{"language":"two","codeGenLanguage":"Shared"},"sdkResourcePrefix":"Two."}]
            """, NativeLanguageJsonContext.Default.NativeLanguageRegistrationArray)!;
        var catalog = new NativeLanguageCatalog(registrations, _ => new());
        Assert.Throws<NotSupportedException>(() => catalog.GeneratedSdk("Shared"));
    }
}
