// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

/// <summary>
/// Defines the update configuration settings for service deployments in a Docker Swarm environment.
/// </summary>
/// <remarks>
/// This class provides various configurable options for updating services, including parallelism,
/// delay between updates, failure actions, monitoring settings, failure ratio limits, and the order of updates.
/// </remarks>
[YamlSerializable]
public sealed class UpdateConfig
{
    /// <summary>
    /// Gets or sets the level of parallelism applied during the update process.
    /// This property specifies the maximum number of service tasks that can
    /// be updated simultaneously.
    /// </summary>
    [YamlMember(Alias = "parallelism")]
    public int? Parallelism { get; set; }

    /// <summary>
    /// Represents the delay between each update operation for a service node in a swarm configuration.
    /// </summary>
    [YamlMember(Alias = "delay")]
    public string? Delay { get; set; }

    /// <summary>
    /// Gets or sets the action to take when an update fails.
    /// Valid values are "continue", "rollback", or "pause" (default).
    /// </summary>
    [YamlMember(Alias = "failure_action")]
    public string? FailureAction { get; set; }

    /// <summary>
    /// Gets or sets the duration or interval for monitoring the progress of an update.
    /// This property is typically used to specify the time span the system will
    /// monitor the update process before determining its status (success or failure).
    /// </summary>
    [YamlMember(Alias = "monitor")]
    public string? Monitor { get; set; }

    /// <summary>
    /// Gets or sets the maximum failure ratio allowed during the update process.
    /// This property specifies the threshold for the ratio of failed tasks
    /// over the total number of tasks during a service update. If the failure
    /// ratio exceeds this value, the update will be rolled back or stopped
    /// depending on the configured failure action.
    /// </summary>
    [YamlMember(Alias = "max_failure_ratio")]
    public string? MaxFailureRatio { get; set; }

    /// <summary>
    /// Represents the execution order of service updates during the update process.
    /// Specifies the sequence in which the update operations are applied (e.g., "start-first" or "stop-first").
    /// </summary>
    [YamlMember(Alias = "order")]
    public string? Order { get; set; }
}
