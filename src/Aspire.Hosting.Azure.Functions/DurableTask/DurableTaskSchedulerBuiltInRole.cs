// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Represents built-in Azure Durable Task Scheduler roles that can be assigned to identities.
/// </summary>
public readonly struct DurableTaskSchedulerBuiltInRole : IEquatable<DurableTaskSchedulerBuiltInRole>
{
    private readonly string _value;

    private DurableTaskSchedulerBuiltInRole(string value)
    {
        _value = value;
    }

    /// <summary>
    /// Durable Task Data Contributor role - provides full access to durable task data operations.
    /// Role ID: 0ad04412-c4d5-4796-b79c-f76d14c8d402
    /// </summary>
    public static DurableTaskSchedulerBuiltInRole DurableTaskDataContributor { get; } = new("0ad04412-c4d5-4796-b79c-f76d14c8d402");

    /// <summary>
    /// Durable Task Data Reader role - provides read-only access to durable task data.
    /// Role ID: d6a5505f-6ebb-45a4-896e-ac8274cfc0ac
    /// </summary>
    public static DurableTaskSchedulerBuiltInRole DurableTaskDataReader { get; } = new("d6a5505f-6ebb-45a4-896e-ac8274cfc0ac");

    /// <summary>
    /// Durable Task Worker role - provides access to execute durable task orchestrations and activities.
    /// Role ID: 80d0d6b0-f522-40a4-8886-a5a11720c375
    /// </summary>
    public static DurableTaskSchedulerBuiltInRole DurableTaskWorker { get; } = new("80d0d6b0-f522-40a4-8886-a5a11720c375");

    /// <summary>
    /// Gets the role ID as a string.
    /// </summary>
    /// <returns>The role ID.</returns>
    public override string ToString() => _value;

    /// <summary>
    /// Gets the human-readable name for the built-in role.
    /// </summary>
    /// <param name="role">The role to get the name for.</param>
    /// <returns>The human-readable role name.</returns>
    public static string GetBuiltInRoleName(DurableTaskSchedulerBuiltInRole role)
    {
        return role._value switch
        {
            "0ad04412-c4d5-4796-b79c-f76d14c8d402" => "Durable_Task_Data_Contributor",
            "d6a5505f-6ebb-45a4-896e-ac8274cfc0ac" => "Durable_Task_Data_Reader",
            "80d0d6b0-f522-40a4-8886-a5a11720c375" => "Durable_Task_Worker",
            _ => role._value
        };
    }

    /// <inheritdoc/>
    public bool Equals(DurableTaskSchedulerBuiltInRole other) => _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DurableTaskSchedulerBuiltInRole other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;

    /// <summary>
    /// Determines if two <see cref="DurableTaskSchedulerBuiltInRole"/> values are equal.
    /// </summary>
    public static bool operator ==(DurableTaskSchedulerBuiltInRole left, DurableTaskSchedulerBuiltInRole right) => left.Equals(right);

    /// <summary>
    /// Determines if two <see cref="DurableTaskSchedulerBuiltInRole"/> values are not equal.
    /// </summary>
    public static bool operator !=(DurableTaskSchedulerBuiltInRole left, DurableTaskSchedulerBuiltInRole right) => !left.Equals(right);
}
