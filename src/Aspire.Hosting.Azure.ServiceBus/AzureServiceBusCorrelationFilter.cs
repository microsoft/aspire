// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the correlation filter expression.
/// </summary>
[AspireDto]
public class AzureServiceBusCorrelationFilter
{
    /// <summary>
    /// Represents the correlation filter expression.
    /// </summary>
    public AzureServiceBusCorrelationFilter()
    {
    }

    /// <summary>
    /// Dictionary object for custom filters.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = [];

    /// <summary>
    /// Identifier of the correlation.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Identifier of the message.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// Address to send to.
    /// </summary>
    public string? SendTo { get; set; }

    /// <summary>
    /// Address of the queue to reply to.
    /// </summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Application specific label.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Session identifier.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Session identifier to reply to.
    /// </summary>
    public string? ReplyToSessionId { get; set; }

    /// <summary>
    /// Content type of the message.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Value that indicates whether the rule action requires preprocessing.
    /// </summary>
    public bool? RequiresPreprocessing { get; set; }
}
