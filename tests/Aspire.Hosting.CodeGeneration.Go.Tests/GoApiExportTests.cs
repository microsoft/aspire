// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class GoApiExportTests
{
    // The test types are compiled into this assembly via Compile Include.
    private const string TestTypesAssemblyName = "Aspire.Hosting.CodeGeneration.Go.Tests";
    private const string HostingAssemblyName = "Aspire.Hosting";
    private const string PackageVersion = "13.5.0";
    private const string InputCollectionTypeId = "Aspire.Hosting/Aspire.Hosting.InteractionInputCollection";

    private static readonly Lazy<AtsContext> s_context = new(CreateContext);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_generated = new(GenerateSdk);
    private static readonly Lazy<IReadOnlyList<GoDeclaration>> s_generatedDeclarations = new(ScanGeneratedSdk);
    private static readonly Lazy<GoApiModel> s_everything = new(ExportEverything);

    [Fact]
    public async Task ApiExportWriter_WithGoSurface_ProducesFocusedCanonicalJson()
    {
        var json = GoApiExportWriter.WriteToJson(CreateFocusedModel(), indented: true);

        await Verify(json, extension: "json")
            .UseFileName("GoApiExportTests.FocusedApiExport");
    }

    [Fact]
    public void ApiExportWriter_WithCrLfDeclaration_NormalizesLineEndings()
    {
        var json = GoApiExportWriter.WriteToJson(CreateFocusedModel());

        var declaration = Assert.Single(JsonNode.Parse(json)!["declarations"]!.AsArray());
        Assert.Equal("type RedisResource interface {\n\thandleReference\n}", (string?)declaration!["content"]);
    }

    [Fact]
    public async Task ApiExport_WithHosting_ProjectsGoOnlySurface()
    {
        var model = s_everything.Value;
        var inputCollection = Assert.Single(
            model.Modules.Single().Items,
            item => item.TypeId == InputCollectionTypeId);

        var json = RenderFocused(
            model,
            ("function:CreateBuilder", null),
            ("interface:DistributedApplicationBuilder", ["AddTestRedis", "Build", "Err"]),
            ("interface:DistributedApplication", ["Err"]),
            (inputCollection.Id, ["Get", "Required", "Value", "RequiredValue", "Err"]),
            ("interface:TestRedisResource", ["GetStatusAsync", "GetTags", "WithPersistence", "WithUnionDependency", "WithValidator", "Err"]),
            ("options:WithPersistenceOptions", null),
            ("dto:TestConfigDto", null),
            ("enum:TestResourceStatus", null),
            ("variable:TestConfigs", null),
            ("type:AtsErrorCode", null));

        await Verify(json, extension: "json")
            .UseFileName("GoApiExportTests.GoOnlySurface");
    }

    [Fact]
    public async Task ApiExport_ForPackageThatAugmentsHosting_ListsOnlyItsOwnMembers()
    {
        var model = BuildModel(TestTypesAssemblyName);

        var json = RenderFocused(
            model,
            ($"augmentation:{TestTypesAssemblyName}:DistributedApplicationBuilder", null),
            ("interface:TestVaultResource", null));

        await Verify(json, extension: "json")
            .UseFileName("GoApiExportTests.AugmentingPackage");
    }

    [Fact]
    public void ApiExport_WritesEnvelopeAndGeneratorIdentity()
    {
        var export = JsonNode.Parse(GoApiExportWriter.WriteToJson(BuildModel(TestTypesAssemblyName)))!;
        var assembly = typeof(AtsGoCodeGenerator).Assembly;

        Assert.Equal(1, (int?)export["schemaVersion"]);
        Assert.Equal("go", (string?)export["language"]);
        Assert.Equal(assembly.GetName().Name, (string?)export["generator"]!["name"]);
        Assert.False(string.IsNullOrEmpty((string?)export["generator"]!["version"]));
        Assert.Equal(TestTypesAssemblyName, (string?)export["package"]!["name"]);
        Assert.Equal(PackageVersion, (string?)export["package"]!["version"]);
        Assert.Equal("apphost/modules/aspire", (string?)export["package"]!["importPath"]);

        var module = Assert.Single(export["modules"]!.AsArray());
        Assert.Equal(TestTypesAssemblyName, (string?)module!["name"]);
        Assert.NotEmpty(module["items"]!.AsArray());
        Assert.NotEmpty(export["declarations"]!.AsArray());
    }

    [Fact]
    public void ApiExport_WithOnlyTestAssemblyOwned_AugmentsHostingTypesAndDeclaresThemOpaquely()
    {
        var model = BuildModel(TestTypesAssemblyName);
        var items = model.Modules.Single().Items;

        var builder = Assert.Single(items, item => item.Kind == GoApiItemKind.Augmentation && item.Name == "DistributedApplicationBuilder");
        Assert.Equal($"augmentation:{TestTypesAssemblyName}:DistributedApplicationBuilder", builder.Id);
        Assert.Equal(HostingAssemblyName, builder.OwningAssemblyName);
        Assert.All(builder.Members, member => Assert.Equal(TestTypesAssemblyName, member.OwningAssemblyName));
        Assert.All(
            items.Where(item => item.Kind != GoApiItemKind.Augmentation),
            item => Assert.Equal(TestTypesAssemblyName, item.OwningAssemblyName));

        // The builder entry point and the hand-written runtime belong to Aspire.Hosting, so the only kinds
        // a package that contributes handle members, options and values documents are these.
        Assert.Equal(
            [GoApiItemKind.Interface, GoApiItemKind.Augmentation, GoApiItemKind.Dto, GoApiItemKind.Options, GoApiItemKind.Enum, GoApiItemKind.Variable],
            items.Select(item => item.Kind).Distinct().Order());
        Assert.Contains(model.Declarations, declaration => declaration.Id == $"{HostingAssemblyName}:opaque:DistributedApplicationBuilder");
        Assert.Contains(model.Declarations, declaration => declaration.Id == "aspire:runtime:base");
    }

    [Fact]
    public void ApiExport_WithOnlyHostingOwned_ExcludesTestAssemblyMembers()
    {
        var model = BuildModel(HostingAssemblyName);
        var items = model.Modules.Single().Items;

        Assert.Contains(items, item => item.Id == "function:CreateBuilder");

        // The test assembly's handle types only appear as augmentations that carry Aspire.Hosting's members.
        Assert.All(
            items.Where(item => item.Kind != GoApiItemKind.Augmentation),
            item => Assert.NotEqual(TestTypesAssemblyName, item.OwningAssemblyName));
        Assert.All(
            items.SelectMany(item => item.Members).Where(member => member.CapabilityId is not null),
            member => Assert.Equal(HostingAssemblyName, member.OwningAssemblyName));

        var builder = Assert.Single(items, item => item.Id == "interface:DistributedApplicationBuilder");
        Assert.Single(builder.Members, member => member.Name == "Build");
    }

    [Fact]
    public void ApiExport_WithHosting_IncludesGoOnlyMembers()
    {
        var items = s_everything.Value.Modules.Single().Items;

        var builder = Assert.Single(items, item => item.Id == "interface:DistributedApplicationBuilder");
        var build = Assert.Single(builder.Members, member => member.Name == "Build");
        Assert.Equal("Build() (DistributedApplication, error)", build.Declaration);
        Assert.Equal("(DistributedApplication, error)", build.ReturnType);
        Assert.Equal(GoApiErrorModel.Returned, build.ErrorModel);
        Assert.True(build.IsSynthesized);

        var inputCollection = Assert.Single(items, item => item.TypeId == InputCollectionTypeId);
        Assert.Equal(
            [
                "Get(name string) (*InteractionInput, error)",
                "Required(name string) (*InteractionInput, error)",
                "Value(name string) (string, error)",
                "RequiredValue(name string) (string, error)",
                "Err() error"
            ],
            inputCollection.Members.Where(member => member.IsSynthesized).Select(member => member.Declaration));

        // Err comes from the runtime's handleReference contract, so every handle interface has it, marker
        // interfaces included.
        Assert.All(
            items.Where(item => item.Kind == GoApiItemKind.Interface),
            item => Assert.Single(item.Members, member => member.Declaration == "Err() error" && member.IsSynthesized));

        // ToMap is written on every struct the generator declares.
        Assert.All(
            items.Where(item => item.Kind is GoApiItemKind.Dto or GoApiItemKind.Options),
            item => Assert.Single(item.Members, member => member.Declaration == "ToMap() map[string]any" && member.IsSynthesized));
    }

    [Fact]
    public void ApiExport_WithOptionalParameters_FlattensThemIntoTrailingOptions()
    {
        var items = s_everything.Value.Modules.Single().Items;

        var persistence = FindMember(items, "TestRedisResource", "WithPersistence");
        Assert.Equal("WithPersistence(options ...*WithPersistenceOptions) TestRedisResource", persistence.Declaration);
        Assert.Empty(persistence.Parameters);
        Assert.Equal(GoApiErrorModel.Deferred, persistence.ErrorModel);
        Assert.True(persistence.ReturnsReceiver);
        var options = Assert.IsType<GoApiOptions>(persistence.Options);
        Assert.Equal("WithPersistenceOptions", options.TypeName);
        Assert.False(options.IsDirect);
        var mode = Assert.Single(options.Fields);
        Assert.Equal(("Mode", "*TestPersistenceMode", "mode"), (mode.Name, mode.DeclaredType, mode.JsonName));
        Assert.True(mode.IsOptional);

        // A lone optional DTO named options is passed through as the variadic itself.
        var httpCommand = FindMember(items, "ContainerResource", "WithHttpCommand");
        Assert.Equal("WithHttpCommand(path string, displayName string, options ...*HttpCommandExportOptions) ContainerResource", httpCommand.Declaration);
        Assert.Equal(("HttpCommandExportOptions", true), (httpCommand.Options!.TypeName, httpCommand.Options.IsDirect));
        Assert.Empty(httpCommand.Options.Fields);

        // Go has one trailing variadic, so an optional DTO next to another optional keeps its options struct.
        var prompt = FindMember(items, "InteractionService", "PromptInput");
        Assert.Equal("PromptInputOptions", prompt.Options!.TypeName);
        Assert.False(prompt.Options.IsDirect);
        Assert.Contains(prompt.Options.Fields, field => field is { Name: "Options", DeclaredType: "*InteractionInputsDialogOptions", JsonName: "options" });
    }

    [Fact]
    public void ApiExport_WithCallbacksAndUnions_ProjectsGoTypes()
    {
        var items = s_everything.Value.Modules.Single().Items;

        var validator = Assert.Single(FindMember(items, "TestRedisResource", "WithValidator").Parameters);
        Assert.Equal(("validator", "func(arg TestResourceContext) bool", true), (validator.Name, validator.DeclaredType, validator.IsCallback));

        var optionalCallback = FindMember(items, "TestRedisResource", "WithOptionalCallback");
        Assert.All(optionalCallback.Options!.Fields, field => Assert.True(field.IsCallback));
        Assert.All(optionalCallback.Options.Fields, field => Assert.Null(field.JsonName));

        var union = Assert.Single(FindMember(items, "TestRedisResource", "WithUnionDependency").Parameters);
        Assert.Equal("any", union.DeclaredType);
        Assert.NotEmpty(union.AllowedTypes);

        var getter = FindMember(items, "TestRedisResource", "GetTags");
        Assert.Equal("GetTags() *List[string]", getter.Declaration);
        Assert.Equal(GoApiErrorModel.None, getter.ErrorModel);
    }

    [Fact]
    public void ApiExport_WithHosting_HasUniqueIds()
    {
        var model = s_everything.Value;

        var duplicateMembers = model.Modules.Single().Items
            .SelectMany(item => item.Members
                .GroupBy(member => member.Id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{item.Id}: {group.Key}"))
            .ToList();
        Assert.Empty(duplicateMembers);

        Assert.Equal(model.Modules.Single().Items.Count, model.Modules.Single().Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(model.Declarations.Count, model.Declarations.Select(declaration => declaration.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ApiExport_IsDeterministicAndLineEndingNeutral()
    {
        var first = GoApiExportWriter.WriteToJson(BuildModel(HostingAssemblyName, TestTypesAssemblyName));
        var second = GoApiExportWriter.WriteToJson(BuildModel(TestTypesAssemblyName, HostingAssemblyName));

        Assert.Equal(first, second);

        // The runtime sources can be checked out with CRLF, so every string in the export has to come out
        // with LF endings. Declarations span several lines, which proves there is something to normalize.
        var strings = Strings(JsonNode.Parse(first)).ToList();
        Assert.Contains(strings, text => text.Contains('\n'));
        Assert.All(strings, text => Assert.Equal(text.ReplaceLineEndings("\n"), text));

        static IEnumerable<string> Strings(JsonNode? node) => node switch
        {
            JsonObject json => json.SelectMany(property => Strings(property.Value)),
            JsonArray array => array.SelectMany(Strings),
            JsonValue value when value.TryGetValue<string>(out var text) => [text],
            _ => []
        };
    }

    [Fact]
    public void ApiExport_WithHosting_ExportsEveryGeneratedTypeAndFunction()
    {
        var items = s_everything.Value.Modules.Single().Items;
        var generated = s_generatedDeclarations.Value;

        AssertSameNames(
            generated.Where(declaration => declaration.Kind == GoDeclarationKind.Type).Select(declaration => declaration.Name),
            items.Where(IsTypeItem).Select(item => item.Name));
        AssertSameNames(
            generated.Where(declaration => declaration.Kind == GoDeclarationKind.Function).Select(declaration => declaration.Name),
            items.Where(item => item.Kind == GoApiItemKind.Function).Select(item => item.Name));
    }

    [Fact]
    public void ApiExport_WithHosting_ExportsEveryGeneratedMemberOfExportedTypes()
    {
        var items = s_everything.Value.Modules.Single().Items;
        var typeItems = items.Where(item => IsTypeItem(item) && item.Kind != GoApiItemKind.Interface).ToList();
        var typeNames = typeItems.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);

        // The members of a handle interface are methods on an unexported struct, which the interface parity
        // test covers.
        AssertSameNames(
            s_generatedDeclarations.Value
                .Where(declaration => declaration.Kind != GoDeclarationKind.Type && typeNames.Contains(declaration.Owner))
                .Select(declaration => $"{declaration.Owner}.{declaration.Name}"),
            typeItems.SelectMany(item => item.Members.Select(member => $"{item.Name}.{member.Name}")));
    }

    [Fact]
    public void ApiExport_WithHosting_MatchesGeneratedInterfaceMethods()
    {
        var interfaces = s_everything.Value.Modules.Single().Items.Where(item => item.Kind == GoApiItemKind.Interface).ToList();
        var generatedMethods = ReadGeneratedInterfaceMethods(s_generated.Value["aspire.go"]);

        // Marker interfaces declare no Err line of their own because handleReference requires it, so Err is
        // compared separately.
        var mismatches = new List<string>();
        foreach (var item in interfaces)
        {
            var exported = item.Members.Select(member => member.Declaration).Where(declaration => declaration != "Err() error").ToList();
            if (!generatedMethods.TryGetValue(item.Name, out var generatedLines))
            {
                mismatches.Add($"{item.Name}: not generated");
                continue;
            }

            var generated = generatedLines.Where(declaration => declaration != "Err() error").ToList();
            if (!generated.SequenceEqual(exported))
            {
                var index = Enumerable.Range(0, Math.Max(generated.Count, exported.Count))
                    .First(i => i >= generated.Count || i >= exported.Count || generated[i] != exported[i]);
                mismatches.Add($"{item.Name} @{index}: generated '{generated.ElementAtOrDefault(index)}' vs exported '{exported.ElementAtOrDefault(index)}'");
            }
        }

        Assert.Empty(mismatches);
        Assert.NotEmpty(interfaces);
    }

    [Fact]
    public void ApiExport_WithHosting_ExportsVariablesExactlyAsGenerated()
    {
        var variables = s_everything.Value.Modules.Single().Items.Where(item => item.Kind == GoApiItemKind.Variable).ToList();
        var generated = s_generated.Value["aspire.go"].ReplaceLineEndings("\n");

        // Exported declarations carry no comments (documentation is exported as member summaries), so they
        // are compared with the comment lines of the generated code removed.
        var generatedCode = string.Join('\n', generated.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // Variables are written as `var Name = struct {` at the start of a line.
        var generatedNames = generated.Split('\n')
            .Where(line => line.StartsWith("var ", StringComparison.Ordinal) && line.EndsWith(" = struct {", StringComparison.Ordinal))
            .Select(line => line["var ".Length..line.IndexOf(' ', "var ".Length)])
            .Order(StringComparer.Ordinal);

        Assert.Equal(generatedNames, variables.Select(variable => variable.Name).Order(StringComparer.Ordinal));
        Assert.All(variables, variable => Assert.Contains(variable.Declaration, generatedCode, StringComparison.Ordinal));
        Assert.All(variables, variable => Assert.NotEmpty(variable.Members));
    }

    [Fact]
    public void ApiExport_ThroughTheExportFilter_NamesPackageMembersAsFullGenerationDoes()
    {
        // CodeGenerationService exports the context that AtsContextFilter.FilterForApiExport returns, and
        // the generator derives every Go name from the context it is given. The test assembly declares no
        // name that another assembly in the context also declares, so its types and members have to be
        // named exactly as the full generation names them.
        var filtered = AtsContextFilter.FilterForApiExport(s_context.Value, [TestTypesAssemblyName]);
        var exported = new AtsGoCodeGenerator()
            .BuildApiModel(filtered, TestTypesAssemblyName, PackageVersion, [TestTypesAssemblyName], CancellationToken.None)
            .Modules.Single().Items;

        // The full generation declares one interface per type, with the members of every package. What
        // the package export lists as an augmentation is the package's share of that interface, and a
        // member with no capability behind it (Err, ToMap, a field) belongs to the package that owns its type.
        static bool IsOwnedByPackage(GoApiItem item, GoApiMember member)
            => member.CapabilityId is null
                ? item.OwningAssemblyName == TestTypesAssemblyName
                : member.OwningAssemblyName == TestTypesAssemblyName;

        // PipelineContext and TestCallbackContext take the same cancellation token options, so the generator
        // declares a single SetCancellationTokenOptions for both. The full generation gives it to Aspire.Hosting,
        // which comes first, while the filtered context holds only the package's capabilities, so the export
        // gives it to the package. The name and the fields are the same either way, which is what is compared.
        var full = s_everything.Value.Modules.Single().Items;
        var sharedOptions = Assert.Single(full, item => item.Id == "options:SetCancellationTokenOptions");
        Assert.Equal(HostingAssemblyName, sharedOptions.OwningAssemblyName);
        Assert.Equal(TestTypesAssemblyName, Assert.Single(exported, item => item.Id == sharedOptions.Id).OwningAssemblyName);

        var expected = full
            .Select(item => (Item: item, Members: item.Members.Where(member => IsOwnedByPackage(item, member)).ToList()))
            .Where(owned => owned.Item.OwningAssemblyName == TestTypesAssemblyName || owned.Members.Count > 0)
            .SelectMany(owned => DescribeDeclarations(owned.Item, owned.Members))
            .Concat(DescribeDeclarations(sharedOptions, sharedOptions.Members));
        var actual = exported.SelectMany(item => DescribeDeclarations(item, item.Members));

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

        // Both sides come from the same model, so an export that lists nothing would also be equal.
        Assert.Contains(exported, item => item.Id == "interface:TestRedisResource");
        Assert.Contains(exported, item => item.Kind == GoApiItemKind.Augmentation && item.Members.Any(member => member.Name == "AddTestRedis"));
    }

    [Fact]
    public void ApiReferenceExporter_ExportsGoSchemaForRequestedPackage()
    {
        IApiReferenceExporter exporter = new AtsGoApiReferenceExporter();

        var export = exporter.ExportApi(
            s_context.Value,
            new ApiReferenceExportOptions(TestTypesAssemblyName, PackageVersion, [TestTypesAssemblyName]),
            CancellationToken.None);

        Assert.Equal("Go", exporter.Language);
        Assert.Equal(GoApiExportWriter.WriteToJson(BuildModel(TestTypesAssemblyName)), export.GetRawText());
    }

    [Fact]
    public void ApiReferenceExporter_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IApiReferenceExporter exporter = new AtsGoApiReferenceExporter();

        Assert.Throws<OperationCanceledException>(() => exporter.ExportApi(
            s_context.Value,
            new ApiReferenceExportOptions(TestTypesAssemblyName, PackageVersion, [TestTypesAssemblyName]),
            cancellation.Token));
    }

    [Fact]
    public void ApiReferenceExporter_IsNotTheCodeGenerator()
    {
        Assert.IsNotAssignableFrom<IApiReferenceExporter>(new AtsGoCodeGenerator());
    }

    [Fact]
    public void Scan_ReadsExportedDeclarationsAndDocumentation()
    {
        const string source = """
            package aspire

            // Greeter says hello.
            //
            // Remarks stay on their own lines.
            type Greeter struct {
                // Name is shown.
                Name string `json:"name,omitempty"`
                hidden int
                *embedded
            }

            func (g *Greeter) ToMap() map[string]any {
                return nil
            }

            // Mode selects the mode.
            type Mode string

            const (
                // ModeFast is fast.
                ModeFast Mode = "fast"
                ModeSlow Mode = "slow"
            )

            // NewGreeter creates a greeter.
            func NewGreeter(name string, opts ...*GreeterOptions) (*Greeter, error) {
                return nil, nil
            }

            func (l *List[T]) Count() (int, error) { return 0, nil }

            type Application interface { handleReference }

            type hiddenType struct {
                Visible string
            }

            func hiddenFunc() {}
            """;

        var declarations = GoDeclarationScanner.Scan(source, includeBodyDocs: true);

        Assert.Equal(
            [
                (GoDeclarationKind.Type, "", "Greeter", "type Greeter struct"),
                (GoDeclarationKind.Field, "Greeter", "Name", "Name string"),
                (GoDeclarationKind.Method, "Greeter", "ToMap", "ToMap() map[string]any"),
                (GoDeclarationKind.Type, "", "Mode", "type Mode string"),
                (GoDeclarationKind.Constant, "Mode", "ModeFast", "ModeFast Mode = \"fast\""),
                (GoDeclarationKind.Constant, "Mode", "ModeSlow", "ModeSlow Mode = \"slow\""),
                (GoDeclarationKind.Function, "", "NewGreeter", "func NewGreeter(name string, opts ...*GreeterOptions) (*Greeter, error)"),
                (GoDeclarationKind.Method, "List", "Count", "Count() (int, error)"),
                (GoDeclarationKind.Type, "", "Application", "type Application interface")
            ],
            declarations.Select(declaration => (declaration.Kind, declaration.Owner, declaration.Name, declaration.Signature)));

        var greeter = declarations[0];
        Assert.Equal("Greeter says hello.", greeter.Summary);
        Assert.Equal("Remarks stay on their own lines.", greeter.Remarks);
        Assert.Equal("Name is shown.", declarations[1].Summary);
        Assert.Equal("name,omitempty", declarations[1].Value![(declarations[1].Value!.IndexOf('"') + 1)..declarations[1].Value!.LastIndexOf('"')]);
        Assert.Equal("fast", declarations[4].Value);
        Assert.Equal("ModeFast is fast.", declarations[4].Summary);
        Assert.Equal("*List[T]", declarations[7].Receiver);
        Assert.Equal("NewGreeter creates a greeter.", declarations[6].Summary);
    }

    [Fact]
    public void Scan_WithoutBodyDocs_LeavesFieldAndConstantDocumentationEmpty()
    {
        const string source = """
            type Mode string

            const (
                // Group heading.
                ModeFast Mode = "fast"
            )
            """;

        var constant = Assert.Single(GoDeclarationScanner.Scan(source, includeBodyDocs: false), declaration => declaration.Kind == GoDeclarationKind.Constant);

        Assert.Null(constant.Summary);
    }

    [Fact]
    public void Scan_ReportsExportedDeclarationsWithNonAsciiUppercaseNames()
    {
        // U+00C9 and U+00DC are upper case letters outside ASCII, so Go exports the names they start;
        // U+00E9 is lower case, so the names it starts stay unexported.
        var source = string.Join(
            '\n',
            "type \u00C9tat struct {",
            "\t\u00C9tage string",
            "\t\u00E9tageCache string",
            "}",
            "",
            "func (e *\u00C9tat) \u00C9crire() error { return nil }",
            "",
            "func \u00DCberFunc() {}",
            "",
            "func \u00E9teindre() {}",
            "",
            "type \u00E9tatPrive struct{}");

        var declarations = GoDeclarationScanner.Scan(source, includeBodyDocs: false);

        Assert.Equal(
            [
                (GoDeclarationKind.Type, "", "\u00C9tat", "type \u00C9tat struct"),
                (GoDeclarationKind.Field, "\u00C9tat", "\u00C9tage", "\u00C9tage string"),
                (GoDeclarationKind.Method, "\u00C9tat", "\u00C9crire", "\u00C9crire() error"),
                (GoDeclarationKind.Function, "", "\u00DCberFunc", "func \u00DCberFunc()")
            ],
            declarations.Select(declaration => (declaration.Kind, declaration.Owner, declaration.Name, declaration.Signature)));
    }

    [Fact]
    public void Scan_OfGeneratedRuntime_ReportsTheErrMethodHandlesInherit()
    {
        var declarations = GoDeclarationScanner.Scan(s_generated.Value["base.go"], includeBodyDocs: false);

        var err = Assert.Single(declarations, declaration => declaration is { Kind: GoDeclarationKind.Method, Name: "Err", Owner: "handleWrapperBase" });
        Assert.Equal("Err() error", err.Signature);
        Assert.False(string.IsNullOrEmpty(err.Summary));
    }

    [Theory]
    [InlineData("var Default = newClient()", "var Default = newClient()")]
    [InlineData("var (\n    Default = newClient()\n)", "var (")]
    [InlineData("type (\n    Handle struct{}\n)", "type (")]
    [InlineData("const Version = \"1\"", "const Version = \"1\"")]
    [InlineData("func Run(ctx context.Context,\n    name string) error {\n}", "func Run(ctx context.Context,")]
    [InlineData("func (c *Client) Run(\n    name string,\n) error {\n}", "func (c *Client) Run(")]
    [InlineData("type Handle interface {\n    Err() error\n}", "type Handle interface {")]
    [InlineData("type Handle interface { Err() error }", "type Handle interface { Err() error }")]
    [InlineData("package aspire\r\n\r\nconst Version = \"1\"\r\n", "const Version = \"1\"")]
    public void ScanRuntime_OfAnUnmodeledExportedConstruct_ThrowsNamingTheLine(string source, string line)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GoDeclarationScanner.ScanRuntime(source));

        Assert.Equal($"The Go source declares an exported construct the API export does not model: {line}", exception.Message);
    }

    [Fact]
    public void ScanRuntime_OfModeledAndUnexportedConstructs_ReportsTheModeledOnes()
    {
        const string source = """
            type Mode string

            const (
                ModeFast Mode = "fast"
            )

            var cache = map[string]int{}

            const limit = 3

            type reader interface {
                Read() error
            }

            type Anything interface{}

            func (m Mode) Valid() bool { return true }

            func run(name string,
                count int) {
            }
            """;

        Assert.Equal(
            ["Mode", "ModeFast", "Anything", "Valid"],
            GoDeclarationScanner.ScanRuntime(source).Select(declaration => declaration.Name));
    }

    [Theory]
    [InlineData("Count() (int, error)", "(int, error)")]
    [InlineData("Err() error", "error")]
    [InlineData("Build()", null)]
    [InlineData("WithValidator(validator func(arg TestResourceContext) bool) TestRedisResource", "TestRedisResource")]
    [InlineData("func RefExpr(format string, valueProviders ...any) *ReferenceExpression", "*ReferenceExpression")]
    public void GetReturnType_ReadsTheResultsOfASignature(string signature, string? expected)
    {
        Assert.Equal(expected, GoDeclarationScanner.GetReturnType(signature));
    }

    [Fact]
    public void ReadTypeDeclaration_KeepsTheShapeOfAnUnexportedType()
    {
        var source = string.Join(
            '\n',
            "// handleType is the ATS type ID carried on a handle envelope.",
            "type handleType = string",
            "",
            "// handle represents a reference to a server-side object.",
            "type handle struct {",
            "\tHandleID string     `json:\"$handle\"`",
            "\t// TypeID is the ATS type ID.",
            "\tTypeID   handleType `json:\"$type\"` // Not part of the tag.",
            "}",
            "",
            "type handleReference interface {",
            "\tgetHandle() *handle",
            "\tErr()       error // Reports the deferred failure.",
            "}",
            "",
            "type handleSource interface { handleReference }");

        Assert.Equal("type handleType = string", GoDeclarationScanner.ReadTypeDeclaration(source, "handleType"));
        Assert.Equal(
            string.Join('\n', "type handle struct {", "\tHandleID string `json:\"$handle\"`", "\tTypeID handleType `json:\"$type\"`", "}"),
            GoDeclarationScanner.ReadTypeDeclaration(source, "handle"));
        Assert.Equal(
            string.Join('\n', "type handleReference interface {", "\tgetHandle() *handle", "\tErr() error", "}"),
            GoDeclarationScanner.ReadTypeDeclaration(source, "handleReference"));
        Assert.Equal("type handleSource interface { handleReference }", GoDeclarationScanner.ReadTypeDeclaration(source, "handleSource"));
    }

    [Fact]
    public void ReadTypeDeclaration_OfATypeTheSourceDoesNotDeclare_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => GoDeclarationScanner.ReadTypeDeclaration("type handle struct {\n}", "handleReference"));

        Assert.Contains("handleReference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiExport_WithHosting_DeclaresTheTypesItsFragmentsEmbed()
    {
        AssertDeclarationsAreSelfContained(s_everything.Value);
    }

    [Fact]
    public void ApiExport_ForPackage_DeclaresTheTypesItsFragmentsEmbed()
    {
        AssertDeclarationsAreSelfContained(BuildModel(TestTypesAssemblyName));
    }

    [Fact]
    public void ApiExport_WithHosting_ReportsHowRuntimeMethodsFail()
    {
        var items = s_everything.Value.Modules.Single().Items;
        var methods = items
            .Where(item => item.Kind == GoApiItemKind.Type)
            .SelectMany(item => item.Members)
            .Where(member => member.Kind == GoApiMemberKind.Method)
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.True(method.ErrorModel is not null, $"{method.Id} has no error model."));

        Assert.Equal(GoApiErrorModel.Returned, FindTypeMember(items, "List", "Count").ErrorModel);
        Assert.Equal(GoApiErrorModel.Returned, FindTypeMember(items, "List", "Add").ErrorModel);
        Assert.Equal(GoApiErrorModel.Returned, FindTypeMember(items, "Dict", "Get").ErrorModel);
        Assert.Equal(GoApiErrorModel.Returned, FindTypeMember(items, "ReferenceExpression", "GetValue").ErrorModel);
        Assert.Equal(GoApiErrorModel.None, FindTypeMember(items, "CancellationToken", "IsCancelled").ErrorModel);

        // Err reports a failure that a handle deferred, so, as on a handle interface, calling it cannot fail.
        Assert.Equal(GoApiErrorModel.None, FindTypeMember(items, "ReferenceExpression", "Err").ErrorModel);
    }

    private static AtsContext CreateContext()
        => AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(TestRedisResource).Assembly]).ToAtsContext();

    private static IReadOnlyDictionary<string, string> GenerateSdk()
        => new AtsGoCodeGenerator().GenerateDistributedApplication(s_context.Value);

    private static IReadOnlyList<GoDeclaration> ScanGeneratedSdk()
        => new[] { "transport.go", "base.go", "aspire.go" }
            .SelectMany(file => GoDeclarationScanner.Scan(s_generated.Value[file], includeBodyDocs: false))
            .ToList();

    private static GoApiModel BuildModel(params string[] exportingAssemblyNames)
        => new AtsGoCodeGenerator().BuildApiModel(s_context.Value, TestTypesAssemblyName, PackageVersion, exportingAssemblyNames, CancellationToken.None);

    // The generated SDK holds every type the model references, whoever owns it, so the parity tests export
    // for all of the owners that show up in a first pass.
    private static GoApiModel ExportEverything()
    {
        var first = BuildModel(HostingAssemblyName, TestTypesAssemblyName);
        var owners = first.Declarations.Select(declaration => declaration.OwningAssemblyName)
            .Concat(first.Modules.SelectMany(module => module.Items).Select(item => item.OwningAssemblyName))
            .Append(HostingAssemblyName)
            .Append(TestTypesAssemblyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return BuildModel(owners);
    }

    private static bool IsTypeItem(GoApiItem item)
        => item.Kind is GoApiItemKind.Interface or GoApiItemKind.Dto or GoApiItemKind.Options or GoApiItemKind.Enum or GoApiItemKind.Type;

    private static GoApiMember FindMember(IReadOnlyList<GoApiItem> items, string interfaceName, string memberName)
    {
        var item = Assert.Single(items, item => item.Id == $"interface:{interfaceName}");

        return Assert.Single(item.Members, member => member.Name == memberName);
    }

    private static GoApiMember FindTypeMember(IReadOnlyList<GoApiItem> items, string typeName, string memberName)
    {
        var item = Assert.Single(items, item => item.Id == $"type:{typeName}");

        return Assert.Single(item.Members, member => member.Name == memberName);
    }

    // The declaration of an item and of the given members, each keyed by the Go name the item declares,
    // so an interface and an augmentation of the same type describe alike, whoever owns the type.
    private static IEnumerable<string> DescribeDeclarations(GoApiItem item, IEnumerable<GoApiMember> members)
        => members.Select(member => $"{item.Name}: {member.Declaration}").Prepend($"{item.Name}: {item.Declaration}");

    // The declarations are Go source, so a type that one of them embeds has to be declared by one of them.
    // The generator embeds a type as a bare name on its own line:
    //   type Redis interface {
    //       handleReference
    //       Err() error
    //   }
    // and declares a type with a header at the start of a line, where the name ends at a space or at a
    // type parameter list: `type handle struct {`, `type handleType = string`, `type List[T any] struct {`.
    private static void AssertDeclarationsAreSelfContained(GoApiModel model)
    {
        static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

        var lines = model.Declarations
            .SelectMany(declaration => declaration.Content.ReplaceLineEndings("\n").Split('\n'))
            .ToList();

        var declared = lines
            .Where(line => line.StartsWith("type ", StringComparison.Ordinal))
            .Select(line => new string(line["type ".Length..].TakeWhile(IsIdentifierCharacter).ToArray()))
            .ToHashSet(StringComparer.Ordinal);
        var embedded = lines
            .Where(line => line.Length > 1 && line[0] == '\t' && line.Skip(1).All(IsIdentifierCharacter))
            .Select(line => line[1..])
            .ToHashSet(StringComparer.Ordinal);

        // Every handle interface embeds handleReference, so an export that embeds nothing has not been read.
        Assert.Contains("handleReference", embedded);
        Assert.Empty(embedded.Except(declared).Order(StringComparer.Ordinal));

        // handleReference.getHandle returns a *handle, whose TypeID is a handleType.
        Assert.Contains("handle", declared);
        Assert.Contains("handleType", declared);
    }

    private static void AssertSameNames(IEnumerable<string> generated, IEnumerable<string> exported)
    {
        var generatedNames = generated.ToHashSet(StringComparer.Ordinal);
        var exportedNames = exported.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(generatedNames.Except(exportedNames).Order(StringComparer.Ordinal));
        Assert.Empty(exportedNames.Except(generatedNames).Order(StringComparer.Ordinal));
        Assert.NotEmpty(generatedNames);
    }

    // Reads the exported handle interfaces out of the generated SDK. The generator writes them as
    //   type Redis interface {
    //       handleReference
    //       WithPersistence(options ...*WithPersistenceOptions) Redis
    //       Err() error
    //   }
    // and a handle-only interface on one line: type DistributedApplication interface { handleReference }.
    private static Dictionary<string, List<string>> ReadGeneratedInterfaceMethods(string source)
    {
        const string OneLineSuffix = " interface { handleReference }";
        const string MultiLineSuffix = " interface {";

        var interfaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var line in source.ReplaceLineEndings("\n").Split('\n'))
        {
            if (current is null)
            {
                if (!line.StartsWith("type ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.EndsWith(OneLineSuffix, StringComparison.Ordinal))
                {
                    interfaces[line["type ".Length..^OneLineSuffix.Length]] = [];
                }
                else if (line.EndsWith(MultiLineSuffix, StringComparison.Ordinal))
                {
                    current = [];
                    interfaces[line["type ".Length..^MultiLineSuffix.Length]] = current;
                }

                continue;
            }

            if (line == "}")
            {
                current = null;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal) && trimmed != "handleReference")
            {
                current.Add(trimmed);
            }
        }

        return interfaces;
    }

    // Trims an export to the requested items so a snapshot stays reviewable. A null member selection keeps
    // every member of the item.
    private static string RenderFocused(GoApiModel model, params (string ItemId, string[]? MemberNames)[] selection)
    {
        var items = model.Modules.Single().Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var focused = new List<GoApiItem>();

        foreach (var (itemId, memberNames) in selection)
        {
            var item = items.TryGetValue(itemId, out var found)
                ? found
                : throw new InvalidOperationException($"The export has no item '{itemId}'. Items: {string.Join(", ", items.Keys.Where(key => key.Contains(itemId[(itemId.IndexOf(':') + 1)..], StringComparison.Ordinal)))}");

            focused.Add(memberNames is null
                ? item
                : item with { Members = memberNames.Select(name => Assert.Single(item.Members, member => member.Name == name)).ToList() });
        }

        return GoApiExportWriter.WriteToJson(
            model with
            {
                // The informational version changes with every build.
                Generator = new GoApiGeneratorIdentity(model.Generator.Name, "1.0.0"),
                Modules = [new GoApiModule { Name = model.Modules.Single().Name, Items = focused }],
                Declarations = []
            },
            indented: true);
    }

    private static GoApiModel CreateFocusedModel()
    {
        const string owner = "Aspire.Hosting.Redis";
        const string redisTypeId = "Aspire.Hosting.Redis/Aspire.Hosting.Redis.RedisResource";

        return new GoApiModel
        {
            SchemaVersion = 1,
            Language = "go",
            Generator = new GoApiGeneratorIdentity("Aspire.Hosting.CodeGeneration.Go", "13.5.0"),
            Package = new GoApiPackageIdentity(owner, PackageVersion, "apphost/modules/aspire"),
            Modules =
            [
                new GoApiModule
                {
                    Name = owner,
                    Summary = "Redis hosting integration.",
                    Items =
                    [
                        new GoApiItem
                        {
                            Id = "interface:RedisResource",
                            TypeId = redisTypeId,
                            Kind = GoApiItemKind.Interface,
                            Name = "RedisResource",
                            Declaration = "type RedisResource interface",
                            OwningAssemblyName = owner,
                            Summary = "A Redis resource.",
                            Remarks = "Add one with AddRedis.",
                            Implements = ["Resource"],
                            Members =
                            [
                                new GoApiMember
                                {
                                    Id = "RedisResource.WithPersistence",
                                    Kind = GoApiMemberKind.Method,
                                    Name = "WithPersistence",
                                    Declaration = "WithPersistence(interval float64, options ...*WithPersistenceOptions) RedisResource",
                                    Summary = "Configures persistence.",
                                    Returns = "The same builder.",
                                    IsExperimental = true,
                                    DeprecationMessage = "Use WithDataVolume.",
                                    CapabilityId = "Aspire.Hosting.Redis/withPersistence",
                                    TargetTypeId = redisTypeId,
                                    CapabilityKind = "method",
                                    OwningAssemblyName = owner,
                                    Parameters =
                                    [
                                        new GoApiParameter { Name = "interval", DeclaredType = "float64", Summary = "The snapshot interval." }
                                    ],
                                    Options = new GoApiOptions
                                    {
                                        TypeName = "WithPersistenceOptions",
                                        Fields =
                                        [
                                            new GoApiParameter
                                            {
                                                Name = "KeysChangedThreshold",
                                                DeclaredType = "*float64",
                                                IsOptional = true,
                                                JsonName = "keysChangedThreshold"
                                            }
                                        ]
                                    },
                                    ReturnType = "RedisResource",
                                    ErrorModel = GoApiErrorModel.Deferred,
                                    ReturnsReceiver = true,
                                    ReturnsBuilder = true
                                },
                                new GoApiMember
                                {
                                    Id = "RedisResource.WithCallback",
                                    Kind = GoApiMemberKind.Method,
                                    Name = "WithCallback",
                                    Declaration = "WithCallback(callback func(arg RedisContext), input any) (string, error)",
                                    CapabilityId = "Aspire.Hosting.Redis/withCallback",
                                    TargetTypeId = redisTypeId,
                                    CapabilityKind = "instanceMethod",
                                    OwningAssemblyName = owner,
                                    Parameters =
                                    [
                                        new GoApiParameter { Name = "callback", DeclaredType = "func(arg RedisContext)", IsCallback = true },
                                        new GoApiParameter { Name = "input", DeclaredType = "any", JsonName = "in", AllowedTypes = ["string", "*ReferenceExpression"] }
                                    ],
                                    ReturnType = "(string, error)",
                                    ErrorModel = GoApiErrorModel.Returned
                                },
                                new GoApiMember
                                {
                                    Id = "RedisResource.Err",
                                    Kind = GoApiMemberKind.Method,
                                    Name = "Err",
                                    Declaration = "Err() error",
                                    Summary = "Err returns the first error recorded on this wrapper.",
                                    ReturnType = "error",
                                    ErrorModel = GoApiErrorModel.None,
                                    IsSynthesized = true
                                }
                            ]
                        },
                        new GoApiItem
                        {
                            Id = "options:WithPersistenceOptions",
                            TypeId = $"{owner}/WithPersistenceOptions",
                            Kind = GoApiItemKind.Options,
                            Name = "WithPersistenceOptions",
                            Declaration = "type WithPersistenceOptions struct",
                            OwningAssemblyName = owner,
                            UsedBy = ["Aspire.Hosting.Redis/withPersistence"],
                            Members =
                            [
                                new GoApiMember
                                {
                                    Id = "WithPersistenceOptions.KeysChangedThreshold",
                                    Kind = GoApiMemberKind.Field,
                                    Name = "KeysChangedThreshold",
                                    Declaration = "KeysChangedThreshold *float64 `json:\"keysChangedThreshold,omitempty\"`",
                                    JsonName = "keysChangedThreshold"
                                }
                            ]
                        },
                        new GoApiItem
                        {
                            Id = "enum:RedisKind",
                            TypeId = $"{owner}/RedisKind",
                            Kind = GoApiItemKind.Enum,
                            Name = "RedisKind",
                            Declaration = "type RedisKind string",
                            OwningAssemblyName = owner,
                            IsMarker = false,
                            Members =
                            [
                                new GoApiMember
                                {
                                    Id = "RedisKind.RedisKindValkey",
                                    Kind = GoApiMemberKind.Constant,
                                    Name = "RedisKindValkey",
                                    Declaration = "RedisKindValkey RedisKind = \"valkey\"",
                                    Value = "valkey"
                                }
                            ]
                        },
                        new GoApiItem
                        {
                            Id = "augmentation:Aspire.Hosting.Redis:Resource",
                            TypeId = "Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource",
                            Kind = GoApiItemKind.Augmentation,
                            Name = "Resource",
                            Declaration = "type Resource interface",
                            OwningAssemblyName = "Aspire.Hosting"
                        }
                    ]
                }
            ],
            Declarations =
            [
                new GoApiDeclaration
                {
                    Id = "Aspire.Hosting.Redis:interface:RedisResource",
                    Content = "type RedisResource interface {\r\n\thandleReference\r\n}",
                    OwningAssemblyName = owner
                }
            ]
        };
    }
}
