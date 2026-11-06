// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;
using Aspire.Hosting.RemoteHost;
using Aspire.Shared.CodeGeneration;
using Aspire.TestUtilities;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class GoDocCommentTests
{
    private const string ResourceTypeId = "Tests/Tests.DocumentedResource";
    private const string MarkerTypeId = "Tests/Tests.IDocumentedMarker";
    private const string SettingsTypeId = "Tests/Tests.DocumentedSettings";
    private const string StatusTypeId = "Tests/Tests.DocumentedStatus";

    // The documentation grammar the scanner produces: one line per summary, ats-ref placeholders, generic arity
    // markers, code fences and "- " list items. See AtsCapabilityScanner.
    private const string RichRemarks =
        "Usage\n```\nresource.WithDocumentation(\"docs\")\n```\nThe `name` and `settings` arguments are required, so pass `null` for neither. " +
        "See {@ats-ref type:Tests.DocumentedSettings|DocumentedSettings}, `IResourceBuilder`1` and " +
        "{@ats-ref method:DocumentedResource.withDocumentation|WithDocumentation}.\n- name: Must be unique.\n- port: Defaults to 8080.";

    public enum DocumentedStatus
    {
        Pending,
        Running,
        Retired,
        Unknown
    }

    [Fact]
    public async Task GenerateDistributedApplication_WithDocumentedMetadata_EmitsGodoc()
    {
        var files = new AtsGoCodeGenerator().GenerateDistributedApplication(CreateDocumentedContext());

        await Verify(DeclarationsOnly(files["aspire.go"]), extension: "go")
            .UseFileName("DocumentedMetadataGeneratedAspire");
    }

    [Fact]
    public async Task Render_FormatsGodocBlocks()
    {
        var docs = CreateDocComment();
        var cases = new (string Label, GoDocContent Content)[]
        {
            ("method summary is reworded to start with the name", new GoDocContent { GoName = "AddRedis", Summary = "Adds a Redis container resource." }),
            ("summary that already starts with the name is kept", new GoDocContent { GoName = "AddRedis", Summary = "AddRedis adds a Redis container resource." }),
            ("getter", new GoDocContent { GoName = "DisplayName", Subject = GoDocSubject.Getter, Summary = "Gets or sets the display name." }),
            ("setter", new GoDocContent { GoName = "SetDisplayName", Subject = GoDocSubject.Setter, Summary = "Gets or sets the display name." }),
            ("boolean getter", new GoDocContent { GoName = "IsEnabled", Subject = GoDocSubject.Getter, Summary = "Gets or sets a value indicating whether the resource is enabled." }),
            ("setter falls back to the description when the property text cannot be reworded", new GoDocContent { GoName = "SetPort", Subject = GoDocSubject.Setter, Summary = "The port.", FallbackSummary = "Sets the Port property" }),
            ("field with a noun phrase", new GoDocContent { GoName = "Port", Subject = GoDocSubject.Field, Summary = "The port to listen on." }),
            ("field with a sentence", new GoDocContent { GoName = "Pending", Subject = GoDocSubject.Field, Summary = "The resource is pending." }),
            ("field with whether", new GoDocContent { GoName = "UseTls", Subject = GoDocSubject.Field, Summary = "Whether TLS is used." }),
            ("type without a verb", new GoDocContent { GoName = "Settings", Subject = GoDocSubject.Type, Summary = "Test settings." }),
            ("fallback summary", new GoDocContent { GoName = "Settings", Subject = GoDocSubject.Type, FallbackSummary = "Settings represents Settings." }),
            ("nothing to document", new GoDocContent { GoName = "Settings", Subject = GoDocSubject.Type }),
            ("remarks, notes, parameters and returns", new GoDocContent
            {
                GoName = "WithDocumentation",
                Summary = "Adds documentation.",
                Remarks = RichRemarks,
                Notes = ["Only one of the values may be set."],
                Parameters = [new GoDocParameter("name", "name", "The name."), new GoDocParameter("settings", "settings", null), new GoDocParameter("port", "options", "Optional parameters; see [WithDocumentationOptions].")],
                Returns = "The builder."
            }),
            ("experimental", new GoDocContent { GoName = "WithPreview", Summary = "Adds a preview.", IsExperimental = true }),
            ("obsolete with a message", new GoDocContent { GoName = "WithLegacy", Summary = "Adds the legacy behavior.", IsObsolete = true, ObsoleteMessage = "Use WithPreview instead.\nIt replaces this method." }),
            ("obsolete without a message", new GoDocContent { GoName = "WithLegacy", Summary = "Adds the legacy behavior.", IsObsolete = true }),
            ("experimental and obsolete", new GoDocContent { GoName = "WithLegacy", Summary = "Adds the legacy behavior.", IsExperimental = true, IsObsolete = true, ObsoleteMessage = "Use WithPreview." }),
            ("long text wraps without splitting words or starting a list", new GoDocContent
            {
                GoName = "WithLongText",
                Summary = "Adds a resource whose documentation is long enough that it has to wrap across several comment lines - the next dash must not start a list item - or a heading # either.",
                Parameters = [new GoDocParameter("value", "value", "A parameter whose description is also long enough that it has to wrap onto a continuation line of the list.")]
            }),
            ("a remark that looks like a heading stays a paragraph", new GoDocContent { GoName = "WithHeading", Summary = "Adds a heading.", Remarks = "Overview\n- first item" }),
            ("special characters are kept", new GoDocContent { GoName = "WithSpecialCharacters", Summary = "Adds <special> characters & \"quotes\", a backslash \\ and a */ terminator. Unicode: 你好こんにちは." }),
        };

        var text = string.Join("\n\n", cases.Select(c => $"## {c.Label}\n{string.Join('\n', docs.Render(c.Content))}"));

        await Verify(text, extension: "txt").UseFileName("GoDocCommentRender");
    }

    [Theory]
    [InlineData("Adds Foo.\nfunc init() {}\n// ")]
    [InlineData("Adds Foo.\r\nfunc init() {}\r\n")]
    [InlineData("Adds Foo.\rfunc init() {}")]
    [InlineData("Adds Foo.\u2028func init() {}\u2029")]
    [InlineData("Adds Foo.\0func init() {}\uFEFF")]
    public void Render_KeepsEveryLineInsideTheComment(string text)
    {
        var lines = CreateDocComment().Render(new GoDocContent
        {
            GoName = "AddFoo",
            Summary = text,
            FallbackSummary = text,
            Remarks = text,
            Notes = [text],
            Parameters = [new GoDocParameter("foo", "foo", text)],
            Returns = text,
            IsObsolete = true,
            ObsoleteMessage = text
        });

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(@"^//(?:[ \t][^\r\n\u0000\u2028\u2029\uFEFF]*)?\z", line));
    }

    // A period followed by a non-space and a possessive 's are both allowed in a heading, so gofmt turns these
    // one-line paragraphs into "# Heading" unless they end with a period.
    [Theory]
    [InlineData("Requires Redis 7.2")]
    [InlineData("Used with the resource's name")]
    public void Render_EndsHeadingLookingRemarksWithAPeriod(string remarks)
    {
        var lines = CreateDocComment().Render(new GoDocContent
        {
            GoName = "WithThing",
            Summary = "Adds a thing.",
            Remarks = remarks,
            Returns = "The builder."
        });

        Assert.Equal(
            [
                "// WithThing adds a thing.",
                "//",
                $"// {remarks}.",
                "//",
                "// Returns: The builder."
            ],
            lines);
    }

    [Fact]
    public void GeneratedSdk_DocCommentsFollowGodocConventions()
    {
        var context = CreateRealSdkContext();
        var sdk = new AtsGoCodeGenerator().GenerateDistributedApplication(context)["aspire.go"];

        var violations = new List<string>();
        var commentedDeclarations = 0;

        // Generated files use the platform line ending, so a lone CR would be a real defect rather than a CRLF pair.
        if (Regex.IsMatch(sdk, "\r(?!\n)"))
        {
            violations.Add("The SDK contains a carriage return that does not belong to a CRLF pair.");
        }

        var lines = sdk.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsComment(line))
            {
                if (GetDeclaredName(line) is { } name && i > 0 && IsComment(lines[i - 1]))
                {
                    var first = i - 1;
                    while (first > 0 && IsComment(lines[first - 1]))
                    {
                        first--;
                    }

                    commentedDeclarations++;
                    // go doc, gopls hover and staticcheck all expect the comment to start with the identifier.
                    if (!Regex.IsMatch(lines[first].Trim(), $"^// {Regex.Escape(name)}(?:[ ,.:]|$)"))
                    {
                        violations.Add($"{first + 1}: the comment for '{name}' must start with its name: {lines[first].Trim()}");
                    }
                }

                continue;
            }

            if (line.EndsWith(' ') || line.EndsWith('\t'))
            {
                violations.Add($"{i + 1}: trailing whitespace: {line}");
            }

            // gopls and staticcheck only recognize Deprecated: at the start of a paragraph.
            if (line.TrimStart().StartsWith("// Deprecated:", StringComparison.Ordinal) && i > 0 && IsComment(lines[i - 1]) && lines[i - 1].Trim() != "//")
            {
                violations.Add($"{i + 1}: Deprecated: must start its own paragraph: {line}");
            }
        }

        // Metadata must reach the output: if the scanner reports an obsolete or experimental capability, the SDK says so.
        if (context.Capabilities.Any(c => c.IsObsolete))
        {
            Assert.Contains("// Deprecated: ", sdk);
        }

        if (context.Capabilities.Any(AtsTypeSystemCompatibility.IsExperimental))
        {
            Assert.Contains("// # Experimental", sdk);
        }

        // Every documented enum value is written as a commented constant, and no other constant is commented.
        var documentedValues = context.EnumTypes.SelectMany(e => e.ValueInfos).Count(v => !string.IsNullOrWhiteSpace(v.Documentation?.Summary));
        var commentedConstants = lines.Where((line, i) => i > 0 && IsComment(lines[i - 1]) && Regex.IsMatch(line, "^\t\\w+ \\w+ = \"[^\"]*\"$")).Count();
        Assert.Equal(documentedValues, commentedConstants);

        Assert.NotEqual(0, commentedDeclarations);
        Assert.Empty(violations);

        static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["go", "gofmt"])]
    public async Task GeneratedSdk_PassesGoToolchainChecks()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var files = new AtsGoCodeGenerator().GenerateDistributedApplication(CreateRealSdkContext());
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            var build = await RunAsync("go", directory.FullName, timeout.Token, "build", "./...");
            Assert.True(build.ExitCode == 0, $"go build failed:{Environment.NewLine}{build.Output}");

            var vet = await RunAsync("go", directory.FullName, timeout.Token, "vet", "./...");
            Assert.True(vet.ExitCode == 0, $"go vet failed:{Environment.NewLine}{vet.Output}");

            // gofmt rewrites a doc comment that is not in canonical form, so an unchanged comment line proves the
            // generated comment already is. go/printer only reformats doc comments that start in column 1, so this
            // covers top-level declarations. Tab-indented interface-method and struct-field comments are never
            // reformatted; Render_EndsHeadingLookingRemarksWithAPeriod covers the heading rule they depend on.
            // The code itself is not gofmt-formatted, only the comments are checked.
            var formatted = await RunAsync("gofmt", directory.FullName, timeout.Token, "aspire.go");
            Assert.True(formatted.ExitCode == 0, $"gofmt failed:{Environment.NewLine}{formatted.Output}");

            var generated = CommentLines(files["aspire.go"]);
            var reformatted = CommentLines(formatted.Output);
            for (var i = 0; i < Math.Min(generated.Count, reformatted.Count); i++)
            {
                Assert.True(generated[i] == reformatted[i], $"gofmt rewrote comment line {i + 1}:{Environment.NewLine}  generated: {generated[i]}{Environment.NewLine}  gofmt:     {reformatted[i]}");
            }

            Assert.Equal(generated.Count, reformatted.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static GoDocComment CreateDocComment()
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DocumentedSettings"] = "DocumentedSettings",
            ["WithDocumentationOptions"] = "WithDocumentationOptions"
        };

        return new GoDocComment(name => types.GetValueOrDefault(name), name => char.ToUpperInvariant(name[0]) + name[1..]);
    }

    private static AtsContext CreateRealSdkContext()
    {
        return AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(TestRedisResource).Assembly]).ToAtsContext();
    }

    private static string? GetDeclaredName(string line)
    {
        // Top-level declarations (type, var, func and methods) and tab-indented members (interface methods, struct
        // fields and constants). Only exported names are documented.
        var match = Regex.Match(line, @"^(?:type |var |const |func (?:\([^)]*\) )?|\t)(?<name>[A-Z]\w*)\b");
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static List<string> CommentLines(string source)
    {
        return source.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("//", StringComparison.Ordinal)).ToList();
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // gofmt prints the formatted source to stdout and diagnostics to stderr; go prints diagnostics to stderr.
        return (process.ExitCode, await stdout + await stderr);
    }

    // The declarations are what go doc and gopls show, so the snapshot keeps the comments and signatures and drops
    // function bodies, which belong to the capability-emission tests.
    private static string DeclarationsOnly(string sdk)
    {
        var lines = sdk.ReplaceLineEndings("\n").Split('\n');
        var start = Array.IndexOf(lines, "// Enums") - 1;
        var end = Array.IndexOf(lines, "// Per-client handle wrapper registration") - 1;

        var result = new List<string>();
        var inBody = false;
        foreach (var line in lines[start..end])
        {
            if (inBody)
            {
                inBody = line != "}";
                continue;
            }

            if (line.StartsWith("func ", StringComparison.Ordinal) && line.EndsWith('{'))
            {
                result.Add($"{line} ... }}");
                inBody = true;
                continue;
            }

            result.Add(line);
        }

        return string.Join('\n', result);
    }

    private static AtsContext CreateDocumentedContext()
    {
        var stringType = Primitive(AtsConstants.String);
        var boolType = Primitive(AtsConstants.Boolean);
        var voidType = Primitive(AtsConstants.Void);
        var resourceType = new AtsTypeRef { TypeId = ResourceTypeId, Category = AtsTypeCategory.Handle };
        var settingsType = new AtsTypeRef { TypeId = SettingsTypeId, Category = AtsTypeCategory.Dto };
        var statusType = new AtsTypeRef { TypeId = StatusTypeId, Category = AtsTypeCategory.Enum };
        var tagsType = new AtsTypeRef { TypeId = "Tests/List<string>", Category = AtsTypeCategory.List, ElementType = stringType };

        return new AtsContext
        {
            Capabilities =
            [
                Capability("withDocumentation", resourceType, new AtsDocumentationInfo { Summary = "Adds documentation to the resource.", Remarks = RichRemarks, Returns = "The resource builder." },
                [
                    Parameter("name", stringType, "The name of the documentation."),
                    Parameter("settings", settingsType, "The settings to apply, or `null` to keep the defaults."),
                    Parameter("port", Primitive(AtsConstants.Number), "The port to use. Pass `name` to override.", isOptional: true),
                    Parameter("status", statusType, "The initial status.", isOptional: true),
                    Parameter("enabled", boolType, isOptional: true)
                ]),
                Capability("withObsoleteDocumentation", resourceType, new AtsDocumentationInfo { Summary = "Adds documentation using the legacy convention." },
                    [Parameter("name", stringType)], isObsolete: true, obsoleteMessage: "Use WithDocumentation instead."),
                Capability("withLegacyDocumentation", resourceType, description: "Adds the legacy documentation.", isObsolete: true),
                Capability("withPreviewDocumentation", resourceType, new AtsDocumentationInfo { Summary = "Adds preview documentation." }, isExperimental: true),
                Capability("withPreviewStatus", resourceType, new AtsDocumentationInfo { Summary = "Sets the preview status." },
                    [Parameter("status", statusType, "The status to set.")], isExperimental: true, isObsolete: true, obsoleteMessage: "Use WithDocumentation instead."),
                Capability("withDirectOptions", resourceType, new AtsDocumentationInfo { Summary = "Applies the settings." },
                    [Parameter("options", settingsType, "The settings to apply.", isOptional: true)]),
                Capability("withHeading", resourceType, new AtsDocumentationInfo { Summary = "Adds a heading.", Remarks = "Overview\n- first item" }),
                Capability("withSpecialText", resourceType, new AtsDocumentationInfo
                {
                    Summary = "Handles <special> characters & line endings.\r\nThe second line follows a CRLF.\rThe third line follows a CR.",
                    Remarks = "A remark that is long enough to be wrapped across several comment lines without splitting a word, and that keeps going for a while so the wrapping is visible."
                }),
                Capability("displayName", stringType, new AtsDocumentationInfo { Summary = "Gets or sets the display name." },
                    kind: AtsCapabilityKind.PropertyGetter, description: "Gets the DisplayName property"),
                Capability("setDisplayName", resourceType, new AtsDocumentationInfo { Summary = "Gets or sets the display name." },
                    [Parameter("value", stringType, "Gets or sets the display name.")], AtsCapabilityKind.PropertySetter, "Sets the DisplayName property"),
                Capability("isEnabled", boolType, new AtsDocumentationInfo { Summary = "Gets or sets a value indicating whether the resource is enabled." },
                    kind: AtsCapabilityKind.PropertyGetter, description: "Gets the IsEnabled property"),
                Capability("port", Primitive(AtsConstants.Number), new AtsDocumentationInfo { Summary = "The port of the resource." },
                    kind: AtsCapabilityKind.PropertyGetter, description: "Gets the Port property"),
                Capability("tags", tagsType, new AtsDocumentationInfo { Summary = "Gets the tags of the resource." }, kind: AtsCapabilityKind.PropertyGetter),
                Capability("getStatus", stringType, new AtsDocumentationInfo { Summary = "Gets the status of the resource.", Returns = "The status of the resource." }, kind: AtsCapabilityKind.InstanceMethod),
                Capability("reset", voidType, new AtsDocumentationInfo { Summary = "Resets the resource to its defaults." },
                    [Parameter("force", boolType, "Whether to discard pending changes.")], AtsCapabilityKind.InstanceMethod),
                Capability("undocumented", voidType, kind: AtsCapabilityKind.InstanceMethod)
            ],
            HandleTypes =
            [
                new AtsTypeInfo
                {
                    AtsTypeId = ResourceTypeId,
                    Documentation = new AtsDocumentationInfo { Summary = "Represents a documented resource.", Remarks = "Create one with the builder." }
                },
                new AtsTypeInfo
                {
                    AtsTypeId = MarkerTypeId,
                    IsInterface = true,
                    Documentation = new AtsDocumentationInfo { Summary = "Marks resources that carry documentation." }
                }
            ],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    TypeId = SettingsTypeId,
                    Name = "DocumentedSettings",
                    Documentation = new AtsDocumentationInfo { Summary = "Represents the settings of a documented resource.", Remarks = "Settings are applied when the resource starts." },
                    Properties =
                    [
                        new AtsDtoPropertyInfo { Name = "DisplayName", Type = stringType, Documentation = new AtsDocumentationInfo { Summary = "Gets or sets the display name." } },
                        new AtsDtoPropertyInfo { Name = "Enabled", Type = boolType, Documentation = new AtsDocumentationInfo { Summary = "Gets or sets a value indicating whether the resource is enabled." } },
                        new AtsDtoPropertyInfo { Name = "Port", Type = Primitive(AtsConstants.Number), Description = "The port of the resource." },
                        new AtsDtoPropertyInfo { Name = "Undocumented", Type = stringType }
                    ]
                }
            ],
            EnumTypes =
            [
                new AtsEnumTypeInfo
                {
                    TypeId = StatusTypeId,
                    Name = "DocumentedStatus",
                    ClrType = typeof(DocumentedStatus),
                    Values = [nameof(DocumentedStatus.Pending), nameof(DocumentedStatus.Running), nameof(DocumentedStatus.Retired), nameof(DocumentedStatus.Unknown)],
                    Documentation = new AtsDocumentationInfo { Summary = "Describes the lifecycle state of a documented resource." },
                    ValueInfos =
                    [
                        new AtsEnumValueInfo { Name = nameof(DocumentedStatus.Pending), Documentation = new AtsDocumentationInfo { Summary = "The resource is pending." } },
                        new AtsEnumValueInfo { Name = nameof(DocumentedStatus.Running), Documentation = new AtsDocumentationInfo { Summary = "Indicates that the resource is running.", Remarks = "It accepts requests." } },
                        new AtsEnumValueInfo { Name = nameof(DocumentedStatus.Retired), Documentation = new AtsDocumentationInfo { Summary = "A resource that was retired." } }
                    ]
                }
            ],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "Tests",
                    PathSegments = ["DocumentedDefaults", "Primary"],
                    Type = settingsType,
                    Value = JsonNode.Parse("""{"DisplayName":"primary","Enabled":true,"Port":8080}"""),
                    Documentation = new AtsDocumentationInfo { Summary = "The primary settings." }
                },
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "Tests",
                    PathSegments = ["DocumentedDefaults", "Secondary"],
                    Type = settingsType,
                    Value = JsonNode.Parse("""{"DisplayName":"secondary","Enabled":false,"Port":8081}"""),
                    Description = "The secondary settings."
                }
            ]
        };
    }

    private static AtsTypeRef Primitive(string typeId) => new() { TypeId = typeId, Category = AtsTypeCategory.Primitive };

    private static AtsParameterInfo Parameter(string name, AtsTypeRef type, string? summary = null, bool isOptional = false) => new()
    {
        Name = name,
        Type = type,
        IsOptional = isOptional,
        Documentation = summary is null ? null : new AtsDocumentationInfo { Summary = summary }
    };

    private static AtsCapabilityInfo Capability(
        string name,
        AtsTypeRef returnType,
        AtsDocumentationInfo? documentation = null,
        IReadOnlyList<AtsParameterInfo>? parameters = null,
        AtsCapabilityKind kind = AtsCapabilityKind.Method,
        string? description = null,
        bool isObsolete = false,
        string? obsoleteMessage = null,
        bool isExperimental = false)
    {
        var resourceType = new AtsTypeRef { TypeId = ResourceTypeId, Category = AtsTypeCategory.Handle };
        return new AtsCapabilityInfo
        {
            CapabilityId = $"Tests/{name}",
            MethodName = name,
            Description = description,
            Documentation = documentation,
            IsObsolete = isObsolete,
            ObsoleteMessage = obsoleteMessage,
            IsExperimental = isExperimental,
            Parameters = [Parameter("builder", resourceType), .. parameters ?? []],
            ReturnType = returnType,
            TargetTypeId = ResourceTypeId,
            TargetType = resourceType,
            TargetParameterName = "builder",
            ExpandedTargetTypes = [resourceType],
            ReturnsBuilder = returnType.TypeId == ResourceTypeId,
            CapabilityKind = kind
        };
    }
}
