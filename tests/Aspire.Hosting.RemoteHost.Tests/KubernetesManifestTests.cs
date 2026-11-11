// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Tests.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class KubernetesManifestTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WithField_DispatchesStructuredValuesToPublishedYaml()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["AppHost:Operation=publish", $"Pipeline:OutputPath={workspace.Path}", "Pipeline:Step=publish"],
            DisableDashboard = true
        });
        builder.AddKubernetesEnvironment("env");
        await using var handles = new HandleRegistry();
        var dispatcher = CreateDispatcher(handles);

        builder.AddContainer("api", "nginx:alpine")
            .PublishAsKubernetesService(service =>
            {
                var serviceHandle = handles.Marshal(service, "Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesResource");
                var manifestHandle = dispatcher.Invoke("Aspire.Hosting.Kubernetes/addManifest", new JsonObject
                {
                    ["context"] = serviceHandle,
                    ["apiVersion"] = "cilium.io/v2",
                    ["kind"] = "CiliumNetworkPolicy",
                    ["name"] = "api-netpol"
                });
                Assert.NotNull(manifestHandle);

                void WithField(string path, JsonNode value)
                {
                    var result = dispatcher.Invoke("Aspire.Hosting.Kubernetes/withField", new JsonObject
                    {
                        ["context"] = manifestHandle.DeepClone(),
                        ["path"] = path,
                        ["value"] = value
                    });
                    Assert.NotNull(result);
                    Assert.True(handles.TryGet(manifestHandle["$handle"]!.GetValue<string>(), out var manifest, out _));
                    Assert.True(handles.TryGet(result["$handle"]!.GetValue<string>(), out var returnedManifest, out _));
                    Assert.Same(manifest, returnedManifest);
                }

                WithField("spec.endpointSelector.matchLabels", JsonNode.Parse("""{"app.kubernetes.io/component":"api"}""")!);
                WithField("spec.ingress", JsonNode.Parse("""
                    [
                      {
                        "fromEndpoints": [
                          { "matchLabels": { "app.kubernetes.io/component": "frontend" } }
                        ],
                        "toPorts": [
                          { "ports": [ { "port": "8080", "protocol": "TCP" } ] }
                        ]
                      }
                    ]
                    """)!);
                WithField("spec.egress", new JsonArray());
                WithField("spec.enableDefaultDeny.ingress", JsonValue.Create(true)!);
                WithField("spec.description", JsonValue.Create("API access")!);
                WithField("spec.description", JsonValue.Create("Frontend access to API")!);
            });

        using var app = builder.Build();
        app.Run();

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "templates", "api", "netpol.yaml")), "yaml");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("nested")]
    public async Task WithField_DispatchRejectsNonJsonHandleValues(string shape)
    {
        await using var handles = new HandleRegistry();
        var dispatcher = CreateDispatcher(handles);
        var service = new KubernetesResource("api", new ContainerResource("api"), new KubernetesEnvironmentResource("env"));
        var manifestHandle = dispatcher.Invoke("Aspire.Hosting.Kubernetes/addManifest", new JsonObject
        {
            ["context"] = handles.Marshal(service, "Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesResource"),
            ["apiVersion"] = "example.com/v1",
            ["kind"] = "Example",
            ["name"] = "example"
        });

        dispatcher.Invoke("Aspire.Hosting.Kubernetes/withField", new JsonObject
        {
            ["context"] = manifestHandle!.DeepClone(),
            ["path"] = "spec",
            ["value"] = "unchanged"
        });
        var handle = handles.Marshal(new ContainerResource("invalid"), "Aspire.Hosting/Aspire.Hosting.ApplicationModel.ContainerResource");
        JsonNode value = shape switch
        {
            "object" => new JsonObject { ["label"] = handle },
            "array" => new JsonArray(handle),
            "nested" => new JsonObject { ["items"] = new JsonArray(new JsonObject { ["label"] = handle }) },
            _ => handle
        };

        var exception = Assert.Throws<CapabilityException>(() => dispatcher.Invoke(
            "Aspire.Hosting.Kubernetes/withField", new JsonObject
            {
                ["context"] = manifestHandle,
                ["path"] = "spec",
                ["value"] = value
            }));

        Assert.Equal(AtsErrorCodes.InvalidArgument, exception.Error.Code);
        Assert.StartsWith("Manifest field values must be JSON-compatible primitives, dictionaries, or arrays.", exception.Message);
        var preservedValueException = Assert.Throws<CapabilityException>(() => dispatcher.Invoke(
            "Aspire.Hosting.Kubernetes/withField", new JsonObject
            {
                ["context"] = manifestHandle.DeepClone(),
                ["path"] = "spec.child",
                ["value"] = true
            }));
        Assert.Equal(AtsErrorCodes.InvalidArgument, preservedValueException.Error.Code);
        Assert.StartsWith("Cannot set nested manifest field 'spec.child' because 'spec' already has a scalar value.", preservedValueException.Message);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("""{"nested":[1e400]}""")]
    [InlineData("""[{"nested":-1e400}]""")]
    public async Task WithField_DispatchRejectsNonFiniteJsonNumbers(string json)
    {
        await using var handles = new HandleRegistry();
        var dispatcher = CreateDispatcher(handles);
        var service = new KubernetesResource("api", new ContainerResource("api"), new KubernetesEnvironmentResource("env"));
        var manifestHandle = dispatcher.Invoke("Aspire.Hosting.Kubernetes/addManifest", new JsonObject
        {
            ["context"] = handles.Marshal(service, "Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesResource"),
            ["apiVersion"] = "example.com/v1",
            ["kind"] = "Example",
            ["name"] = "example"
        });

        var exception = Assert.Throws<CapabilityException>(() => dispatcher.Invoke(
            "Aspire.Hosting.Kubernetes/withField", new JsonObject
            {
                ["context"] = manifestHandle,
                ["path"] = "spec",
                ["value"] = JsonNode.Parse(json)
            }));

        Assert.Equal(AtsErrorCodes.InvalidArgument, exception.Error.Code);
        Assert.StartsWith("Manifest field numeric values must be finite.", exception.Message);
    }

    [Theory]
    [InlineData("Go")]
    [InlineData("TypeScript")]
    [InlineData("Python")]
    [InlineData("Java")]
    [InlineData("Rust")]
    public async Task WithField_GeneratedSdkAcceptsStructuredValues(string language)
    {
        var scannedContext = AtsCapabilityScanner.ScanAssemblies([typeof(KubernetesResource).Assembly]).ToAtsContext();
        var capability = Assert.Single(scannedContext.Capabilities,
            capability => capability.CapabilityId == "Aspire.Hosting.Kubernetes/withField");
        var context = new AtsContext
        {
            Capabilities = [capability],
            HandleTypes = scannedContext.HandleTypes.Where(type => type.AtsTypeId.EndsWith(".KubernetesManifestResource", StringComparison.Ordinal)).ToList(),
            DtoTypes = [],
            EnumTypes = []
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new CodeGeneratorResolver(services,
            () => [Assembly.Load($"Aspire.Hosting.CodeGeneration.{language}")],
            NullLogger<CodeGeneratorResolver>.Instance);
        var generator = Assert.IsAssignableFrom<ICodeGenerator>(resolver.GetCodeGenerator(language));
        var files = generator.GenerateDistributedApplication(context);
        var generated = Assert.Single(files, file => file.Value.Contains(capability.CapabilityId, StringComparison.Ordinal));

        var source = generated.Value;
        if (language == "Python")
        {
            // Python embeds its shared runtime in aspire_app.py; snapshot only the generated wrappers.
            var start = source.IndexOf("\nclass BaseKubernetesObject:", StringComparison.Ordinal);
            var end = source.IndexOf("\ndef create_builder(", start, StringComparison.Ordinal);
            source = source[start..end];
        }

        var extension = Path.GetExtension(generated.Key).TrimStart('.');
        await Verify(source.TrimEnd(), extension is "mts" ? "ts" : extension)
            .UseParameters(language);
    }

    private static CapabilityDispatcher CreateDispatcher(HandleRegistry handles)
    {
        var context = AtsCapabilityScanner.ScanAssemblies([typeof(KubernetesResource).Assembly]).ToAtsContext();
        var marshaller = new AtsMarshaller(handles, context, new CancellationTokenRegistry(),
            new Lazy<AtsCallbackProxyFactory>(() => throw new NotImplementedException()));

        return new CapabilityDispatcher(handles, marshaller, [typeof(KubernetesResource).Assembly]);
    }
}
