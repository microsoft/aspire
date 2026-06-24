// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Built-in Azure Container Apps sandbox group roles that can be assigned to users, groups, service principals, and managed identities.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public readonly struct AzureSandboxGroupBuiltInRole : IEquatable<AzureSandboxGroupBuiltInRole>
{
    private const string SandboxGroupDataOwnerId = "c24cf47c-5077-412d-a19c-45202126392c";
    private readonly string? _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSandboxGroupBuiltInRole"/> struct.
    /// </summary>
    /// <param name="value">The Azure role definition ID.</param>
    public AzureSandboxGroupBuiltInRole(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>
    /// Gets the role that allows full data-plane access to Azure Container Apps sandbox groups.
    /// </summary>
    public static AzureSandboxGroupBuiltInRole SandboxGroupDataOwner { get; } = new(SandboxGroupDataOwnerId);

    /// <summary>
    /// Gets the display name for a built-in sandbox group role.
    /// </summary>
    /// <param name="role">The built-in role.</param>
    /// <returns>The display name for the built-in role.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="role"/> is not a known sandbox group built-in role.</exception>
    public static string GetBuiltInRoleName(AzureSandboxGroupBuiltInRole role) =>
        role == SandboxGroupDataOwner ? "SandboxGroup Data Owner" :
            throw new ArgumentException($"The role '{role}' is not a known Azure Container Apps sandbox group built-in role.", nameof(role));

    /// <inheritdoc/>
    public bool Equals(AzureSandboxGroupBuiltInRole other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AzureSandboxGroupBuiltInRole other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;

    /// <summary>
    /// Converts an Azure role definition ID into a sandbox group built-in role value.
    /// </summary>
    /// <param name="value">The Azure role definition ID.</param>
    public static implicit operator AzureSandboxGroupBuiltInRole(string value) => new(value);

    /// <summary>
    /// Determines whether two <see cref="AzureSandboxGroupBuiltInRole"/> values are equal.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when the values are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(AzureSandboxGroupBuiltInRole left, AzureSandboxGroupBuiltInRole right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="AzureSandboxGroupBuiltInRole"/> values are not equal.
    /// </summary>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns><see langword="true"/> when the values are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(AzureSandboxGroupBuiltInRole left, AzureSandboxGroupBuiltInRole right) => !left.Equals(right);
}
