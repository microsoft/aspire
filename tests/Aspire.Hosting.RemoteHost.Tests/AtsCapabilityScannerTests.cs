// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AtsCapabilityScannerTests
{
    #region MapToAtsTypeId Tests

    [Fact]
    public void MapToAtsTypeId_String_ReturnsString()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(string));

        Assert.Equal("string", result);
    }

    [Fact]
    public void MapToAtsTypeId_Int32_ReturnsNumber()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(int));

        Assert.Equal("number", result);
    }

    [Fact]
    public void MapToAtsTypeId_Boolean_ReturnsBoolean()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(bool));

        Assert.Equal("boolean", result);
    }

    [Fact]
    public void MapToAtsTypeId_Void_ReturnsNull()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(void));

        Assert.Null(result);
    }

    [Fact]
    public void MapToAtsTypeId_Task_ReturnsNull()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(Task));

        Assert.Null(result);
    }

    [Fact]
    public void MapToAtsTypeId_TaskOfString_ReturnsString()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(Task<string>));

        Assert.Equal("string", result);
    }

    [Fact]
    public void MapToAtsTypeId_TaskOfInt_ReturnsNumber()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(Task<int>));

        Assert.Equal("number", result);
    }

    [Fact]
    public void MapToAtsTypeId_NullableInt_ReturnsNumber()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(int?));

        Assert.Equal("number", result);
    }

    [Fact]
    public void MapToAtsTypeId_StringArray_ReturnsStringArray()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(string[]));

        Assert.Equal("string[]", result);
    }

    [Fact]
    public void MapToAtsTypeId_IntArray_ReturnsNumberArray()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(int[]));

        Assert.Equal("number[]", result);
    }

    [Fact]
    public void MapToAtsTypeId_IEnumerableOfString_ReturnsStringArray()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(IEnumerable<string>));

        Assert.Equal("string[]", result);
    }

    [Fact]
    public void MapToAtsTypeId_IResourceBuilder_ExtractsResourceType()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(IResourceBuilder<TestResource>));

        // Should derive type ID from TestResource's full name
        // Format: {AssemblyName}/{FullTypeName}
        Assert.Equal("Aspire.Hosting.RemoteHost.Tests/Aspire.Hosting.RemoteHost.Tests.AtsCapabilityScannerTests+TestResource", result);
    }

    [Fact]
    public void MapToAtsTypeId_UnknownType_ReturnsNull()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(AtsCapabilityScannerTests));

        // Unknown types return null (capabilities with unknown types are skipped)
        Assert.Null(result);
    }

    [Fact]
    public void MapToAtsTypeId_ObjectType_ReturnsAny()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(object));

        // System.Object maps to 'any'
        Assert.Equal("any", result);
    }

    [Fact]
    public void MapToAtsTypeId_NonGenericIDictionary_ReturnsStringAnyDict()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(IDictionary));

        Assert.Equal("Aspire.Hosting/Dict<string,any>", result);
    }

    [Fact]
    public void MapToAtsTypeId_NonGenericIList_ReturnsAnyList()
    {
        var result = AtsCapabilityScanner.MapToAtsTypeId(typeof(IList));

        Assert.Equal("Aspire.Hosting/List<any>", result);
    }

    [Fact]
    public void ScanAssembly_IEnumerableCapability_UsesArrayTypes()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var enumerableParameterCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testEnumerableParameter", StringComparison.Ordinal));
        var itemsParameter = Assert.Single(enumerableParameterCapability.Parameters);
        var itemsType = Assert.IsType<AtsTypeRef>(itemsParameter.Type);
        Assert.Equal("string[]", itemsType.TypeId);
        Assert.Equal(AtsTypeCategory.Array, itemsType.Category);

        var enumerableReturnCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testEnumerableReturn", StringComparison.Ordinal));
        Assert.Equal("string[]", enumerableReturnCapability.ReturnType.TypeId);
        Assert.Equal(AtsTypeCategory.Array, enumerableReturnCapability.ReturnType.Category);
    }

    #endregion

    #region DeriveMethodName Tests

    [Fact]
    public void DeriveMethodName_SimpleCapabilityId_ReturnsMethodName()
    {
        var result = AtsCapabilityScanner.DeriveMethodName("Aspire.Hosting/createBuilder");

        Assert.Equal("createBuilder", result);
    }

    [Fact]
    public void DeriveMethodName_NestedCapabilityId_ReturnsMethodName()
    {
        var result = AtsCapabilityScanner.DeriveMethodName("Aspire.Hosting.Redis/addRedis");

        Assert.Equal("addRedis", result);
    }

    [Fact]
    public void DeriveMethodName_NoSlash_ReturnsEntireId()
    {
        var result = AtsCapabilityScanner.DeriveMethodName("withEnvironment");

        Assert.Equal("withEnvironment", result);
    }

    #endregion

    #region DerivePackage Tests

    [Fact]
    public void DerivePackage_SimpleCapabilityId_ReturnsPackage()
    {
        var result = AtsCapabilityScanner.DerivePackage("Aspire.Hosting/createBuilder");

        Assert.Equal("Aspire.Hosting", result);
    }

    [Fact]
    public void DerivePackage_NestedCapabilityId_ReturnsPackage()
    {
        var result = AtsCapabilityScanner.DerivePackage("Aspire.Hosting.Redis/addRedis");

        Assert.Equal("Aspire.Hosting.Redis", result);
    }

    #endregion

    #region Assembly-Level AspireExport Tests

    [Fact]
    public void ScanAssembly_AssemblyLevelExport_AppearsInHandleTypes()
    {
        // Regression test: assembly-level [AspireExport(typeof(T))] attributes must be
        // discovered and included in HandleTypes so they participate in Unknown→Handle resolution.
        // The Aspire.Hosting assembly exports CancellationToken at assembly level.
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);

        // ContainerApp types are exported via assembly-level attributes in AppContainers,
        // but CancellationToken is exported in Aspire.Hosting's AtsTypeMappings.cs
        var cancellationTokenType = result.HandleTypes
            .FirstOrDefault(t => t.AtsTypeId.Contains("CancellationToken"));

        Assert.NotNull(cancellationTokenType);
    }

    [Fact]
    public void ScanAssembly_HostingAssembly_CoreFrameworkAndLifecycleCapabilitiesAreRegistered()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);

        var expectedCapabilities = new[]
        {
            "Aspire.Hosting/getSection",
            "Aspire.Hosting/getChildren",
            "Aspire.Hosting/exists",
            "Aspire.Hosting/isProduction",
            "Aspire.Hosting/isStaging",
            "Aspire.Hosting/isEnvironment",
            "Aspire.Hosting/subscribeBeforeStart",
            "Aspire.Hosting/subscribeAfterResourcesCreated",
            "Aspire.Hosting/onBeforeResourceStarted",
            "Aspire.Hosting/onResourceStopped",
            "Aspire.Hosting/onConnectionStringAvailable",
            "Aspire.Hosting/onInitializeResource",
            "Aspire.Hosting/onResourceEndpointsAllocated",
            "Aspire.Hosting/onResourceReady",
            "Aspire.Hosting/getLoggerFactory",
            "Aspire.Hosting/createLogger",
            "Aspire.Hosting/getResourceLoggerService",
            "Aspire.Hosting/getResourceNotificationService",
            "Aspire.Hosting/getDistributedApplicationModel",
            "Aspire.Hosting/getResources",
            "Aspire.Hosting/findResourceByName",
            "Aspire.Hosting/getUserSecretsManager",
            "Aspire.Hosting/getEventing",
            "Aspire.Hosting/saveStateJson"
        };

        foreach (var expectedCapability in expectedCapabilities)
        {
            Assert.Contains(result.Capabilities, capability => capability.CapabilityId == expectedCapability);
        }
    }

    [Fact]
    public void ScanAssembly_HostingAssembly_ExportsExpectedHandleTypesAndInstanceMembers()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);

        var expectedHandleTypes = new[]
        {
            "IConfigurationSection",
            "ILogger",
            "ILoggerFactory",
            "DistributedApplicationModel",
            "IDistributedApplicationEventing",
            "BeforeStartEvent",
            "AfterResourcesCreatedEvent",
            "BeforeResourceStartedEvent",
            "ConnectionStringAvailableEvent",
            "InitializeResourceEvent",
            "ResourceEndpointsAllocatedEvent",
            "ResourceReadyEvent",
            "ResourceStoppedEvent",
            "IUserSecretsManager",
            "IReportingStep",
            "IReportingTask",
            "PipelineContext",
            "PipelineStepFactoryContext",
            "PipelineSummary"
        };

        foreach (var expectedHandleType in expectedHandleTypes)
        {
            Assert.Contains(result.HandleTypes, type => type.AtsTypeId.Contains(expectedHandleType, StringComparison.Ordinal));
        }

        Assert.Contains(result.Capabilities, capability => capability.CapabilityId.EndsWith("/BeforeStartEvent.services", StringComparison.Ordinal));
        Assert.Contains(result.Capabilities, capability => capability.CapabilityId.EndsWith("/IUserSecretsManager.trySetSecret", StringComparison.Ordinal));
        Assert.Contains(result.Capabilities, capability => capability.CapabilityId.EndsWith("/PipelineStepContext.reportingStep", StringComparison.Ordinal));
        Assert.Contains(result.Capabilities, capability => capability.CapabilityId.EndsWith("/PipelineSummary.add", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanAssembly_HostingAssembly_ExportsWithHttpCommandCapabilityWithAtsFriendlyOptions()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId == "Aspire.Hosting/withHttpCommand");

        Assert.Equal("withHttpCommand", capability.MethodName);
        Assert.Equal(3, capability.Parameters.Count);
        Assert.DoesNotContain(capability.Parameters, p => p.Name == "endpointName");
        Assert.DoesNotContain(capability.Parameters, p => p.Name == "commandName");

        var optionsParameter = Assert.Single(capability.Parameters, p => p.Name == "options");
        Assert.Equal("options", optionsParameter.Name);
        Assert.NotNull(optionsParameter.Type);
        Assert.Equal(AtsTypeCategory.Dto, optionsParameter.Type.Category);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(typeof(HttpCommandExportOptions)), optionsParameter.Type.TypeId);

        var dto = Assert.Single(result.DtoTypes, d => d.TypeId == AtsTypeMapping.DeriveTypeId(typeof(HttpCommandExportOptions)));
        Assert.Equal(nameof(HttpCommandExportOptions), dto.Name);
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.CommandName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.EndpointName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.MethodName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.ResultMode));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(CommandOptions.Parameter));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.HttpClientName));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.PrepareRequest));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.Method));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.EndpointSelector));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.GetCommandResult));
    }

    #endregion

    #region Callback Parameter Type Resolution Tests

    [Fact]
    public void ScanAssembly_MultiParamCallbackTypes_AreResolved()
    {
        // Regression test: callback parameter types must be resolved (not left as Unknown)
        // when the types are exported. Previously only param.Type was resolved but not
        // param.CallbackParameters[i].Type.
        var testAssembly = typeof(AtsCapabilityScannerTests).Assembly;
        var hostingAssembly = typeof(DistributedApplication).Assembly;

        var result = AtsCapabilityScanner.ScanAssemblies([hostingAssembly, testAssembly]);

        // Find the testMultiParamHandleCallback capability
        var capability = result.Capabilities
            .FirstOrDefault(c => c.CapabilityId.EndsWith("/testMultiParamHandleCallback", StringComparison.Ordinal));

        Assert.NotNull(capability);

        var callbackParam = Assert.Single(capability.Parameters, p => p.IsCallback);
        Assert.NotNull(callbackParam.CallbackParameters);
        Assert.Equal(2, callbackParam.CallbackParameters.Count);

        // Both callback parameter types should be resolved to Handle (not Unknown)
        foreach (var cbParam in callbackParam.CallbackParameters)
        {
            Assert.NotNull(cbParam.Type);
            Assert.NotEqual(AtsTypeCategory.Unknown, cbParam.Type.Category);
        }
    }

    [Fact]
    public void ScanAssemblies_AssemblyLevelExportedTypes_AreResolvedAcrossScanOrder()
    {
        Assert.Null(AtsCapabilityScanner.MapToAtsTypeId(typeof(AssemblyLevelExportedTestType)));

        var capabilityAssembly = CreateAssemblyLevelExportCapabilityAssembly(typeof(AssemblyLevelExportedTestType));
        var exportAssembly = CreateAssemblyLevelExportAssembly(typeof(AssemblyLevelExportedTestType));

        var result = AtsCapabilityScanner.ScanAssemblies([capabilityAssembly, exportAssembly]);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/usesAssemblyExportedType", StringComparison.Ordinal));
        var parameter = Assert.Single(capability.Parameters);

        Assert.NotNull(parameter.Type);
        Assert.Equal(AtsTypeCategory.Handle, parameter.Type.Category);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(typeof(AssemblyLevelExportedTestType)), parameter.Type.TypeId);
        Assert.Equal(
            AtsTypeMapping.DeriveTypeId(typeof(AssemblyLevelExportedTestType)),
            AtsCapabilityScanner.MapToAtsTypeId(typeof(AssemblyLevelExportedTestType)));
    }

    [Fact]
    public void ScanAssembly_YarpWithConfiguration_UsesBackgroundThreadOptIn()
    {
        var yarpAssembly = typeof(global::Aspire.Hosting.Yarp.YarpResource).Assembly;

        var result = AtsCapabilityScanner.ScanAssembly(yarpAssembly);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/withConfiguration", StringComparison.Ordinal));
        var withConfigurationMethod = Assert.Single(result.Methods,
            m => m.Key.EndsWith("/withConfiguration", StringComparison.Ordinal)).Value;

        Assert.True(capability.RunSyncOnBackgroundThread);
        Assert.Equal(typeof(IResourceBuilder<global::Aspire.Hosting.Yarp.YarpResource>), withConfigurationMethod.ReturnType);

        var parameters = withConfigurationMethod.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IResourceBuilder<global::Aspire.Hosting.Yarp.YarpResource>), parameters[0].ParameterType);
        Assert.Equal(typeof(Action<global::Aspire.Hosting.IYarpConfigurationBuilder>), parameters[1].ParameterType);
    }

    [Fact]
    public void ScanAssembly_ClassLevelBackgroundThreadOptIn_AppliesToExportedMethods()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/classLevelBackgroundThreadProbe", StringComparison.Ordinal));

        Assert.True(capability.RunSyncOnBackgroundThread);
    }

    [Fact]
    public void ScanAssembly_MethodNameCollision_PrefersConcreteTargetAndKeepsGenericElsewhere()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);
        var testResourceTypeId = AtsTypeMapping.DeriveTypeId(typeof(TestResource));
        var otherTestResourceTypeId = AtsTypeMapping.DeriveTypeId(typeof(OtherTestResource));

        var genericCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testGenericCollision", StringComparison.Ordinal));
        var concreteCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testConcreteCollision", StringComparison.Ordinal));

        Assert.DoesNotContain(genericCapability.ExpandedTargetTypes, t => t.TypeId == testResourceTypeId);
        Assert.Contains(genericCapability.ExpandedTargetTypes, t => t.TypeId == otherTestResourceTypeId);
        Assert.Contains(concreteCapability.ExpandedTargetTypes, t => t.TypeId == testResourceTypeId);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("testGenericCollision", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("testConcreteCollision", StringComparison.Ordinal) &&
            diagnostic.Location is not null &&
            diagnostic.Location.EndsWith("/testGenericCollision", StringComparison.Ordinal));

        Assert.Contains(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testGlobalCapability", StringComparison.Ordinal));
    }

    #endregion

    #region Exported Value Tests

    [Fact]
    public void ScanAssembly_MutableDictionaryExportedValue_IsSkipped()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        Assert.DoesNotContain(result.ExportedValues, value =>
            string.Join(".", value.PathSegments) == "InvalidValues.InvalidExportedValues.MutableMetadata");
        Assert.DoesNotContain(result.ExportedValues, value =>
            string.Join(".", value.PathSegments) == "InvalidValues.InvalidExportedValues.DtoWithMutableList");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("copied shapes", StringComparison.Ordinal)
            && diagnostic.Location == "Aspire.Hosting.RemoteHost.Tests.AtsCapabilityScannerTests+InvalidExportedValues.MutableMetadata");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("copied shapes", StringComparison.Ordinal)
            && diagnostic.Location == "Aspire.Hosting.RemoteHost.Tests.AtsCapabilityScannerTests+InvalidExportedValues.DtoWithMutableList");
    }

    [Fact]
    public void ScanAssembly_ExportedDtoValueWithIgnoredMutableProperty_IsIncluded()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var exportedValue = Assert.Single(result.ExportedValues,
            value => string.Join(".", value.PathSegments) == "IgnoredPropertyValues.IgnoredPropertyExportedValues.Value");
        var valueObject = Assert.IsType<JsonObject>(exportedValue.Value);
        Assert.True(valueObject.ContainsKey(nameof(ExportedDtoWithIgnoredMutableProperty.Name)));
        Assert.False(valueObject.ContainsKey(nameof(ExportedDtoWithIgnoredMutableProperty.Items)));
    }

    [Fact]
    public void ScanAssembly_InvalidExportedValuePath_EmitsWarningAndSkipsValue()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        Assert.DoesNotContain(result.ExportedValues, value =>
            string.Join(".", value.PathSegments).Contains("Invalid-Values", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ExportedValues, value =>
            string.Join(".", value.PathSegments).Contains("123Name", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("path segment 'Invalid-Values'", StringComparison.Ordinal)
            && diagnostic.Location == "Aspire.Hosting.RemoteHost.Tests.AtsCapabilityScannerTests+InvalidExportedValuePaths.BadCatalog");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("path segment '123Name'", StringComparison.Ordinal)
            && diagnostic.Location == "Aspire.Hosting.RemoteHost.Tests.AtsCapabilityScannerTests+InvalidExportedValuePaths.BadName");
    }

    [Fact]
    public void ScanAssembly_DuplicateExportedValuePath_EmitsWarningAndSkipsLaterValue()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        Assert.Single(result.ExportedValues,
            value => string.Join(".", value.PathSegments) == "DuplicateValues.DuplicateExportedValues.Shared");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains(
                "Duplicate exported value path 'DuplicateValues.DuplicateExportedValues.Shared'",
                StringComparison.Ordinal)
            && diagnostic.Location == "DuplicateValues.DuplicateExportedValues.Shared");
    }

    [Fact]
    public void ScanAssembly_PrefixConflictingExportedValuePath_EmitsWarningAndSkipsLaterValue()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        Assert.DoesNotContain(result.ExportedValues, value =>
            string.Join(".", value.PathSegments) == "ConflictingValues.PrefixConflictingExportedValues.Node.Child");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains(
                "Conflicting exported value path 'ConflictingValues.PrefixConflictingExportedValues.Node.Child'",
                StringComparison.Ordinal)
            && diagnostic.Message.Contains(
                "Existing path 'ConflictingValues.PrefixConflictingExportedValues.Node'",
                StringComparison.Ordinal)
            && diagnostic.Location == "ConflictingValues.PrefixConflictingExportedValues.Node.Child");
    }

    #endregion

    #region Test Types

    private sealed class TestResource : Resource
    {
        public TestResource(string name) : base(name)
        {
        }
    }

    private sealed class OtherTestResource : Resource
    {
        public OtherTestResource(string name) : base(name)
        {
        }
    }

    public sealed class AssemblyLevelExportedTestType
    {
    }

    private static class TestExports
    {
        [AspireExport]
        public static void TestEnumerableParameter(IDistributedApplicationBuilder builder, IEnumerable<string> items)
        {
            _ = builder;
            _ = items;
        }

        [AspireExport]
        public static IEnumerable<string> TestEnumerableReturn(IDistributedApplicationBuilder builder)
        {
            _ = builder;
            return [];
        }

        [AspireExport]
        public static IResourceBuilder<TestResource> TestMultiParamHandleCallback(
            IResourceBuilder<TestResource> builder,
            Func<ContainerResource, ProjectResource, Task> callback)
        {
            _ = callback;
            return builder;
        }

        [AspireExport("testGenericCollision", MethodName = "withCollision")]
        public static IResourceBuilder<T> TestGenericCollision<T>(IResourceBuilder<T> builder)
            where T : IResource
        {
            return builder;
        }

        [AspireExport("testConcreteCollision", MethodName = "withCollision")]
        public static IResourceBuilder<TestResource> TestConcreteCollision(IResourceBuilder<TestResource> builder)
        {
            return builder;
        }

        [AspireExport]
        public static IResourceBuilder<OtherTestResource> TestOtherResource(IResourceBuilder<OtherTestResource> builder)
        {
            return builder;
        }

        [AspireExport]
        public static string TestGlobalCapability()
        {
            return "value";
        }

    }

    private static class InvalidExportedValues
    {
        [AspireValue("InvalidValues")]
        public static Dictionary<string, string> MutableMetadata { get; } = [];

        [AspireValue("InvalidValues")]
        public static InvalidExportedDto DtoWithMutableList { get; } = new();
    }

    [AspireDto]
    private sealed class InvalidExportedDto
    {
        public List<string> Items { get; set; } = [];
    }

    private static class IgnoredPropertyExportedValues
    {
        [AspireValue("IgnoredPropertyValues")]
        public static ExportedDtoWithIgnoredMutableProperty Value { get; } = new()
        {
            Name = "valid",
            Items = ["ignored"]
        };
    }

    [AspireDto]
    private sealed class ExportedDtoWithIgnoredMutableProperty
    {
        public string Name { get; init; } = "";

        [AspireExportIgnore]
        public List<string> Items { get; init; } = [];
    }

    private static class InvalidExportedValuePaths
    {
        [AspireValue("Invalid-Values")]
        public static string BadCatalog { get; } = "bad";

        [AspireValue("InvalidValues", Name = "123Name")]
        public static string BadName { get; } = "bad";
    }

    private static class DuplicateExportedValues
    {
        [AspireValue("DuplicateValues", Name = "Shared")]
        public static string FirstShared { get; } = "first";

        [AspireValue("DuplicateValues", Name = "Shared")]
        public static string SecondShared { get; } = "second";
    }

    private static class PrefixConflictingExportedValues
    {
        [AspireValue("ConflictingValues", Name = "Node")]
        public static string NodeValue { get; } = "root";

        public static class Node
        {
            [AspireValue("ConflictingValues", Name = "Child")]
            public static string ChildValue { get; } = "child";
        }
    }

    private static Assembly CreateAssemblyLevelExportCapabilityAssembly(Type parameterType)
    {
        var assemblyName = new AssemblyName($"AssemblyLevelExportCapability_{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        var exportsTypeBuilder = moduleBuilder.DefineType(
            "Generated.AssemblyLevelExportedTypeExports",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var methodBuilder = exportsTypeBuilder.DefineMethod(
            "UsesAssemblyExportedType",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            [typeof(IDistributedApplicationBuilder), parameterType]);
        methodBuilder.DefineParameter(1, ParameterAttributes.None, "builder");
        methodBuilder.DefineParameter(2, ParameterAttributes.None, "value");
        methodBuilder.SetCustomAttribute(
            new CustomAttributeBuilder(
                typeof(AspireExportAttribute).GetConstructor([typeof(string)])!,
                ["usesAssemblyExportedType"]));
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);

        _ = exportsTypeBuilder.CreateType();

        return assemblyBuilder;
    }

    private static Assembly CreateAssemblyLevelExportAssembly(Type exportedType)
    {
        var assemblyName = new AssemblyName($"AssemblyLevelExport_{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        _ = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        assemblyBuilder.SetCustomAttribute(
            new CustomAttributeBuilder(
                typeof(AspireExportAttribute).GetConstructor([typeof(Type)])!,
                [exportedType]));

        return assemblyBuilder;
    }

    [AspireExport(RunSyncOnBackgroundThread = true)]
    private static class ClassLevelBackgroundThreadExports
    {
        [AspireExport("classLevelBackgroundThreadProbe")]
        public static void Probe(IDistributedApplicationBuilder builder)
        {
            _ = builder;
        }
    }

    #endregion

    #region XML Documentation Extraction Tests

    [Fact]
    public void GetXmlDocSummary_ReturnsNull_WhenDocIsNull()
    {
        var result = AtsCapabilityScanner.GetXmlDocSummary(null, "T:Some.Type");

        Assert.Null(result);
    }

    [Fact]
    public void GetXmlDocSummary_ReturnsNull_WhenMemberNotFound()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:Some.OtherType">
                  <summary>Other type.</summary>
                </member>
              </members>
            </doc>
            """);

        var result = AtsCapabilityScanner.GetXmlDocSummary(doc, "T:Some.Type");

        Assert.Null(result);
    }

    [Fact]
    public void GetXmlDocSummary_ExtractsTypeSummary()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:Some.MyDto">
                  <summary>Options for creating a builder.</summary>
                </member>
              </members>
            </doc>
            """);

        var result = AtsCapabilityScanner.GetXmlDocSummary(doc, "T:Some.MyDto");

        Assert.Equal("Options for creating a builder.", result);
    }

    [Fact]
    public void GetXmlDocSummary_ExtractsPropertySummary()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="P:Some.MyDto.Name">
                  <summary>The resource name.</summary>
                </member>
              </members>
            </doc>
            """);

        var result = AtsCapabilityScanner.GetXmlDocSummary(doc, "P:Some.MyDto.Name");

        Assert.Equal("The resource name.", result);
    }

    [Fact]
    public void GetXmlDocSummary_NormalizesMultilineWhitespace()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:Some.MyDto">
                  <summary>
                    Options for creating
                    a distributed application builder.
                  </summary>
                </member>
              </members>
            </doc>
            """);

        var result = AtsCapabilityScanner.GetXmlDocSummary(doc, "T:Some.MyDto");

        Assert.Equal("Options for creating a distributed application builder.", result);
    }

    [Fact]
    public void GetXmlDocSummary_ReturnsNull_WhenSummaryIsEmpty()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:Some.MyDto">
                  <summary>   </summary>
                </member>
              </members>
            </doc>
            """);

        var result = AtsCapabilityScanner.GetXmlDocSummary(doc, "T:Some.MyDto");

        Assert.Null(result);
    }

    [Fact]
    public void LoadXmlDocumentation_ReturnsCachedResult()
    {
        // Loading for the same assembly twice should return the same object
        var assembly = typeof(DistributedApplication).Assembly;
        var first = AtsCapabilityScanner.LoadXmlDocumentation(assembly);
        var second = AtsCapabilityScanner.LoadXmlDocumentation(assembly);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    #endregion
}
