// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ManifestExpressionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SymbolicOnlyProvidersPreserveOpaqueTokensAlongsideLoweredOrigins()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var origin = frontend.GetEndpoint("http").BrowserOrigins.First;
        var opaque = new TestManifestExpression("{customValue}");
        var mixed = new TestManifestExpression($"{opaque.ValueExpression}|{origin.ValueExpression}", opaque, origin);
        var module = builder.AddResource(new AzureBicepResource("module", templateString: "param custom string\nparam mixed string"));
        module.Resource.Parameters["custom"] = opaque;
        module.Resource.Parameters["mixed"] = mixed;
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>(), workspace.Path)
            .DefaultTimeout();

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData("{resource.connectionString}", true)]
    [InlineData("{parameter.value}", true)]
    [InlineData("{parameter.inputs.value}", true)]
    [InlineData("{infra.outputs.host}", true)]
    [InlineData("{infra.secretOutputs.password}", true)]
    [InlineData("{infra.secretOutputs}", true)]
    [InlineData("{infra.secrets.password}", true)]
    [InlineData("{.outputs.host}", true)]
    [InlineData("{frontend.bindings.http.url}", true)]
    [InlineData("{frontend.bindings.http.host}", true)]
    [InlineData("{frontend.bindings.http.port}", true)]
    [InlineData("{frontend.bindings.http.targetPort}", true)]
    [InlineData("{frontend.bindings.http.scheme}", true)]
    [InlineData("{frontend.bindings.http.protocol}", true)]
    [InlineData("{frontend.bindings.http.transport}", true)]
    [InlineData("{frontend.bindings.http.external}", true)]
    [InlineData("{frontend.containerImage}", true)]
    [InlineData("{frontend.containerPort}", true)]
    [InlineData("{frontend.volumes.0.storage}", true)]
    [InlineData("{frontend.bindMounts.0.storage}", true)]
    [InlineData("{frontend.bindings.http.tlsEnabled}", false)]
    [InlineData("{frontend.customProperty}", false)]
    [InlineData("{frontend.bindings.http.customProperty}", false)]
    [InlineData("{frontend.bindings.http}", false)]
    [InlineData("{frontend.volumes.bad.storage}", false)]
    [InlineData("{frontend.volumes.-1.storage}", false)]
    [InlineData("{parameter.inputs.value.extra}", false)]
    [InlineData("{.unknown.host}", false)]
    [InlineData("{unknown}", false)]
    public void ClassifiesManifestVocabularyIndependentlyOfProviderType(string expression, bool supported)
    {
        Assert.Equal(supported, ManifestExpression.IsSupportedReference(expression));
    }

    [Fact]
    public void TokenizationPreservesEscapedReferences()
    {
        var expression = "{{ignored.value}}/{real.value}/{{{other.value}}}";

        Assert.Collection(ManifestExpression.GetTokens(expression),
            token => Assert.Equal("{real.value}", expression.Substring(token.Start, token.Length)),
            token => Assert.Equal("{other.value}", expression.Substring(token.Start, token.Length)));
    }

    [Theory]
    [InlineData("{unclosed")]
    [InlineData("unexpected}")]
    public void MalformedExpressionsFailExplicitly(string expression)
    {
        Assert.Throws<FormatException>(() => ManifestExpression.GetTokens(expression).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedEndpointPropertyBecomesConcreteInDirectAndSelectedValues(bool tlsEnabled)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publicHost = builder.AddParameter("public-host");
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var endpoint = frontend.GetEndpoint("http");
        endpoint.EndpointAnnotation.TlsEnabled = tlsEnabled;
        var tls = endpoint.Property(EndpointProperty.TlsEnabled);
        var selected = ReferenceExpression.CreateConditional(tls, tlsEnabled.ToString(),
            ReferenceExpression.Create($"https://{publicHost}?tls={tls}"), ReferenceExpression.Empty);
        builder.AddContainer("backend", "image")
            .WithEnvironment("TLS", tls)
            .WithEnvironment("URL", selected);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseParameters(tlsEnabled);
    }

    [Fact]
    public async Task UnsupportedCustomPropertyResolvesOnceWithoutResolvingAdjacentParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publicHost = new ParameterResource("public-host", _ =>
            throw new InvalidOperationException("A supported deployment parameter must stay symbolic."));
        var calls = 0;
        var custom = new TestManifestValue("{frontend.customProperty}", (context, _) =>
        {
            Assert.True(context.ExecutionContext!.IsPublishMode);
            calls++;
            return ValueTask.FromResult<string?>("path with spaces/{literal.value}");
        });
        builder.AddContainer("backend", "image")
            .WithEnvironment("CUSTOM", custom)
            .WithEnvironment("URL", ReferenceExpression.Create($"https://{publicHost}/{custom:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.Equal(1, calls);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task DistinctProviderInstancesRetainValuesAndDeterministicNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var firstCalls = 0;
        var secondCalls = 0;
        var first = new TestManifestValue("{opaque}", (_, _) =>
        {
            firstCalls++;
            return ValueTask.FromResult<string?>("first");
        });
        var second = new TestManifestValue("{opaque}", (_, _) =>
        {
            secondCalls++;
            return ValueTask.FromResult<string?>("second");
        });
        var selected = ReferenceExpression.CreateConditional(new ParameterResource("enabled", _ => bool.TrueString),
            bool.TrueString, ReferenceExpression.Create($"{first}|{second}|{first}"), ReferenceExpression.Empty);
        builder.AddContainer("backend", "image")
            .WithEnvironment("FIRST", first)
            .WithEnvironment("VALUES", selected);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var manifest = await ManifestUtils.GetManifestForModel(model);

        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        var repeated = await ManifestUtils.GetManifestForModel(model);
        Assert.Equal(2, firstCalls);
        Assert.Equal(2, secondCalls);
        Assert.True(JsonNode.DeepEquals(manifest, repeated));
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrappedStructuredExpressionsPreserveProviderOccurrencesAndFormats(bool reverseOrder)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = new TestManifestValue("{opaque}", (_, _) => ValueTask.FromResult<string?>("first value"));
        var second = new TestManifestValue("{opaque}", (_, _) => ValueTask.FromResult<string?>("second/value"));
        var inner = reverseOrder
            ? ReferenceExpression.Create($"{second:uri}|{first}|{second}")
            : ReferenceExpression.Create($"{first}|{second:uri}|{first}");
        var wrapper = new TestManifestValue(inner.ValueExpression, (_, _) =>
            throw new InvalidOperationException("A structured wrapper must not be resolved."), inner);
        builder.AddContainer("backend", "image").WithEnvironment("VALUES", wrapper);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseParameters(reverseOrder);
    }

    [Fact]
    public async Task WrappedRepeatedProviderIsNotAmbiguous()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var calls = 0;
        var provider = new TestManifestValue("{opaque}", (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<string?>("value");
        });
        var wrapper = new TestManifestValue("prefix/{opaque}/{opaque}", (_, _) =>
            throw new InvalidOperationException("An explainable wrapper must not be resolved."), provider, provider);
        builder.AddContainer("backend", "image").WithEnvironment("VALUES", wrapper);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.Equal(1, calls);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task AmbiguousWrapperTokensFailWithoutSelectingAProvider()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = new TestManifestValue("{opaque}", (_, _) => throw new InvalidOperationException("Do not resolve first."));
        var second = new TestManifestValue("{opaque}", (_, _) => throw new InvalidOperationException("Do not resolve second."));
        var wrapper = new TestManifestValue("prefix/{opaque}/{opaque}", (_, _) =>
            throw new InvalidOperationException("Do not resolve the ambiguous wrapper."), first, second);
        builder.AddContainer("backend", "image").WithEnvironment("VALUES", wrapper);
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestUtils.GetManifestForModel(
            app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Equal(
            "Manifest expression '{opaque}' matches multiple distinct providers without unambiguous expression structure.",
            error.Message);
    }

    [Fact]
    public async Task ConcreteNamesAvoidExistingModelResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var value = new TestManifestValue("{opaque}", (_, _) => ValueTask.FromResult<string?>("concrete"));
        builder.AddContainer("backend", "image").WithEnvironment("CUSTOM", value);
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var initial = await ManifestUtils.GetManifestForModel(model);
        var name = Assert.Single(initial["resources"]!.AsObject(), resource =>
            resource.Value!["type"]!.GetValue<string>() == "value.v0").Key;
        model.Resources.Add(new ParameterResource(name, _ => "reserved"));

        var manifest = await ManifestUtils.GetManifestForModel(model);

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task SupportedCustomProviderRetainsSymbolicValueWithoutResolution()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = builder.AddParameter("public-host");
        var symbolic = new TestManifestValue("{public-host.value}", (_, _) =>
            throw new InvalidOperationException("A supported symbolic provider must not be resolved."), host.Resource);
        builder.AddContainer("backend", "image").WithEnvironment("HOST", symbolic);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task LiteralManifestValuesAreFormattedWithoutResolvingProviders()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var literal = new TestManifestValue("path with spaces/{{literal}}", (_, _) =>
            throw new InvalidOperationException("A literal manifest value must not be resolved."));
        builder.AddContainer("backend", "image")
            .WithEnvironment("URL", ReferenceExpression.Create($"https://public.example/{literal:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task CompositeUriFormattingEncodesLiteralsAndDefersParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = new ParameterResource("host", _ =>
            throw new InvalidOperationException("A deferred parameter must not be resolved."));
        var query = new ParameterResource("query", _ =>
            throw new InvalidOperationException("A deferred parameter must not be resolved."));
        var wrapper = new TestManifestValue("prefix/{host.value}/{{literal}}?q={query.value}&again={host.value}",
            (_, _) => throw new InvalidOperationException("A symbolic wrapper must not be resolved."), host, query);
        var selected = ReferenceExpression.CreateConditional(new ParameterResource("enabled", _ => bool.TrueString),
            bool.TrueString, ReferenceExpression.Create($"{wrapper:uri}"), ReferenceExpression.Empty);
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", selected);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task UriFormattingExpandsOnlySelectedConditionalAliases()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = builder.AddParameter("public-host");
        var conditionCalls = 0;
        var condition = new ParameterResource("selected", _ =>
        {
            conditionCalls++;
            return bool.TrueString;
        });
        var customCalls = 0;
        var custom = new TestManifestValue("{opaque}", (_, _) =>
        {
            customCalls++;
            return ValueTask.FromResult<string?>("path with spaces/{literal}");
        });
        var inactive = ReferenceExpression.CreateConditional(new ParameterResource("inactive", _ =>
                throw new InvalidOperationException("An unselected condition must not be resolved.")),
            bool.TrueString, ReferenceExpression.Empty, ReferenceExpression.Empty);
        var conditional = ReferenceExpression.CreateConditional(condition,
            bool.TrueString, ReferenceExpression.Create($"https://{host}/{custom}"), inactive);
        var wrapper = new TestManifestValue(conditional.ValueExpression, (_, _) =>
            throw new InvalidOperationException("The selected expression must remain symbolic."), conditional);
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", ReferenceExpression.Create($"{wrapper:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.Equal(1, customCalls);
        Assert.Equal(1, conditionCalls);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData(EndpointProperty.BrowserOrigin)]
    [InlineData(EndpointProperty.BrowserOrigins)]
    [InlineData(EndpointProperty.BrowserOriginsJson)]
    public async Task EndpointOriginPropertiesBecomeRequiredInputs(EndpointProperty property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var endpoint = frontend.GetEndpoint("http");
        var origin = endpoint.Property(property);
        var wrapped = new TestManifestValue(origin.ValueExpression, (_, _) =>
            throw new InvalidOperationException("A manifest input must not read runtime values."), origin);
        var selected = ReferenceExpression.CreateConditional(endpoint.Property(EndpointProperty.TlsEnabled), bool.FalseString,
            ReferenceExpression.Create($"{frontend.GetEndpoint("HTTP").Property(property)}"),
            ReferenceExpression.Create($"unused"));
        builder.AddContainer("backend", "image")
            .WithEnvironment("DIRECT", origin)
            .WithEnvironment("COMPOSITE", ReferenceExpression.Create($"prefix/{origin}/suffix"))
            .WithEnvironment("WRAPPED", wrapped)
            .WithEnvironment("SELECTED", selected)
            .WithEnvironment("ENCODED", ReferenceExpression.Create($"{origin:uri}"))
            .WithEnvironment("ENDPOINT", endpoint)
            .WithEnvironment("HOST", endpoint.Property(EndpointProperty.Host));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>())
            .DefaultTimeout();

        Assert.False(endpoint.EndpointAnnotation.BrowserUrlsSnapshot.IsValueSet);
        Assert.Collection(manifest["resources"]!.AsObject().Where(resource => resource.Value!["type"]!.GetValue<string>() == "parameter.v0"),
            input => Assert.Equal($"frontend-http-{property}", input.Key));
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseParameters(property);
    }

    [Fact]
    public async Task EndpointOriginInputsAreLoweredInResourceManifestCallbacks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var origin = frontend.GetEndpoint("http").BrowserOrigins.First;
        var module = builder.AddResource(new AzureBicepResource("module", templateString: "param origin string\nparam redirect string"));
        module.Resource.Parameters["origin"] = origin;
        module.Resource.Parameters["redirect"] = () => ReferenceExpression.Create($"{origin}/signin-oidc");
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>(), workspace.Path)
            .DefaultTimeout();

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task EndpointOriginInputsAvoidExistingResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddParameter("frontend-http-BrowserOrigin");
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        builder.AddContainer("backend", "image")
            .WithEnvironment("ORIGIN", frontend.GetEndpoint("http").BrowserOrigins.First);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>())
            .DefaultTimeout();

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnselectedEndpointOriginDoesNotRequireDeploymentInput(bool originInTrueBranch)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var origin = ReferenceExpression.Create($"{frontend.GetEndpoint("http").Property(EndpointProperty.BrowserOrigin)}");
        var deployed = ReferenceExpression.Create($"https://deployed.example");
        var conditional = ReferenceExpression.CreateConditional(
            frontend.GetEndpoint("http").Property(EndpointProperty.TlsEnabled),
            originInTrueBranch ? bool.TrueString : bool.FalseString,
            originInTrueBranch ? origin : deployed,
            originInTrueBranch ? deployed : origin);
        builder.AddContainer("backend", "image").WithEnvironment("ORIGIN", conditional);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseParameters(originInTrueBranch);
        Assert.False(frontend.GetEndpoint("http").EndpointAnnotation.BrowserUrlsSnapshot.IsValueSet);
    }

    [Fact]
    public async Task ConditionalDoesNotEvaluateUnselectedNestedCondition()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var unselectedCondition = new ParameterResource("unselected-condition", _ =>
            throw new InvalidOperationException("An unselected condition must not be evaluated."));
        var selectedValue = new ParameterResource("selected-value", _ => "selected");
        var nested = ReferenceExpression.CreateConditional(unselectedCondition, bool.TrueString,
            ReferenceExpression.Create($"nested-true"), ReferenceExpression.Create($"nested-false"));
        var conditional = ReferenceExpression.CreateConditional(
            frontend.GetEndpoint("http").Property(EndpointProperty.TlsEnabled), bool.TrueString,
            nested, ReferenceExpression.Create($"{selectedValue}"));
        builder.AddContainer("backend", "image").WithEnvironment("RESULT", conditional);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task CompositeUriFormattingDefersInfrastructureOutputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var infrastructure = builder.AddResource(new AzureBicepResource("infra",
            templateString: "output host string = 'public.example'"));
        var host = new BicepOutputReference("host", infrastructure.Resource);
        var wrapper = new TestManifestValue("https://{infra.outputs.host}/signin-oidc",
            (_, _) => throw new InvalidOperationException("An infrastructure output must not be resolved."), host);
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", ReferenceExpression.Create($"{wrapper:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(
            app.Services.GetRequiredService<DistributedApplicationModel>(), workspace.Path);

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedUriFormattingPreservesParameterAndCompositeAliasOperations(bool composite)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var query = new ParameterResource("query", _ =>
            throw new InvalidOperationException("A deployment parameter must remain deferred."));
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var wrapper = new TestManifestValue("segment/{query.value}", (_, _) =>
            throw new InvalidOperationException("A symbolic wrapper must not be resolved."), query);
        var inner = composite
            ? ReferenceExpression.Create($"https://example/?q={wrapper:uri}")
            : ReferenceExpression.Create($"https://example/?q={query:uri}");
        var inactive = new ParameterResource("inactive", _ =>
            throw new InvalidOperationException("An inactive branch must not be resolved."));
        var conditional = ReferenceExpression.CreateConditional(frontend.GetEndpoint("http").Property(EndpointProperty.TlsEnabled),
            bool.FalseString, inner, ReferenceExpression.Create($"{inactive}"));
        var browser = new TestManifestValue(conditional.ValueExpression, (_, _) =>
            throw new InvalidOperationException("The selected expression must remain symbolic."), conditional);
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", ReferenceExpression.Create($"{browser:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseParameters(composite);
    }

    [Fact]
    public async Task NestedUriFormattingPreservesConcreteEncodingPasses()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var calls = 0;
        var concrete = new TestManifestValue("{opaque}", (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<string?>("one two/%");
        });
        var frontend = builder.AddContainer("frontend", "image").WithHttpEndpoint(targetPort: 8080);
        var inner = ReferenceExpression.Create($"https://example/?q={concrete:uri}");
        var browser = new TestManifestValue(inner.ValueExpression, (_, _) =>
            throw new InvalidOperationException("The composed expression must remain symbolic."), inner);
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", ReferenceExpression.Create($"{browser:uri}"));
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.Equal(1, calls);
        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task OpaqueSymbolicUriFormattingFailsWithoutResolvingTheProvider()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var opaque = new TestManifestValue("{resource.connectionString}", (_, _) =>
            throw new InvalidOperationException("An opaque symbolic reference must not be forced to resolve."));
        builder.AddContainer("backend", "image").WithEnvironment("ENCODED", ReferenceExpression.Create($"{opaque:uri}"));
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => ManifestUtils.GetManifestForModel(
            app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Equal(
            "URI formatting cannot be deferred for manifest reference '{resource.connectionString}'. " +
            "Use a parameter/output expression or a publication-time value.", error.Message);
    }

    [Fact]
    public async Task WrappersDiscoverSelectedConditionalAndUnsupportedLeafReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = builder.AddParameter("public-host");
        var condition = new ParameterResource("enabled", _ => bool.TrueString);
        var custom = new TestManifestValue("{frontend.customProperty}", (_, _) => ValueTask.FromResult<string?>("concrete"));
        var inactive = new TestManifestValue("{frontend.inactiveProperty}", (_, _) =>
            throw new InvalidOperationException("An inactive value must not be resolved."));
        var selected = ReferenceExpression.CreateConditional(condition, bool.TrueString,
            ReferenceExpression.Create($"https://{host}/{custom}"), ReferenceExpression.Create($"{inactive}"));
        var inner = new TestManifestValue(selected.ValueExpression, (_, _) =>
            throw new InvalidOperationException("A wrapper around a conditional must remain symbolic."), selected);
        var wrapper = new TestManifestValue($"redirect={inner.ValueExpression}", (_, _) =>
            throw new InvalidOperationException("A composite wrapper must remain symbolic."), inner);
        builder.AddContainer("backend", "image").WithEnvironment("REDIRECT", wrapper);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task WrapperRewritesOnlyReferencedTokensAndIgnoresEscapedDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var host = builder.AddParameter("public-host");
        var unusedCondition = new ParameterResource("unused-condition", _ =>
            throw new InvalidOperationException("An escaped reference must not evaluate its condition."));
        var unused = ReferenceExpression.CreateConditional(unusedCondition, bool.TrueString,
            ReferenceExpression.Create($"unused-true"), ReferenceExpression.Create($"unused-false"));
        var custom = new TestManifestValue("{frontend.customProperty}", (_, _) => ValueTask.FromResult<string?>("concrete"));
        var wrapper = new TestManifestValue(
            $"https://{{public-host.value}}/{custom.ValueExpression}?literal={{{unused.ValueExpression}}}",
            (_, _) => throw new InvalidOperationException("A wrapper with explainable references must remain symbolic."),
            host.Resource, custom, unused);
        builder.AddContainer("backend", "image").WithEnvironment("URL", wrapper);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task ConcreteResolutionPropagatesErrors()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var value = new TestManifestValue("{frontend.customProperty}", (_, _) =>
            throw new InvalidOperationException("The publication-time value is unavailable."));
        builder.AddContainer("backend", "image").WithEnvironment("CUSTOM", value);
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestUtils.GetManifestForModel(
            app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Equal("The publication-time value is unavailable.", error.Message);
    }

    [Fact]
    public async Task ConcreteNullValueRetainsEmptyStringBehavior()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var value = new TestManifestValue("{frontend.customProperty}", (_, _) => ValueTask.FromResult<string?>(null));
        builder.AddContainer("backend", "image").WithEnvironment("CUSTOM", value);
        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());

        await Verify(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task CyclicWrappersFailWithoutEvaluatingProviders()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var references = new object[1];
        var first = new TestManifestValue("{custom.value}", (_, _) =>
            throw new InvalidOperationException("A cyclic wrapper must not be evaluated."), references);
        references[0] = new TestManifestValue("{custom.value}", (_, _) =>
            throw new InvalidOperationException("A cyclic wrapper must not be evaluated."), first);
        builder.AddContainer("backend", "image").WithEnvironment("CUSTOM", first);
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ManifestUtils.GetManifestForModel(
            app.Services.GetRequiredService<DistributedApplicationModel>()));

        Assert.Equal("A cycle was encountered while lowering a manifest expression.", error.Message);
    }

    [Fact]
    public async Task ConcreteResolutionReceivesPublicationContextAndCancellation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var cts = new CancellationTokenSource();
        var value = new TestManifestValue("{frontend.customProperty}", (context, token) =>
        {
            Assert.Same(builder.ExecutionContext, context.ExecutionContext);
            Assert.Equal(cts.Token, token);
            return ValueTask.FromException<string?>(new OperationCanceledException(token));
        });
        builder.AddContainer("backend", "image").WithEnvironment("CUSTOM", value);
        using var app = builder.Build();
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        var context = new ManifestPublishingContext(builder.ExecutionContext,
            Path.Combine(builder.AppHostDirectory, "manifest.json"), writer, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.WriteModel(
            app.Services.GetRequiredService<DistributedApplicationModel>(), cts.Token));
    }
}
