#pragma warning disable ASPIREPIPELINES001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ============================================================================
// ATS Type Exports for Aspire.Hosting
// ============================================================================
// These assembly-level attributes mark types as ATS exports.
// Type IDs are automatically derived as {AssemblyName}/{TypeName}.

// Core types (from Aspire.Hosting namespace)
[assembly: AspireExport(typeof(IDistributedApplicationBuilder))]
[assembly: AspireExport(typeof(IDistributedApplicationPipeline))]
[assembly: AspireExport(typeof(DistributedApplication))]

// Note: DistributedApplicationExecutionContext has [AspireExport(ExposeProperties = true)] on the type itself

// Reference types (from Aspire.Hosting.ApplicationModel namespace)
[assembly: AspireExport(typeof(EndpointReference))]
[assembly: AspireExport(typeof(ReferenceExpression))]
[assembly: AspireExport(typeof(IAspireStore), ExposeProperties = true)]
[assembly: AspireExport(typeof(IExecutionConfigurationBuilder))]
[assembly: AspireExport(typeof(IExecutionConfigurationResult))]

// Note: EnvironmentCallbackContext has [AspireExport(ExposeProperties = true)] on the type itself

// Resource interfaces (from Aspire.Hosting.ApplicationModel namespace)
[assembly: AspireExport(typeof(IResource))]
[assembly: AspireExport(typeof(IResourceWithEnvironment))]
[assembly: AspireExport(typeof(IResourceWithEndpoints))]
[assembly: AspireExport(typeof(IResourceWithArgs))]
[assembly: AspireExport(typeof(IResourceWithConnectionString))]
[assembly: AspireExport(typeof(IResourceWithWaitSupport))]
[assembly: AspireExport(typeof(IResourceWithParent))]

// Concrete resource types (from Aspire.Hosting namespace)
[assembly: AspireExport(typeof(ContainerResource))]
[assembly: AspireExport(typeof(ExecutableResource))]
[assembly: AspireExport(typeof(ProjectResource))]
[assembly: AspireExport(typeof(ParameterResource))]
[assembly: AspireExport(typeof(ContainerMountAnnotation), ExposeProperties = true)]
[assembly: AspireExport(typeof(ContainerImageReference), ExposeProperties = true)]
[assembly: AspireExport(typeof(ContainerPortReference), ExposeProperties = true)]

// Service types
[assembly: AspireExport(typeof(IServiceProvider))]
[assembly: AspireExport(typeof(ResourceNotificationService))]
[assembly: AspireExport(typeof(ResourceLoggerService))]

// Additional framework and hosting types we reference
[assembly: AspireExport(typeof(IConfiguration))]
[assembly: AspireExport(typeof(IConfigurationSection), ExposeProperties = true)]
[assembly: AspireExport(typeof(IHostEnvironment), ExposeProperties = true)]
[assembly: AspireExport(typeof(ILogger))]
[assembly: AspireExport(typeof(ILoggerFactory))]
[assembly: AspireExport(typeof(CancellationToken))]
[assembly: AspireExport(typeof(IReportingStep))]
[assembly: AspireExport(typeof(IReportingTask))]

// Eventing types
[assembly: AspireExport(typeof(DistributedApplicationEventSubscription))]
