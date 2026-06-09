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

    [Fact]
    public void ScanAssembly_UnionCapability_CollectsEnumTypesFromUnionMembers()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/testUnionEnumParameter", StringComparison.Ordinal));
        var parameter = Assert.Single(capability.Parameters);

        Assert.Equal(AtsTypeCategory.Union, parameter.Type?.Category);
        Assert.Contains(parameter.Type!.UnionTypes!, type => type.ClrType == typeof(TestUnionEnum));
        Assert.Contains(parameter.Type.UnionTypes!, type => type.TypeId == AtsConstants.String);
        Assert.Contains(result.EnumTypes, type => type.ClrType == typeof(TestUnionEnum));
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
            "Aspire.Hosting/getResourceCommandService",
            "Aspire.Hosting/executeResourceCommand",
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
    public void ScanAssembly_HostingAssembly_ExportsResourceCommandWithResourceUnionAndArguments()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var result = AtsCapabilityScanner.ScanAssembly(hostingAssembly);

        var capability = Assert.Single(result.Capabilities,
            capability => capability.CapabilityId == "Aspire.Hosting/executeResourceCommand");

        Assert.Equal("executeCommandAsync", capability.MethodName);
        Assert.Equal("resourceCommandService", capability.TargetParameterName);
        Assert.Equal(4, capability.Parameters.Count);

        var resourceParameter = capability.Parameters[0];
        Assert.Equal("resource", resourceParameter.Name);
        Assert.Equal(AtsTypeCategory.Union, resourceParameter.Type?.Category);
        Assert.Contains(resourceParameter.Type!.UnionTypes!, type => type.TypeId == "string");
        Assert.Contains(resourceParameter.Type.UnionTypes!, type => type.TypeId == "Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResource");

        var argumentsParameter = capability.Parameters.Single(parameter => parameter.Name == "arguments");
        Assert.True(argumentsParameter.IsOptional);
        Assert.Equal(AtsTypeCategory.Dict, argumentsParameter.Type?.Category);
        Assert.True(argumentsParameter.Type?.IsReadOnly);
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
            "ResourceCommandService",
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
        var commandOptionsProperty = Assert.Single(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.CommandOptions));
        Assert.True(commandOptionsProperty.IsOptional);
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.CommandName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.EndpointName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.MethodName));
        Assert.Contains(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.ResultMode));
        var prepareRequestProperty = Assert.Single(dto.Properties, p => p.Name == nameof(HttpCommandExportOptions.PrepareRequest));
        Assert.True(prepareRequestProperty.IsCallback);
        Assert.True(prepareRequestProperty.IsOptional);

        var callbackParameter = Assert.Single(prepareRequestProperty.CallbackParameters!);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(typeof(HttpCommandPrepareRequestContext)), callbackParameter.Type.TypeId);
        Assert.Equal(AtsTypeCategory.Handle, callbackParameter.Type.Category);
        Assert.Equal(AtsTypeMapping.DeriveTypeId(typeof(HttpCommandRequestExportData)), prepareRequestProperty.CallbackReturnType?.TypeId);
        Assert.Equal(AtsTypeCategory.Dto, prepareRequestProperty.CallbackReturnType?.Category);

        Assert.DoesNotContain(dto.Properties, p => p.Name == "Parameter");
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.HttpClientName));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.Method));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.EndpointSelector));
        Assert.DoesNotContain(dto.Properties, p => p.Name == nameof(HttpCommandOptions.GetCommandResult));

        Assert.DoesNotContain(result.Capabilities,
            c => c.CapabilityId == "Aspire.Hosting/withHttpCommandPrepareRequest");

        var requestDataDto = Assert.Single(result.DtoTypes, d => d.TypeId == AtsTypeMapping.DeriveTypeId(typeof(HttpCommandRequestExportData)));
        Assert.Equal(nameof(HttpCommandRequestExportData), requestDataDto.Name);
        Assert.Contains(requestDataDto.Properties, p => p.Name == nameof(HttpCommandRequestExportData.MethodName));
        Assert.Contains(requestDataDto.Properties, p => p.Name == nameof(HttpCommandRequestExportData.Headers));
        Assert.Contains(requestDataDto.Properties, p => p.Name == nameof(HttpCommandRequestExportData.Content));
        Assert.Contains(requestDataDto.Properties, p => p.Name == nameof(HttpCommandRequestExportData.ContentType));
    }

    [Fact]
    public void ScanAssembly_DerivedExportedType_DoesNotRegenerateInheritedProperties()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var baseNameCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/BaseExportedProperties.name", StringComparison.Ordinal));

        Assert.Contains(baseNameCapability.ExpandedTargetTypes,
            t => t.TypeId == AtsTypeMapping.DeriveTypeId(typeof(DerivedExportedProperties)));
        Assert.Contains(result.Capabilities,
            c => c.CapabilityId.EndsWith("/DerivedExportedProperties.framework", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Capabilities,
            c => c.CapabilityId.EndsWith("/DerivedExportedProperties.name", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Message.Contains(nameof(DerivedExportedProperties), StringComparison.Ordinal)
                && d.Message.Contains("has collisions", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanAssembly_ExposeProperties_DoesNotGenerateSettersForInitOnlyProperties()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);
        var capabilityPrefix = "Aspire.Hosting.RemoteHost.Tests/InitOnlyExportedProperties.";

        var nameGetter = Assert.Single(result.Capabilities,
            c => c.CapabilityId == capabilityPrefix + "name");
        Assert.Equal(AtsCapabilityKind.PropertyGetter, nameGetter.CapabilityKind);
        Assert.Equal(AtsConstants.String, nameGetter.ReturnType.TypeId);

        var descriptionGetter = Assert.Single(result.Capabilities,
            c => c.CapabilityId == capabilityPrefix + "description");
        Assert.Equal(AtsCapabilityKind.PropertyGetter, descriptionGetter.CapabilityKind);
        Assert.Equal(AtsConstants.String, descriptionGetter.ReturnType.TypeId);
        Assert.True(descriptionGetter.ReturnType.IsNullable);

        var mutableSetter = Assert.Single(result.Capabilities,
            c => c.CapabilityId == capabilityPrefix + "setMutableLabel");
        Assert.Equal(AtsCapabilityKind.PropertySetter, mutableSetter.CapabilityKind);

        Assert.DoesNotContain(result.Capabilities,
            c => c.CapabilityId == capabilityPrefix + "setName");
        Assert.DoesNotContain(result.Capabilities,
            c => c.CapabilityId == capabilityPrefix + "setDescription");
    }

    [Fact]
    public void ScanAssembly_DtoNullableScalarProperties_SetTypeRefNullability()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var dto = Assert.Single(result.DtoTypes, d => d.ClrType == typeof(NullableScalarDto));

        var nullableString = Assert.Single(dto.Properties, p => p.Name == nameof(NullableScalarDto.NullableString));
        Assert.Equal(AtsConstants.String, nullableString.Type.TypeId);
        Assert.True(nullableString.Type.IsNullable);
        Assert.False(nullableString.IsOptional);

        var requiredString = Assert.Single(dto.Properties, p => p.Name == nameof(NullableScalarDto.RequiredString));
        Assert.Equal(AtsConstants.String, requiredString.Type.TypeId);
        Assert.NotEqual(true, requiredString.Type.IsNullable);
        Assert.False(requiredString.IsOptional);

        var nullableNumber = Assert.Single(dto.Properties, p => p.Name == nameof(NullableScalarDto.NullableNumber));
        Assert.Equal(AtsConstants.Number, nullableNumber.Type.TypeId);
        Assert.True(nullableNumber.Type.IsNullable);
        Assert.True(nullableNumber.IsOptional);

        var requiredNumber = Assert.Single(dto.Properties, p => p.Name == nameof(NullableScalarDto.RequiredNumber));
        Assert.Equal(AtsConstants.Number, requiredNumber.Type.TypeId);
        Assert.NotEqual(true, requiredNumber.Type.IsNullable);
        Assert.False(requiredNumber.IsOptional);
    }

    [Fact]
    public void ScanAssembly_TargetSpecificMethodShadowsGenericExpandedMethodOnlyForThatTarget()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);
        var shadowedTypeId = AtsTypeMapping.DeriveTypeId(typeof(ShadowedEnvironmentResource));
        var otherTypeId = AtsTypeMapping.DeriveTypeId(typeof(OtherEnvironmentResource));

        var genericCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/shadowedExporter", StringComparison.Ordinal));
        var specificCapability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/specificShadowedExporter", StringComparison.Ordinal));

        Assert.Equal("shadowedExporter", genericCapability.MethodName);
        Assert.Equal("shadowedExporter", specificCapability.MethodName);
        Assert.DoesNotContain(genericCapability.ExpandedTargetTypes, t => t.TypeId == shadowedTypeId);
        Assert.Contains(genericCapability.ExpandedTargetTypes, t => t.TypeId == otherTypeId);
        Assert.Contains(specificCapability.ExpandedTargetTypes, t => t.TypeId == shadowedTypeId);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Message.Contains("shadowedExporter", StringComparison.Ordinal)
                && d.Message.Contains("has collisions", StringComparison.Ordinal));
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
    public void ScanAssembly_GetOnlyMutableCollectionDtoProperties_EmitWarnings()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("Add an init accessor", StringComparison.Ordinal)
            && diagnostic.Location == $"{typeof(GetOnlyCollectionDto).FullName}.{nameof(GetOnlyCollectionDto.Items)}");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Severity == AtsDiagnosticSeverity.Warning
            && diagnostic.Message.Contains("Add an init accessor", StringComparison.Ordinal)
            && diagnostic.Location == $"{typeof(GetOnlyCollectionDto).FullName}.{nameof(GetOnlyCollectionDto.Metadata)}");
    }

    [Fact]
    public void ScanAssembly_InitDtoProperties_AreOptionalUnlessRequired()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);
        var dto = Assert.Single(result.DtoTypes, d => d.TypeId == AtsTypeMapping.DeriveTypeId(typeof(InitPropertiesDto)));

        Assert.True(Assert.Single(dto.Properties, p => p.Name == nameof(InitPropertiesDto.DisplayName)).IsOptional);
        Assert.True(Assert.Single(dto.Properties, p => p.Name == nameof(InitPropertiesDto.Items)).IsOptional);
        Assert.True(Assert.Single(dto.Properties, p => p.Name == nameof(InitPropertiesDto.Metadata)).IsOptional);
        Assert.False(Assert.Single(dto.Properties, p => p.Name == nameof(InitPropertiesDto.RequiredDisplayName)).IsOptional);
        Assert.False(Assert.Single(dto.Properties, p => p.Name == nameof(InitPropertiesDto.RequiredItems)).IsOptional);
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

    [Fact]
    public void ScanAssembly_DescriptionFallback_PopulatesDocumentationSummaryWhenXmlDocsArePartial()
    {
        var result = AtsCapabilityScanner.ScanAssembly(typeof(AtsCapabilityScannerTests).Assembly);

        var capability = Assert.Single(result.Capabilities,
            c => c.CapabilityId.EndsWith("/descriptionFallback", StringComparison.Ordinal));

        Assert.Equal("Uses the description as fallback documentation.", capability.Description);
        Assert.Equal("Uses the description as fallback documentation.", capability.Documentation?.Summary);
        Assert.Equal("The fallback value.", Assert.Single(capability.Parameters).Documentation?.Summary);
    }

    #endregion

    #region Test Types

    private sealed class TestResource : Resource
    {
        public TestResource(string name) : base(name)
        {
        }
    }

    private sealed class ShadowedEnvironmentResource(string name) : Resource(name), IResourceWithEnvironment;

    private sealed class OtherEnvironmentResource(string name) : Resource(name), IResourceWithEnvironment;

    private enum TestUnionEnum
    {
        First,
        Second
    }

    [AspireDto]
    private sealed class NullableScalarDto
    {
        public string? NullableString { get; set; }

        public string RequiredString { get; set; } = "";

        public int? NullableNumber { get; set; }

        public int RequiredNumber { get; set; }
    }

    [AspireExport(ExposeProperties = true)]
    private class BaseExportedProperties
    {
        public string Name { get; } = "";
    }

    [AspireExport(ExposeProperties = true)]
    private sealed class DerivedExportedProperties : BaseExportedProperties
    {
        public string Framework { get; } = "";
    }

    [AspireExport(ExposeProperties = true)]
    private sealed class InitOnlyExportedProperties
    {
        public required string Name { get; init; }

        public string? Description { get; init; }

        public string MutableLabel { get; set; } = "";
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

        [AspireExport]
        public static void TestUnionEnumParameter(IDistributedApplicationBuilder builder, [AspireUnion(typeof(TestUnionEnum), typeof(string))] object value)
        {
            _ = builder;
            _ = value;
        }

        /// <param name="value">The fallback value.</param>
        [AspireExport("descriptionFallback", Description = "Uses the description as fallback documentation.")]
        public static void DescriptionFallback(IDistributedApplicationBuilder builder, string value)
        {
            _ = builder;
            _ = value;
        }

        [AspireExport("shadowedExporter")]
        public static IResourceBuilder<T> ShadowedExporter<T>(IResourceBuilder<T> builder)
            where T : IResourceWithEnvironment
        {
            return builder;
        }

        [AspireExport("specificShadowedExporter", MethodName = "shadowedExporter")]
        public static IResourceBuilder<ShadowedEnvironmentResource> SpecificShadowedExporter(IResourceBuilder<ShadowedEnvironmentResource> builder)
        {
            return builder;
        }

        [AspireExport("otherEnvironmentProbe")]
        public static IResourceBuilder<OtherEnvironmentResource> OtherEnvironmentProbe(IResourceBuilder<OtherEnvironmentResource> builder)
        {
            return builder;
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

    [AspireDto]
    private sealed class GetOnlyCollectionDto
    {
        public List<string> Items { get; } = [];

        public Dictionary<string, string> Metadata { get; } = [];
    }

    [AspireDto]
    private sealed class InitPropertiesDto
    {
        public string DisplayName { get; init; } = "";

        public List<string> Items { get; init; } = [];

        public Dictionary<string, string> Metadata { get; init; } = [];

        public required string RequiredDisplayName { get; init; }

        public required List<string> RequiredItems { get; init; }
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
