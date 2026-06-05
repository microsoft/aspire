// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.EventHubs;
using Azure.Provisioning;
using Azure.Provisioning.EventHubs;
using Azure.Provisioning.Expressions;
using AzureProvisioning = Azure.Provisioning.EventHubs;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure Event Hubs resources to the application model.
/// </summary>
public static class AzureEventHubsExtensions
{
    private const string EmulatorHealthEndpointName = "emulatorhealth";

    /// <summary>
    /// Adds an Azure Event Hubs Namespace resource to the application model. This resource can be used to create Event Hub resources.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// By default references to the Azure AppEvent Hubs Namespace resource will be assigned the following roles:
    ///
    /// - <see cref="EventHubsBuiltInRole.AzureEventHubsDataOwner"/>
    ///
    /// These can be replaced by calling <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureEventHubsResource}, EventHubsBuiltInRole[])"/>.
    /// </remarks>
    public static IResourceBuilder<AzureEventHubsResource> AddAzureEventHubs(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = static (AzureResourceInfrastructure infrastructure) =>
        {
            var eventHubsNamespace = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = AzureProvisioning.EventHubsNamespace.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) =>
                {
                    var skuParameter = new ProvisioningParameter("sku", typeof(string))
                    {
                        Value = "Standard"
                    };
                    infrastructure.Add(skuParameter);

                    var resource = new AzureProvisioning.EventHubsNamespace(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        DisableLocalAuth = true,
                        Sku = new AzureProvisioning.EventHubsSku()
                        {
                            Name = skuParameter
                        },
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    return resource;
                });

            infrastructure.Add(new ProvisioningOutput("eventHubsEndpoint", typeof(string)) { Value = eventHubsNamespace.ServiceBusEndpoint.ToBicepExpression() });

            // Extract hostname from endpoint: split(replace(endpoint, 'https://', ''), ':')[0]
            var replaceExpr = new FunctionCallExpression(
                new IdentifierExpression("replace"),
                eventHubsNamespace.ServiceBusEndpoint.Compile(),
                new StringLiteralExpression("https://"),
                new StringLiteralExpression(""));
            var splitExpr = new FunctionCallExpression(
                new IdentifierExpression("split"),
                replaceExpr,
                new StringLiteralExpression(":"));
            var hostNameExpr = new IndexExpression(splitExpr, new IntLiteralExpression(0));

            // We need the HostName specifically because the Azure SDK client use it instead of the full endpoint.
            infrastructure.Add(new ProvisioningOutput("eventHubsHostName", typeof(string)) { Value = (BicepValue<string>)hostNameExpr });

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = eventHubsNamespace.Name.ToBicepExpression() });

            var azureResource = (AzureEventHubsResource)infrastructure.AspireResource;

            foreach (var hub in azureResource.Hubs)
            {
                var cdkHub = hub.ToProvisioningEntity();
                cdkHub.Parent = eventHubsNamespace;
                infrastructure.Add(cdkHub);

                foreach (var consumerGroup in hub.ConsumerGroups)
                {
                    var cdkConsumerGroup = consumerGroup.ToProvisioningEntity();
                    cdkConsumerGroup.Parent = cdkHub;
                    infrastructure.Add(cdkConsumerGroup);
                }
            }
        };

        var resource = new AzureEventHubsResource(name, configureInfrastructure);
        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(EventHubsBuiltInRole.GetBuiltInRoleName,
                EventHubsBuiltInRole.AzureEventHubsDataOwner);
    }

    /// <summary>
    /// Adds an Azure Event Hubs hub resource to the application model. This resource requires an <see cref="AzureEventHubsResource"/> to be added to the application model.
    /// </summary>
    /// <param name="builder">The Azure Event Hubs resource builder.</param>
    /// <param name="name">The name of the Event Hub.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use {nameof(AddHub)} instead to add an Azure Event Hub.")]
    public static IResourceBuilder<AzureEventHubsResource> AddEventHub(this IResourceBuilder<AzureEventHubsResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddHub(name);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Event Hubs hub resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Event Hubs resource builder.</param>
    /// <param name="name">The name of the Event Hub resource.</param>
    /// <param name="hubName">The name of the Event Hub. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureEventHubResource> AddHub(this IResourceBuilder<AzureEventHubsResource> builder, [ResourceName] string name, string? hubName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the hub name if it's not provided
        hubName ??= name;

        var hub = new AzureEventHubResource(name, hubName, builder.Resource);
        builder.Resource.Hubs.Add(hub);

        return builder.ApplicationBuilder.AddResource(hub);
    }

    /// <summary>
    /// Allows setting the properties of an Azure Event Hub resource.
    /// </summary>
    /// <param name="builder">The Azure Event Hub resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="AzureEventHubResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureEventHubResource> WithProperties(this IResourceBuilder<AzureEventHubResource> builder, Action<AzureEventHubResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Event Hub Consumer Group resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Event Hub resource builder.</param>
    /// <param name="name">The name of the Event Hub Consumer Group resource.</param>
    /// <param name="groupName">The name of the Consumer Group. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureEventHubConsumerGroupResource> AddConsumerGroup(
        this IResourceBuilder<AzureEventHubResource> builder,
        [ResourceName] string name,
        string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the group name if it's not provided
        groupName ??= name;

        var consumerGroup = new AzureEventHubConsumerGroupResource(name, groupName, builder.Resource);
        builder.Resource.ConsumerGroups.Add(consumerGroup);

        return builder.ApplicationBuilder.AddResource(consumerGroup);
    }

    /// <summary>
    /// Configures an Azure Event Hubs resource to be emulated. This resource requires an <see cref="AzureEventHubsResource"/> to be added to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="EventHubsEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="EventHubsEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="EventHubsEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The Azure Event Hubs resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="EventHubsEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="EventHubsEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="EventHubsEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <example>
    /// The following example creates an Azure Event Hubs resource that runs locally is an emulator and referencing that
    /// resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var eventHub = builder.AddAzureEventHubs("eventhubns")
    ///    .RunAsEmulator()
    ///    .AddEventHub("hub");
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;()
    ///        .WithReference(eventHub);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IResourceBuilder<AzureEventHubsResource> RunAsEmulator(this IResourceBuilder<AzureEventHubsResource> builder, Action<IResourceBuilder<AzureEventHubsEmulatorResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.IsEmulator)
        {
            throw new InvalidOperationException("The Azure Event Hubs resource is already configured to run as an emulator.");
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        builder
            .WithEndpoint(name: "emulator", targetPort: 5672)
            .WithHttpEndpoint(name: EmulatorHealthEndpointName, targetPort: 5300)
            .WithHttpHealthCheck(endpointName: EmulatorHealthEndpointName, path: "/health")
            .WithAnnotation(new ContainerImageAnnotation
            {
                Registry = EventHubsEmulatorContainerImageTags.Registry,
                Image = EventHubsEmulatorContainerImageTags.Image,
                Tag = EventHubsEmulatorContainerImageTags.Tag
            })
            .WithUrlForEndpoint(EmulatorHealthEndpointName, u => u.DisplayLocation = UrlDisplayLocation.DetailsOnly);

        // Create a separate storage emulator for the Event Hub one
        var storageResource = builder.ApplicationBuilder
                .AddAzureStorage($"{builder.Resource.Name}-storage")
                .WithParentRelationship(builder);

        var lifetime = ContainerLifetime.Session;

        // Copy the lifetime from the main resource to the storage resource
        var surrogate = new AzureEventHubsEmulatorResource(builder.Resource);
        var surrogateBuilder = builder.ApplicationBuilder.CreateResourceBuilder(surrogate);
        if (configureContainer != null)
        {
            configureContainer(surrogateBuilder);

            if (surrogate.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var lifetimeAnnotation))
            {
                lifetime = lifetimeAnnotation.Lifetime;
            }
        }

        storageResource = storageResource.RunAsEmulator(c => c.WithLifetime(lifetime));

        var storage = storageResource.Resource;

        builder.WithAnnotation(new EnvironmentCallbackAnnotation((EnvironmentCallbackContext context) =>
        {
            var blobEndpoint = storage.GetEndpoint("blob");
            var tableEndpoint = storage.GetEndpoint("table");

            context.EnvironmentVariables["ACCEPT_EULA"] = "Y";
            context.EnvironmentVariables["BLOB_SERVER"] = $"{blobEndpoint.Resource.Name}:{blobEndpoint.TargetPort}";
            context.EnvironmentVariables["METADATA_SERVER"] = $"{tableEndpoint.Resource.Name}:{tableEndpoint.TargetPort}";
        }));

        // RunAsEmulator() can be followed by custom model configuration so we need to delay the creation of the Config.json file
        // until all resources are about to be prepared and annotations can't be updated anymore.
        surrogateBuilder.WithContainerFiles(
            AzureEventHubsEmulatorResource.EmulatorConfigFilesPath,
            (_, _) =>
            {
                var customConfigFile = builder.Resource.Annotations.OfType<ConfigFileAnnotation>().FirstOrDefault();
                if (customConfigFile != null)
                {
                    return Task.FromResult<IEnumerable<ContainerFileSystemItem>>([
                        new ContainerFile
                        {
                            Name = AzureEventHubsEmulatorResource.EmulatorConfigJsonFile,
                            SourcePath = customConfigFile.SourcePath,
                        },
                    ]);
                }

                // Create default Config.json file content
                var tempConfig = JsonNode.Parse(CreateEmulatorConfigJson(builder.Resource));

                if (tempConfig == null)
                {
                    throw new InvalidOperationException("The configuration file mount could not be parsed.");
                }

                // Apply ConfigJsonAnnotation modifications
                var configJsonAnnotations = builder.Resource.Annotations.OfType<ConfigJsonAnnotation>();

                if (configJsonAnnotations.Any())
                {
                    foreach (var annotation in configJsonAnnotations)
                    {
                        annotation.Configure(tempConfig);
                    }
                }

                using var writeStream = new MemoryStream();
                using var writer = new Utf8JsonWriter(writeStream, new JsonWriterOptions { Indented = true });
                tempConfig.WriteTo(writer);

                writer.Flush();

                return Task.FromResult<IEnumerable<ContainerFileSystemItem>>([
                    new ContainerFile
                    {
                        Name = AzureEventHubsEmulatorResource.EmulatorConfigJsonFile,
                        Contents = Encoding.UTF8.GetString(writeStream.ToArray()),
                    },
                ]);
            });

        return builder;
    }

    /// <summary>
    /// Adds a bind mount for the data folder to an Azure Event Hubs emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureEventHubsEmulatorResource"/>.</param>
    /// <param name="path">Relative path to the AppHost where emulator storage is persisted between runs. Defaults to the path '.eventhubs/{builder.Resource.Name}'</param>
    /// <returns>A builder for the <see cref="AzureEventHubsEmulatorResource"/>.</returns>
    [Obsolete($"This method is obsolete because it doesn't work as intended and will be removed in a future version.")]
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithDataBindMount(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithBindMount(path ?? $".eventhubs/{builder.Resource.Name}", "/data", isReadOnly: false);
    }

    /// <summary>
    /// Adds a named volume for the data folder to an Azure Event Hubs emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureEventHubsEmulatorResource"/>.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <returns>A builder for the <see cref="AzureEventHubsEmulatorResource"/>.</returns>
    [Obsolete($"This method is obsolete because it doesn't work as intended and will be removed in a future version.")]
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithDataVolume(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/data", isReadOnly: false);
    }

    /// <summary>
    /// Configures the host port for the Azure Event Hubs emulator is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">Builder for the Azure Event Hubs emulator container</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used, a random port will be assigned.</param>
    /// <returns>Azure Event Hubs emulator resource builder.</returns>
    [Obsolete("Use WithHostPort instead.")]
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithGatewayPort(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return WithHostPort(builder, port);
    }

    /// <summary>
    /// Configures the host port for the Azure Event Hubs emulator is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">Builder for the Azure Event Hubs emulator container</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used, a random port will be assigned.</param>
    /// <returns>Azure Event Hubs emulator resource builder.</returns>
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithHostPort(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("emulator", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a bind mount for the configuration file of an Azure Event Hubs emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureEventHubsEmulatorResource"/>.</param>
    /// <param name="path">Path to the file on the AppHost where the emulator configuration is located.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithConfigurationFile(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return builder.WithAnnotation(new ConfigFileAnnotation(path), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Alters the JSON configuration document used by the emulator.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureEventHubsEmulatorResource"/>.</param>
    /// <param name="configJson">A callback to update the JSON object representation of the configuration.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureEventHubsEmulatorResource> WithConfiguration(this IResourceBuilder<AzureEventHubsEmulatorResource> builder, Action<JsonNode> configJson)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configJson);

        builder.WithAnnotation(new ConfigJsonAnnotation(configJson));

        return builder;
    }

    private static string CreateEmulatorConfigJson(AzureEventHubsResource emulatorResource)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();                      // {
        writer.WriteStartObject("UserConfig");          //   "UserConfig": {
        writer.WriteStartArray("NamespaceConfig");      //     "NamespaceConfig": [
        writer.WriteStartObject();                      //       {
        writer.WriteString("Type", "EventHub");

        // This name is currently required by the emulator
        writer.WriteString("Name", "emulatorNs1");
        writer.WriteStartArray("Entities");             //         "Entities": [

        foreach (var hub in emulatorResource.Hubs)
        {
            writer.WriteStartObject();
            hub.WriteJsonObjectProperties(writer);
            writer.WriteEndObject();                    //           }
        }

        writer.WriteEndArray();                         //         ] (/Entities)
        writer.WriteEndObject();                        //       }
        writer.WriteEndArray();                         //     ], (/NamespaceConfig)
        writer.WriteStartObject("LoggingConfig");       //     "LoggingConfig": {
        writer.WriteString("Type", "File");             //       "Type": "File"
        writer.WriteEndObject();                        //     } (/LoggingConfig)

        writer.WriteEndObject();                        //   } (/UserConfig)
        writer.WriteEndObject();                        // } (/Root)

        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Event Hubs Namespace resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Event Hubs Namespace resource.</param>
    /// <param name="roles">The built-in Event Hubs roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <example>
    /// Assigns the AzureEventHubsDataSender role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var eventHubs = builder.AddAzureEventHubs("eventHubs");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(eventHubs, EventHubsBuiltInRole.AzureEventHubsDataSender)
    ///   .WithReference(eventHubs);
    /// </code>
    /// </example>
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureEventHubsResource> target,
        params EventHubsBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, EventHubsBuiltInRole.GetBuiltInRoleName, roles);
    }
}
