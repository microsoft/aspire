// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a Service Bus Topic.
/// Initializes a new instance of the <see cref="AzureServiceBusTopicResource"/> class.
/// </summary>
/// <remarks>
/// Use <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}(ApplicationModel.IResourceBuilder{T}, Action{AzureResourceInfrastructure})"/> to configure specific <see cref="Azure.Provisioning"/> properties.
/// </remarks>
public class AzureServiceBusTopicResource(string name, string topicName, AzureServiceBusResource parent)
    : Resource(name), IResourceWithParent<AzureServiceBusResource>, IResourceWithConnectionString, IResourceWithAzureFunctionsConfig
{
    private string _topicName = ThrowIfNullOrEmpty(topicName);

    /// <summary>
    /// The topic name.
    /// </summary>
    public string TopicName
    {
        get => _topicName;
        set => _topicName = ThrowIfNullOrEmpty(value, nameof(topicName));
    }

    /// <summary>
    /// Gets the parent Azure Service Bus resource.
    /// </summary>
    public AzureServiceBusResource Parent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the connection string expression for the Azure Service Bus Topic.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetConnectionString(TopicName, null);

    /// <summary>
    /// ISO 8601 default message timespan to live value. This is the duration
    /// after which the message expires, starting from when the message is
    /// sent to Service Bus. This is the default value used when TimeToLive is
    /// not set on a message itself.
    /// </summary>
    public TimeSpan? DefaultMessageTimeToLive { get; set; }

    /// <summary>
    /// ISO 8601 timeSpan structure that defines the duration of the duplicate
    /// detection history. The default value is 10 minutes.
    /// </summary>
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; set; }

    /// <summary>
    /// A value indicating if this topic requires duplicate detection.
    /// </summary>
    public bool? RequiresDuplicateDetection { get; set; }

    /// <summary>
    /// The subscriptions for this topic.
    /// </summary>
    internal List<AzureServiceBusSubscriptionResource> Subscriptions { get; } = [];

    // ensure Azure Functions projects can WithReference a ServiceBus topic
    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
        => Parent.ApplyAzureFunctionsConfiguration(target, connectionName, TopicName);

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    /// <returns>A <see cref="global::Azure.Provisioning.ServiceBus.ServiceBusTopic"/> instance.</returns>
    internal global::Azure.Provisioning.ServiceBus.ServiceBusTopic ToProvisioningEntity()
    {
        var topic = new global::Azure.Provisioning.ServiceBus.ServiceBusTopic(Infrastructure.NormalizeBicepIdentifier(Name));

        if (TopicName != null)
        {
            topic.Name = TopicName;
        }

        if (DefaultMessageTimeToLive.HasValue)
        {
            topic.DefaultMessageTimeToLive = DefaultMessageTimeToLive.Value;
        }
        if (DuplicateDetectionHistoryTimeWindow.HasValue)
        {
            topic.DuplicateDetectionHistoryTimeWindow = DuplicateDetectionHistoryTimeWindow.Value;
        }
        if (RequiresDuplicateDetection.HasValue)
        {
            topic.RequiresDuplicateDetection = RequiresDuplicateDetection.Value;
        }
        return topic;
    }

    /// <summary>
    /// Converts the current instance to a JSON object.
    /// </summary>
    /// <param name="writer">The Utf8JsonWriter to write the JSON object to.</param>
    internal void WriteJsonObjectProperties(Utf8JsonWriter writer)
    {
        var topic = this;

        if (topic.Name != null)
        {
            writer.WriteString(nameof(Name), topic.TopicName);
        }
        writer.WriteStartObject("Properties");

        if (topic.DefaultMessageTimeToLive.HasValue)
        {
            writer.WriteString(nameof(DefaultMessageTimeToLive), XmlConvert.ToString(topic.DefaultMessageTimeToLive.Value));
        }
        if (topic.DuplicateDetectionHistoryTimeWindow.HasValue)
        {
            writer.WriteString(nameof(DuplicateDetectionHistoryTimeWindow), XmlConvert.ToString(topic.DuplicateDetectionHistoryTimeWindow.Value));
        }
        if (topic.RequiresDuplicateDetection.HasValue)
        {
            writer.WriteBoolean(nameof(RequiresDuplicateDetection), topic.RequiresDuplicateDetection.Value);
        }

        writer.WriteEndObject();
    }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        foreach (var property in ((IResourceWithConnectionString)Parent).GetConnectionProperties())
        {
            yield return property;
        }

        yield return new("TopicName", ReferenceExpression.Create($"{TopicName}"));
    }
}
