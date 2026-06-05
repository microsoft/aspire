// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.ServiceBus;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.ServiceBus;
using AzureProvisioning = Azure.Provisioning.ServiceBus;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure Service Bus resources to the application model.
/// </summary>
public static class AzureServiceBusExtensions
{
    private const string EmulatorHealthEndpointName = "emulatorhealth";

    /// <summary>
    /// Adds an Azure Service Bus Namespace resource to the application model. This resource can be used to create queue, topic, and subscription resources.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// By default references to the Azure Service Bus resource will be assigned the following roles:
    ///
    /// - <see cref="ServiceBusBuiltInRole.AzureServiceBusDataOwner"/>
    ///
    /// These can be replaced by calling <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureServiceBusResource}, ServiceBusBuiltInRole[])"/>.
    /// </remarks>
    public static IResourceBuilder<AzureServiceBusResource> AddAzureServiceBus(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = static (AzureResourceInfrastructure infrastructure) =>
        {
            AzureProvisioning.ServiceBusNamespace serviceBusNamespace = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = AzureProvisioning.ServiceBusNamespace.FromExisting(identifier);
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
                    var resource = new AzureProvisioning.ServiceBusNamespace(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Sku = new AzureProvisioning.ServiceBusSku()
                        {
                            Name = skuParameter
                        },
                        DisableLocalAuth = true,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    return resource;
                });

            infrastructure.Add(new ProvisioningOutput("serviceBusEndpoint", typeof(string)) { Value = serviceBusNamespace.ServiceBusEndpoint.ToBicepExpression() });

            // Extract hostname from endpoint: split(replace(endpoint, 'https://', ''), ':')[0]
            var replaceExpr = new FunctionCallExpression(
                new IdentifierExpression("replace"),
                serviceBusNamespace.ServiceBusEndpoint.Compile(),
                new StringLiteralExpression("https://"),
                new StringLiteralExpression(""));
            var splitExpr = new FunctionCallExpression(
                new IdentifierExpression("split"),
                replaceExpr,
                new StringLiteralExpression(":"));
            var hostNameExpr = new IndexExpression(splitExpr, new IntLiteralExpression(0));

            // We need the HostName specifically because the Azure SDK client use it instead of the full endpoint.
            infrastructure.Add(new ProvisioningOutput("serviceBusHostName", typeof(string)) { Value = (BicepValue<string>)hostNameExpr });

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = serviceBusNamespace.Name.ToBicepExpression() });

            var azureResource = (AzureServiceBusResource)infrastructure.AspireResource;

            foreach (var queue in azureResource.Queues)
            {
                var cdkQueue = queue.ToProvisioningEntity();
                cdkQueue.Parent = serviceBusNamespace;
                infrastructure.Add(cdkQueue);
            }

            foreach (var topic in azureResource.Topics)
            {
                var cdkTopic = topic.ToProvisioningEntity();
                cdkTopic.Parent = serviceBusNamespace;
                infrastructure.Add(cdkTopic);

                foreach (var subscription in topic.Subscriptions)
                {
                    var cdkSubscription = subscription.ToProvisioningEntity();
                    cdkSubscription.Parent = cdkTopic;
                    infrastructure.Add(cdkSubscription);

                    foreach (var rule in subscription.Rules)
                    {
                        var cdkRule = rule.ToProvisioningEntity();
                        cdkRule.Parent = cdkSubscription;
                        infrastructure.Add(cdkRule);
                    }
                }
            }
        };

        var resource = new AzureServiceBusResource(name, configureInfrastructure);
        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(ServiceBusBuiltInRole.GetBuiltInRoleName,
                ServiceBusBuiltInRole.AzureServiceBusDataOwner);
    }

    /// <summary>
    /// Adds an Azure Service Bus Queue resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="name">The name of the queue resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use {nameof(AddServiceBusQueue)} instead to add an Azure Service Bus Queue.")]
    public static IResourceBuilder<AzureServiceBusResource> AddQueue(this IResourceBuilder<AzureServiceBusResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddServiceBusQueue(name);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Queue resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="name">The name of the queue resource.</param>
    /// <param name="queueName">The name of the Service Bus Queue. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusQueueResource> AddServiceBusQueue(this IResourceBuilder<AzureServiceBusResource> builder, [ResourceName] string name, string? queueName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the queue name if it's not provided
        queueName ??= name;

        var queue = new AzureServiceBusQueueResource(name, queueName, builder.Resource);
        builder.Resource.Queues.Add(queue);

        return builder.ApplicationBuilder.AddResource(queue);
    }

    /// <summary>
    /// Allows setting the properties of an Azure Service Bus Queue resource.
    /// </summary>
    /// <param name="builder">The Azure Service Bus Queue resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="AzureServiceBusQueueResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusQueueResource> WithProperties(this IResourceBuilder<AzureServiceBusQueueResource> builder, Action<AzureServiceBusQueueResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Topic resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="name">The name of the topic resource.</param>
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use {nameof(AddServiceBusTopic)} instead to add an Azure Service Bus Topic.")]
    public static IResourceBuilder<AzureServiceBusResource> AddTopic(this IResourceBuilder<AzureServiceBusResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddServiceBusTopic(name);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Topic resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="name">The name of the topic resource.</param>
    /// <param name="subscriptions">The name of the subscriptions.</param>
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use {nameof(AddServiceBusTopic)} and {nameof(AddServiceBusSubscription)} instead to add an Azure Service Bus Topic and Subscriptions.")]
    public static IResourceBuilder<AzureServiceBusResource> AddTopic(this IResourceBuilder<AzureServiceBusResource> builder, [ResourceName] string name, string[] subscriptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(subscriptions);

        var topic = builder.AddServiceBusTopic(name);

        foreach (var subscription in subscriptions)
        {
            ArgumentException.ThrowIfNullOrEmpty(subscription);
            topic.AddServiceBusSubscription(subscription);
        }

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Topic resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="name">The name of the topic resource.</param>
    /// <param name="topicName">The name of the Service Bus Topic. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusTopicResource> AddServiceBusTopic(this IResourceBuilder<AzureServiceBusResource> builder, [ResourceName] string name, string? topicName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the topic name if it's not provided
        topicName ??= name;

        var topic = new AzureServiceBusTopicResource(name, topicName, builder.Resource);
        builder.Resource.Topics.Add(topic);

        return builder.ApplicationBuilder.AddResource(topic);
    }

    /// <summary>
    /// Allows setting the properties of an Azure Service Bus Topic resource.
    /// </summary>
    /// <param name="builder">The Azure Service Bus Topic resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="AzureServiceBusTopicResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusTopicResource> WithProperties(this IResourceBuilder<AzureServiceBusTopicResource> builder, Action<AzureServiceBusTopicResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Subscription resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="topicName">The name of the topic resource.</param>
    /// <param name="subscriptionName">The name of the subscription.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AddServiceBusSubscription)} instead to add an Azure Service Bus Subscription to a Topic.")]
    public static IResourceBuilder<AzureServiceBusResource> AddSubscription(this IResourceBuilder<AzureServiceBusResource> builder, string topicName, string subscriptionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(topicName);
        ArgumentException.ThrowIfNullOrEmpty(subscriptionName);

        IResourceBuilder<AzureServiceBusTopicResource> topicBuilder;
        if (builder.Resource.Topics.FirstOrDefault(x => x.Name == topicName) is { } existingResource)
        {
            topicBuilder = builder.ApplicationBuilder.CreateResourceBuilder(existingResource);
        }
        else
        {
            topicBuilder = builder.AddServiceBusTopic(topicName);
        }

        topicBuilder.AddServiceBusSubscription(subscriptionName);

        return builder;
    }

    /// <summary>
    /// Adds an Azure Service Bus Subscription resource to the application model.
    /// </summary>
    /// <param name="builder">The Azure Service Bus Topic resource builder.</param>
    /// <param name="name">The name of the subscription resource.</param>
    /// <param name="subscriptionName">The name of the Service Bus Subscription. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusSubscriptionResource> AddServiceBusSubscription(this IResourceBuilder<AzureServiceBusTopicResource> builder, [ResourceName] string name, string? subscriptionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the subscription name if it's not provided
        subscriptionName ??= name;

        var subscription = new AzureServiceBusSubscriptionResource(name, subscriptionName, builder.Resource);
        builder.Resource.Subscriptions.Add(subscription);

        return builder.ApplicationBuilder.AddResource(subscription);
    }

    /// <summary>
    /// Allows setting the properties of an Azure Service Bus Subscription resource.
    /// </summary>
    /// <param name="builder">The Azure Service Bus Subscription resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="AzureServiceBusSubscriptionResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusSubscriptionResource> WithProperties(this IResourceBuilder<AzureServiceBusSubscriptionResource> builder, Action<AzureServiceBusSubscriptionResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Configures an Azure Service Bus resource to be emulated. This resource requires an <see cref="AzureServiceBusResource"/> to be added to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="ServiceBusEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="ServiceBusEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="ServiceBusEmulatorContainerImageTags.Image"/> container image.
    /// <param name="builder">The Azure Service Bus resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// The following example creates an Azure Service Bus resource that runs locally is an emulator and referencing that
    /// resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var serviceBus = builder.AddAzureServiceBus("myservicebus")
    ///    .RunAsEmulator()
    ///    .AddQueue("queue");
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;()
    ///        .WithReference(serviceBus);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureServiceBusResource> RunAsEmulator(this IResourceBuilder<AzureServiceBusResource> builder, Action<IResourceBuilder<AzureServiceBusEmulatorResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.IsEmulator)
        {
            throw new InvalidOperationException("The Azure Service Bus resource is already configured to run as an emulator.");
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        // Add emulator container

        // The password must be at least 8 characters long and contain characters from three of the following four sets: Uppercase letters, Lowercase letters, Base 10 digits, and Symbols
        var passwordParameter = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder.ApplicationBuilder, $"{builder.Resource.Name}-sql-pwd", minLower: 1, minUpper: 1, minNumeric: 1);

        builder
            .WithEndpoint(name: "emulator", targetPort: 5672)
            .WithHttpEndpoint(name: EmulatorHealthEndpointName, targetPort: 5300)
            .WithAnnotation(new ContainerImageAnnotation
            {
                Registry = ServiceBusEmulatorContainerImageTags.Registry,
                Image = ServiceBusEmulatorContainerImageTags.Image,
                Tag = ServiceBusEmulatorContainerImageTags.Tag
            })
            .WithUrlForEndpoint(EmulatorHealthEndpointName, u => u.DisplayLocation = UrlDisplayLocation.DetailsOnly);

        var sqlServerResource = builder.ApplicationBuilder
                .AddContainer($"{builder.Resource.Name}-mssql",
                    image: ServiceBusEmulatorContainerImageTags.SqlServerImage,
                    tag: ServiceBusEmulatorContainerImageTags.SqlServerTag)
                .WithImageRegistry(ServiceBusEmulatorContainerImageTags.SqlServerRegistry)
                .WithEndpoint(targetPort: 1433, name: "tcp")
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEnvironment(context =>
                {
                    context.EnvironmentVariables["MSSQL_SA_PASSWORD"] = passwordParameter;
                })
                .WithParentRelationship(builder);

        builder.WithAnnotation(new EnvironmentCallbackAnnotation((EnvironmentCallbackContext context) =>
        {
            var sqlEndpoint = sqlServerResource.Resource.GetEndpoint("tcp");

            context.EnvironmentVariables["ACCEPT_EULA"] = "Y";
            context.EnvironmentVariables["SQL_SERVER"] = $"{sqlEndpoint.Resource.Name}:{sqlEndpoint.TargetPort}";
            context.EnvironmentVariables["MSSQL_SA_PASSWORD"] = passwordParameter;
        }));

        var lifetime = ContainerLifetime.Session;

        var surrogate = new AzureServiceBusEmulatorResource(builder.Resource);
        var surrogateBuilder = builder.ApplicationBuilder.CreateResourceBuilder(surrogate);

        if (configureContainer != null)
        {
            configureContainer(surrogateBuilder);

            if (surrogate.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var lifetimeAnnotation))
            {
                lifetime = lifetimeAnnotation.Lifetime;
            }
        }

        sqlServerResource = sqlServerResource.WithLifetime(lifetime);

        // RunAsEmulator() can be followed by custom model configuration so we need to delay the creation of the Config.json file
        // until all resources are about to be prepared and annotations can't be updated anymore.
        surrogateBuilder.WithContainerFiles(
            AzureServiceBusEmulatorResource.EmulatorConfigFilesPath,
            (_, _) =>
            {
                var customConfigFile = builder.Resource.Annotations.OfType<ConfigFileAnnotation>().FirstOrDefault();
                if (customConfigFile != null)
                {
                    return Task.FromResult<IEnumerable<ContainerFileSystemItem>>([
                        new ContainerFile
                        {
                            Name = AzureServiceBusEmulatorResource.EmulatorConfigJsonFile,
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
                        Name = AzureServiceBusEmulatorResource.EmulatorConfigJsonFile,
                        Contents = Encoding.UTF8.GetString(writeStream.ToArray()),
                    },
                ]);
            }
        );

        builder.WithHttpHealthCheck(endpointName: EmulatorHealthEndpointName, path: "/health");

        return builder;
    }

    /// <summary>
    /// Copies the configuration file into an Azure Service Bus emulator resource.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureServiceBusEmulatorResource"/>.</param>
    /// <param name="path">Path to the file on the AppHost where the emulator configuration is located.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusEmulatorResource> WithConfigurationFile(this IResourceBuilder<AzureServiceBusEmulatorResource> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return builder.WithAnnotation(new ConfigFileAnnotation(path), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Alters the JSON configuration document used by the emulator.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="AzureServiceBusEmulatorResource"/>.</param>
    /// <param name="configJson">A callback to update the JSON object representation of the configuration.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Here is an example of how to configure the emulator to use a different logging mechanism:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddAzureServiceBus("servicebusns")
    ///        .RunAsEmulator(configure => configure
    ///            .WithConfiguration(document =>
    ///            {
    ///                document["UserConfig"]!["Logging"] = new JsonObject { ["Type"] = "Console" };
    ///            });
    ///        );
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<AzureServiceBusEmulatorResource> WithConfiguration(this IResourceBuilder<AzureServiceBusEmulatorResource> builder, Action<JsonNode> configJson)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configJson);

        builder.WithAnnotation(new ConfigJsonAnnotation(configJson));

        return builder;
    }

    /// <summary>
    /// Configures the host port for the Azure Service Bus emulator is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">Builder for the Azure Service Bus emulator container</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used, a random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureServiceBusEmulatorResource> WithHostPort(this IResourceBuilder<AzureServiceBusEmulatorResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("emulator", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    private static string CreateEmulatorConfigJson(AzureServiceBusResource emulatorResource)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();                      // {
        writer.WriteStartObject("UserConfig");          //   "UserConfig": {
        writer.WriteStartArray("Namespaces");           //     "Namespaces": [
        writer.WriteStartObject();                      //       {
        writer.WriteString("Name", emulatorResource.Name);
        writer.WriteStartArray("Queues");               //         "Queues": [

        foreach (var queue in emulatorResource.Queues)
        {
            writer.WriteStartObject();
            queue.WriteJsonObjectProperties(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();                         //         ] (/Queues)

        writer.WriteStartArray("Topics");               //         "Topics": [
        foreach (var topic in emulatorResource.Topics)
        {
            writer.WriteStartObject();                  //           "{ (Topic)"
            topic.WriteJsonObjectProperties(writer);

            writer.WriteStartArray("Subscriptions");    //             "Subscriptions": [
            foreach (var subscription in topic.Subscriptions)
            {
                writer.WriteStartObject();              //               "{ (Subscription)"
                subscription.WriteJsonObjectProperties(writer);

                writer.WriteStartArray("Rules");        //                 "Rules": [
                foreach (var rule in subscription.Rules)
                {
                    writer.WriteStartObject();
                    rule.WriteJsonObjectProperties(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();                 //                  ] (/Rules)

                writer.WriteEndObject();                //               } (/Subscription)
            }

            writer.WriteEndArray();                     //             ] (/Subscriptions)

            writer.WriteEndObject();                    //           } (/Topic)
        }
        writer.WriteEndArray();                         //         ] (/Topics)

        writer.WriteEndObject();                        //       } (/Namespace)
        writer.WriteEndArray();                         //     ], (/Namespaces)
        writer.WriteStartObject("Logging");             //     "Logging": {
        writer.WriteString("Type", "File");             //       "Type": "File"
        writer.WriteEndObject();                        //     } (/LoggingConfig)

        writer.WriteEndObject();                        //   } (/UserConfig)
        writer.WriteEndObject();                        // } (/Root)

        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Service Bus namespace. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Service Bus namespace.</param>
    /// <param name="roles">The built-in Service Bus roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <remarks>
    /// <example>
    /// Assigns the AzureServiceBusDataSender role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var sb = builder.AddAzureServiceBus("bus");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(sb, ServiceBusBuiltInRole.AzureServiceBusDataSender)
    ///   .WithReference(sb);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureServiceBusResource> target,
        params ServiceBusBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, ServiceBusBuiltInRole.GetBuiltInRoleName, roles);
    }
}
