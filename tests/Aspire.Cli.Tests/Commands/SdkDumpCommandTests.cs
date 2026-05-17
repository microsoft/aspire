// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.DotNet.RemoteExecutor;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.Commands;

public class SdkDumpCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly RemoteInvokeOptions s_remoteInvokeOptions = new()
    {
        StartInfo = { RedirectStandardOutput = true }
    };

    [Fact]
    public async Task SdkDumpWithHelpReturnsZero()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    [InlineData("ci")]
    [InlineData("Ci")]
    [InlineData("pretty")]
    [InlineData("Pretty")]
    public void ParsesFormatOptionWithoutErrors(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"sdk dump --format {format}");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SdkDumpWithNonexistentCsprojReturnsFailedToFindProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump /nonexistent/path/to/integration.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithEmptyPackageNameReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump @13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithEmptyVersionReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithInvalidVersionFormatReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@not-a-version!!!");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task SdkDumpWithInvalidArgumentFormatReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump some-random-string");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void ParsesValidPackageFormatWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParsesMultipleMixedArgumentsWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0 Aspire.Hosting.PostgreSQL@13.2.0");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParsesPreReleaseVersionWithoutErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@13.2.0-preview.1");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SdkDumpWithDoubleAtSignReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // LastIndexOf('@') splits as "Aspire.Hosting.Redis@" and "13.2.0"
        // The @ in the package name part triggers format validation
        var result = command.Parse("sdk dump Aspire.Hosting.Redis@@13.2.0");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // "Aspire.Hosting.Redis@" is not a valid semver, so it should fail version validation
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void SdkDumpCi_ForHostingProject_DoesNotEmitWarnings()
    {
        // Assertions and skips inside the callback are surfaced back to the parent test process.
        using var result = RemoteExecutor.Invoke(async (baseDirectory) =>
        {
            var repoRoot = TryFindRepoRoot(baseDirectory);
            if (repoRoot is null)
            {
                Assert.Skip($"Could not find Aspire.slnx starting from '{baseDirectory}'.");
                return;
            }

            var projectPath = Path.Combine(repoRoot, "src", "Aspire.Hosting", "Aspire.Hosting.csproj");
            Assert.True(File.Exists(projectPath), $"Could not find Aspire.Hosting project at '{projectPath}'.");

            var tempDirectory = Path.Combine(Path.GetTempPath(), "aspire-sdk-dump-tests", $"{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var outputPath = Path.Combine(tempDirectory, "capabilities.txt");

            try
            {
                Environment.CurrentDirectory = repoRoot;
                Environment.SetEnvironmentVariable("ASPIRE_REPO_ROOT", repoRoot);
                Environment.SetEnvironmentVariable(CliConfigNames.NoLogo, "true");
                Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "true");
                Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "true");
                Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");

                // ExtraLongTimeout because this spawns a real dotnet build of Aspire.Hosting.csproj
                // in a child process, which can exceed the default timeout under concurrent test load.
                var exitCode = await Program.Main(["sdk", "dump", "--format", "ci", "--output", outputPath, projectPath]).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
                Assert.Equal(ExitCodeConstants.Success, exitCode);

                var output = await File.ReadAllTextAsync(outputPath);
                Assert.NotEmpty(output);

                var warningLines = output
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("warning:", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                Console.WriteLine($"Warnings: {warningLines.Length}");
                foreach (var warningLine in warningLines)
                {
                    Console.WriteLine(warningLine);
                }

                Assert.Empty(warningLines);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }, AppContext.BaseDirectory, options: s_remoteInvokeOptions);

        outputHelper.WriteLine(result.Process.StandardOutput.ReadToEnd());
    }

    [Fact]
    public void FormatJson_IncludesExportedValues()
    {
        var capabilities = CreateCapabilitiesInfo();

        var json = InvokeFormatter("FormatJson", capabilities);
        using var document = JsonDocument.Parse(json);

        var exportedValues = document.RootElement.GetProperty("ExportedValues");
        Assert.Single(exportedValues.EnumerateArray());
        var exportedValue = exportedValues[0];
        Assert.Equal("TestCatalog", exportedValue.GetProperty("PathSegments")[0].GetString());
        Assert.Equal("Default", exportedValue.GetProperty("PathSegments")[1].GetString());
        Assert.Equal("你好", exportedValue.GetProperty("Value").GetString());
    }

    [Fact]
    public void FormatCi_IncludesExportedValues()
    {
        var capabilities = CreateCapabilitiesInfo();

        var output = InvokeFormatter("FormatCi", capabilities);

        Assert.Contains("# Exported Values", output);
        Assert.Contains("TestCatalog.Default: test/string = \"你好\"", output);
    }

    [Fact]
    public void FormatPretty_IncludesExportedValues()
    {
        var capabilities = CreateCapabilitiesInfo();

        var output = InvokeFormatter("FormatPretty", capabilities);

        Assert.Contains("Exported Values (copied into guest SDKs)", output);
        Assert.Contains("TestCatalog.Default: string", output);
        Assert.Contains("\"你好\"", output);
    }

    [Fact]
    public void FormatCi_TreatsExplicitNullCapabilityCollectionsAsEmpty()
    {
        var json =
            """
            {
              "Diagnostics": null,
              "HandleTypes": null,
              "DtoTypes": [
                {
                  "TypeId": "test/dto",
                  "Name": "TestDto",
                  "Properties": null
                }
              ],
              "EnumTypes": [
                {
                  "TypeId": "test/enum",
                  "Name": "TestEnum",
                  "Values": null
                }
              ],
              "ExportedValues": [
                {
                  "PathSegments": null,
                  "Type": {
                    "TypeId": "test/string",
                    "Category": "Primitive"
                  },
                  "Value": "value"
                }
              ],
              "Capabilities": [
                {
                  "CapabilityId": "test.capability",
                  "MethodName": "Test",
                  "Parameters": null
                }
              ]
            }
            """;
        var capabilities = JsonSerializer.Deserialize(json, CapabilitiesJsonContext.Default.CapabilitiesInfo);

        Assert.NotNull(capabilities);
        var output = InvokeFormatter("FormatCi", capabilities);

        Assert.Contains("# DTO Types", output);
        Assert.Contains("test/dto", output);
        Assert.Contains("# Enum Types", output);
        Assert.Contains("test/enum = ", output);
        Assert.Contains(": test/string = \"value\"", output);
        Assert.Contains("test.capability() -> void", output);
    }

    [Theory]
    [InlineData("FormatCi")]
    [InlineData("FormatPretty")]
    public void Formatters_TolerateExplicitNullCapabilityScalars(string formatter)
    {
        var json =
            """
            {
              "Diagnostics": [
                {
                  "Severity": null,
                  "Message": null,
                  "Location": null
                }
              ],
              "HandleTypes": [
                {
                  "AtsTypeId": null,
                  "ImplementedInterfaces": null,
                  "BaseTypeHierarchy": null
                }
              ],
              "DtoTypes": [
                {
                  "TypeId": null,
                  "Name": null,
                  "Properties": [
                    {
                      "Name": null,
                      "Type": {
                        "TypeId": null,
                        "Category": null
                      }
                    }
                  ]
                }
              ],
              "EnumTypes": [
                {
                  "TypeId": null,
                  "Name": null,
                  "Values": [null, "Known"]
                }
              ],
              "Capabilities": [
                {
                  "CapabilityId": null,
                  "MethodName": null,
                  "Parameters": [
                    {
                      "Name": null,
                      "Type": {
                        "TypeId": null
                      }
                    }
                  ],
                  "ReturnType": {
                    "TypeId": null
                  }
                }
              ]
            }
            """;
        var capabilities = JsonSerializer.Deserialize(json, CapabilitiesJsonContext.Default.CapabilitiesInfo);

        Assert.NotNull(capabilities);
        var output = InvokeFormatter(formatter, capabilities);

        Assert.NotEmpty(output);
        Assert.Contains("unknown", output);
    }

    [Theory]
    [InlineData("FormatCi")]
    [InlineData("FormatPretty")]
    public void Formatters_TolerateNullCapabilityCollectionItems(string formatter)
    {
        var capabilities = new CapabilitiesInfo
        {
            Diagnostics = [null!, new DiagnosticInfo { Severity = "Warning", Message = "Careful" }],
            HandleTypes = [null!, new HandleTypeInfo { AtsTypeId = "test/handle" }],
            DtoTypes =
            [
                null!,
                new DtoTypeInfo
                {
                    TypeId = "test/dto",
                    Name = "TestDto",
                    Properties = [null!, new DtoPropertyInfo { Name = "Value" }]
                }
            ],
            EnumTypes =
            [
                null!,
                new EnumTypeInfo
                {
                    TypeId = "test/enum",
                    Name = "TestEnum",
                    Values = [null!, "Known"]
                }
            ],
            ExportedValues =
            [
                null!,
                new ExportedValueInfo
                {
                    PathSegments = [null!, "Value"],
                    Type = new TypeRefInfo { TypeId = "test/string" },
                    Value = JsonValue.Create("value")
                }
            ],
            Capabilities =
            [
                null!,
                new CapabilityInfo
                {
                    CapabilityId = "test.capability",
                    MethodName = "Test",
                    Parameters = [null!, new Aspire.Cli.Commands.Sdk.ParameterInfo { Name = "name" }]
                }
            ]
        };

        var output = InvokeFormatter(formatter, capabilities);

        Assert.NotEmpty(output);
        Assert.Contains("Known", output);
    }

    [Fact]
    public void FormatPretty_ThrowsClearExceptionWhenExportedValueTypeIsNull()
    {
        var capabilities = new CapabilitiesInfo
        {
            ExportedValues =
            [
                new ExportedValueInfo
                {
                    PathSegments = ["TestCatalog", "Default"],
                    Type = null
                }
            ]
        };

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeFormatter("FormatPretty", capabilities));
        var invalidOperationException = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Exported value 'TestCatalog.Default' is missing required type metadata.", invalidOperationException.Message);
    }

    private static string InvokeFormatter(string methodName, CapabilitiesInfo capabilities)
    {
        var method = typeof(SdkDumpCommand).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [capabilities]));
    }

    private static CapabilitiesInfo CreateCapabilitiesInfo()
    {
        return new CapabilitiesInfo
        {
            ExportedValues =
            [
                new ExportedValueInfo
                {
                    PathSegments = ["TestCatalog", "Default"],
                    Type = new TypeRefInfo
                    {
                        TypeId = "test/string",
                        Category = "Primitive"
                    },
                    Value = JsonValue.Create("你好"),
                    Description = "Greeting"
                }
            ]
        };
    }

    private static string? TryFindRepoRoot(string startPath)
    {
        var currentDirectory = new DirectoryInfo(startPath);

        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "Aspire.slnx")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }
}
