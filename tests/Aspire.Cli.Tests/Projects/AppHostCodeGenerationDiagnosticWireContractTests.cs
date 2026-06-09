// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

/// <summary>
/// Guards the wire contract between the AppHost server's reflection-based
/// <c>SystemTextJsonFormatter</c> (default <see cref="JsonSerializerOptions"/>, no naming policy,
/// PascalCase on the wire) and the CLI source-generated
/// <see cref="BackchannelJsonSerializerContext"/>. If either side adopts a naming policy or
/// renames a property, this test must catch it before the diagnostic silently degrades to
/// "all null" on the CLI as in issue #16709.
/// </summary>
public class AppHostCodeGenerationDiagnosticWireContractTests
{
    [Fact]
    public void Deserialize_PascalCaseServerPayload_PopulatesAllFields()
    {
        const string serverJson =
            """
            {
              "OriginalExceptionType": "System.TypeLoadException",
              "TypeName": "Aspire.Hosting.SomeType",
              "MemberName": "Void Foo()",
              "RuntimeAspireHostingVersion": "13.4.0-preview.1.26218.1",
              "RuntimeAspireHostingPath": "C:\\aspire\\Aspire.Hosting.dll",
              "LoadedAssemblies": [
                {
                  "Name": "Aspire.Hosting.CodeGeneration.TypeScript",
                  "InformationalVersion": "13.4.0-preview.1.26227.1",
                  "Location": "C:\\aspire\\Aspire.Hosting.CodeGeneration.TypeScript.dll"
                }
              ],
              "RemediationHint": "Run 'aspire update'."
            }
            """;

        var diagnostic = JsonSerializer.Deserialize(
            serverJson,
            BackchannelJsonSerializerContext.Default.AppHostCodeGenerationDiagnostic);

        Assert.NotNull(diagnostic);
        Assert.Equal("System.TypeLoadException", diagnostic.OriginalExceptionType);
        Assert.Equal("Aspire.Hosting.SomeType", diagnostic.TypeName);
        Assert.Equal("Void Foo()", diagnostic.MemberName);
        Assert.Equal("13.4.0-preview.1.26218.1", diagnostic.RuntimeAspireHostingVersion);
        Assert.Equal("C:\\aspire\\Aspire.Hosting.dll", diagnostic.RuntimeAspireHostingPath);
        Assert.Equal("Run 'aspire update'.", diagnostic.RemediationHint);
        var loaded = Assert.Single(diagnostic.LoadedAssemblies);
        Assert.Equal("Aspire.Hosting.CodeGeneration.TypeScript", loaded.Name);
        Assert.Equal("13.4.0-preview.1.26227.1", loaded.InformationalVersion);
        Assert.Equal("C:\\aspire\\Aspire.Hosting.CodeGeneration.TypeScript.dll", loaded.Location);
    }

    [Fact]
    public void Roundtrip_UsingRpcFormatterOptions_PreservesAllFields()
    {
        // Use the exact same JsonSerializerOptions the StreamJsonRpc formatter uses on the CLI
        // side. This catches the case where the wire-level deserializer differs from the typed
        // JsonTypeInfo<T> deserializer used by TryReadDiagnostic.
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var source = new AppHostCodeGenerationDiagnostic
        {
            OriginalExceptionType = "System.TypeLoadException",
            TypeName = "Aspire.Hosting.SomeType",
            MemberName = "Void Foo()",
            RuntimeAspireHostingVersion = "13.4.0-preview.1.26218.1",
            RuntimeAspireHostingPath = "/aspire/Aspire.Hosting.dll",
            LoadedAssemblies =
            [
                new AppHostLoadedAssemblyInfo
                {
                    Name = "Aspire.Hosting.CodeGeneration.TypeScript",
                    InformationalVersion = "13.4.0-preview.1.26227.1",
                    Location = "/aspire/Aspire.Hosting.CodeGeneration.TypeScript.dll"
                }
            ],
            RemediationHint = "Run 'aspire update'."
        };

        var json = JsonSerializer.Serialize(source, typeof(AppHostCodeGenerationDiagnostic), options);
        var roundtripped = (AppHostCodeGenerationDiagnostic?)JsonSerializer.Deserialize(json, typeof(AppHostCodeGenerationDiagnostic), options);

        Assert.NotNull(roundtripped);
        Assert.Equal(source.OriginalExceptionType, roundtripped.OriginalExceptionType);
        Assert.Equal(source.TypeName, roundtripped.TypeName);
        Assert.Equal(source.MemberName, roundtripped.MemberName);
        Assert.Equal(source.RuntimeAspireHostingVersion, roundtripped.RuntimeAspireHostingVersion);
        Assert.Equal(source.RemediationHint, roundtripped.RemediationHint);
        var loaded = Assert.Single(roundtripped.LoadedAssemblies);
        Assert.Equal(source.LoadedAssemblies[0].Name, loaded.Name);
        Assert.Equal(source.LoadedAssemblies[0].InformationalVersion, loaded.InformationalVersion);
    }
}
