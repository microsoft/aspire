// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class GoGeneratorEscapingTests
{
    private const string ResourceTypeId = "Tests/Tests.EscapedResource";
    private const string ChildTypeId = "Tests/Tests.Child\"Quote";
    private const string SettingsTypeId = "Tests/Tests.EscapedSettings";
    private const string StatusTypeId = "Tests/Tests.EscapedStatus";

    // Tries to end the comment it is written into and to declare a function after it.
    private const string InjectedText = "Adds Foo.\nfunc init() {}\n// ";
    private const string InjectedStatement = "func init() {}";

    public enum EscapedStatus
    {
        Ready
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithHostileMetadata_EscapesGoOutput()
    {
        var sdk = new AtsGoCodeGenerator().GenerateDistributedApplication(CreateHostileContext())["aspire.go"];

        await Verify(MetadataDriven(sdk), extension: "go")
            .UseFileName("EscapedMetadataGeneratedAspire");
    }

    [Fact]
    public void GenerateDistributedApplication_WithHostileMetadata_KeepsInjectedTextOutOfGoCode()
    {
        var sdk = new AtsGoCodeGenerator().GenerateDistributedApplication(CreateHostileContext())["aspire.go"];

        // The Go scanner ends a line at CR, LF or the Unicode line and paragraph separators, so a raw one in the
        // output would put the injected text at the start of a line.
        Assert.DoesNotContain('\u2028', sdk);
        Assert.DoesNotContain('\u2029', sdk);
        Assert.DoesNotContain('\r', sdk.Replace("\r\n", "\n", StringComparison.Ordinal));

        var lines = sdk.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimStart()).ToList();

        // The injected text must be present for the checks below to prove anything.
        Assert.Contains(lines, line => line.Contains(InjectedStatement, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(InjectedStatement, StringComparison.Ordinal) && !line.StartsWith("//", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("func init", StringComparison.Ordinal) || line.StartsWith("func injected", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedSdk_WithHostileMetadata_PassesGoBuildAndVet()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            foreach (var (name, content) in new AtsGoCodeGenerator().GenerateDistributedApplication(CreateHostileContext()))
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            var build = await GoDocCommentTests.RunAsync("go", directory.FullName, timeout.Token, "build", "./...");
            Assert.True(build.ExitCode == 0, $"go build failed:{Environment.NewLine}{build.Output}");

            var vet = await GoDocCommentTests.RunAsync("go", directory.FullName, timeout.Token, "vet", "./...");
            Assert.True(vet.ExitCode == 0, $"go vet failed:{Environment.NewLine}{vet.Output}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Everything between the enums and the builder entry point is generated from metadata; the header before it and
    // the builder after it are fixed text.
    private static string MetadataDriven(string sdk)
    {
        var lines = sdk.ReplaceLineEndings("\n").Split('\n');
        var start = Array.IndexOf(lines, "// Enums") - 1;
        var end = Array.IndexOf(lines, "// Builder construction & Build()") - 1;
        return string.Join('\n', lines[start..end]);
    }

    private static AtsContext CreateHostileContext()
    {
        var stringType = Primitive(AtsConstants.String);
        var numberType = Primitive(AtsConstants.Number);
        var voidType = Primitive(AtsConstants.Void);
        var resourceType = Handle(ResourceTypeId);
        var tagsType = new AtsTypeRef { TypeId = "Tests/List<string>", Category = AtsTypeCategory.List, ElementType = stringType };
        var unionType = new AtsTypeRef { TypeId = "Tests/Union", Category = AtsTypeCategory.Union, UnionTypes = [stringType, numberType] };
        var callbackType = new AtsTypeRef { TypeId = "Tests/Callback", Category = AtsTypeCategory.Callback };
        var enumType = new AtsTypeRef { TypeId = StatusTypeId, Category = AtsTypeCategory.Enum };
        var lookupType = new AtsTypeRef { TypeId = "Tests/Dict<string,string>", Category = AtsTypeCategory.Dict, KeyType = stringType, ValueType = stringType, IsReadOnly = true };
        var unknownType = new AtsTypeRef { TypeId = "Tests/Unknown", Category = AtsTypeCategory.Unknown };
        AtsCallbackParameterInfo[] callbackParameters = [new() { Name = "con\"text", Type = stringType }];

        return new AtsContext
        {
            Capabilities =
            [
                // The description is the only documentation, so it reaches the comment without any XML-doc processing.
                Capability("Tests/addFoo", "addFoo", resourceType, description: InjectedText),
                Capability("Tests/withHostileDocumentation", "withHostileDocumentation", resourceType,
                    documentation: new AtsDocumentationInfo { Summary = InjectedText, Remarks = InjectedText, Returns = InjectedText },
                    parameters: [Parameter("value", stringType, InjectedText)],
                    isObsolete: true, obsoleteMessage: InjectedText, isExperimental: true),

                // Fluent capability: required, optional (Options struct) and union parameters.
                Capability("Tests/with\"Quote", "with\"Quote", resourceType,
                    parameters:
                    [
                        Parameter("na\"me", stringType, "The name."),
                        Parameter("un\"ion", unionType, "The union value."),
                        Parameter("op\"tional", stringType, "The optional value.", isOptional: true)
                    ]),

                // Capability returning a child handle whose type id is also registered as a handle wrapper.
                Capability("Tests/addChild\"Quote%dId", "add-Child\"X", Handle(ChildTypeId), description: "Adds a child."),

                // The unexpected-type error text embeds the id, so a percent sign must not become a format verb.
                Capability("Tests/getExpression\"%d", "getExpression", Handle(AtsConstants.ReferenceExpressionTypeId), description: "Gets an expression."),
                Capability("Tests/getBack\\slash", "getBackslash", stringType, kind: AtsCapabilityKind.InstanceMethod),

                // Line and paragraph separators in an id, and a target parameter that needs a map key.
                Capability("Tests/void\r\nfunc injected() {}\u2028func injected() {}\u2029", "reset", voidType, kind: AtsCapabilityKind.InstanceMethod,
                    targetParameterName: "tar\"get",
                    parameters: [Callback("cal\"lback", callbackType, callbackParameters, voidType)]),
                Capability("Tests/tags\"List", "tags", tagsType, kind: AtsCapabilityKind.PropertyGetter, description: "Gets the tags."),
                Capability("Tests/withCancel", "withCancel", resourceType,
                    parameters: [Parameter("cancel\"Token", Primitive(AtsConstants.CancellationToken), isOptional: true)])
            ],
            HandleTypes =
            [
                new AtsTypeInfo { AtsTypeId = ResourceTypeId, Documentation = new AtsDocumentationInfo { Summary = InjectedText } }
            ],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    TypeId = SettingsTypeId,
                    Name = "EscapedSettings",
                    Properties =
                    [
                        new AtsDtoPropertyInfo { Name = "Bad\"Name", Type = stringType, Documentation = new AtsDocumentationInfo { Summary = InjectedText } },
                        new AtsDtoPropertyInfo { Name = "Back\\slash", Type = numberType },
                        new AtsDtoPropertyInfo { Name = "cal\"lback", Type = callbackType, IsCallback = true, CallbackParameters = callbackParameters, CallbackReturnType = voidType }
                    ]
                }
            ],
            EnumTypes =
            [
                new AtsEnumTypeInfo
                {
                    TypeId = StatusTypeId,
                    Name = "EscapedStatus",
                    ClrType = typeof(EscapedStatus),
                    Values = [nameof(EscapedStatus.Ready)],
                    ValueInfos = [new AtsEnumValueInfo { Name = nameof(EscapedStatus.Ready), Documentation = new AtsDocumentationInfo { Summary = InjectedText } }]
                }
            ],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "Tests",
                    PathSegments = ["Defaults\"X", "Pri-mary"],
                    Type = stringType,
                    Value = JsonValue.Create("primary"),
                    Description = InjectedText
                },

                // Characters outside the Basic Multilingual Plane (U+1F600 here) are written as UTF-8. A JSON string
                // would spell them as the surrogate pair \uD83D\uDE00, which Go rejects in a string literal. The
                // string, map key, enum and untyped values each take a different path through the renderer.
                ExportedValue(["Emoji", "Greeting"], stringType, JsonValue.Create("hi \U0001F600")),
                ExportedValue(["Emoji", "Lookup"], lookupType, new JsonObject { ["\U0001F600"] = "\U0001F600" }),
                ExportedValue(["Emoji", "Status"], enumType, JsonValue.Create("Ready\U0001F600")),
                ExportedValue(["Emoji", "Anything"], unknownType, JsonValue.Create("\U0001F600"))
            ]
        };
    }

    private static AtsExportedValueInfo ExportedValue(IReadOnlyList<string> pathSegments, AtsTypeRef type, JsonNode value) => new()
    {
        OwningAssemblyName = "Tests",
        PathSegments = pathSegments,
        Type = type,
        Value = value
    };

    private static AtsTypeRef Primitive(string typeId) => new() { TypeId = typeId, Category = AtsTypeCategory.Primitive };

    private static AtsTypeRef Handle(string typeId) => new() { TypeId = typeId, Category = AtsTypeCategory.Handle };

    private static AtsParameterInfo Parameter(string name, AtsTypeRef type, string? summary = null, bool isOptional = false) => new()
    {
        Name = name,
        Type = type,
        IsOptional = isOptional,
        Documentation = summary is null ? null : new AtsDocumentationInfo { Summary = summary }
    };

    private static AtsParameterInfo Callback(string name, AtsTypeRef type, IReadOnlyList<AtsCallbackParameterInfo> parameters, AtsTypeRef returnType) => new()
    {
        Name = name,
        Type = type,
        IsCallback = true,
        CallbackParameters = parameters,
        CallbackReturnType = returnType
    };

    private static AtsCapabilityInfo Capability(
        string capabilityId,
        string methodName,
        AtsTypeRef returnType,
        AtsDocumentationInfo? documentation = null,
        IReadOnlyList<AtsParameterInfo>? parameters = null,
        AtsCapabilityKind kind = AtsCapabilityKind.Method,
        string? description = null,
        bool isObsolete = false,
        string? obsoleteMessage = null,
        bool isExperimental = false,
        string targetParameterName = "builder")
    {
        var resourceType = Handle(ResourceTypeId);
        return new AtsCapabilityInfo
        {
            CapabilityId = capabilityId,
            MethodName = methodName,
            Description = description,
            Documentation = documentation,
            IsObsolete = isObsolete,
            ObsoleteMessage = obsoleteMessage,
            IsExperimental = isExperimental,
            Parameters = [Parameter(targetParameterName, resourceType), .. parameters ?? []],
            ReturnType = returnType,
            TargetTypeId = ResourceTypeId,
            TargetType = resourceType,
            TargetParameterName = targetParameterName,
            ExpandedTargetTypes = [resourceType],
            ReturnsBuilder = returnType.TypeId == ResourceTypeId,
            CapabilityKind = kind
        };
    }
}
