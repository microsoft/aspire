// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Describes an application role declared on a Microsoft Entra ID application registration created by Aspire.
/// </summary>
/// <remarks>
/// Application roles are separate from delegated scopes and from Azure RBAC roles. Declaring a role does not assign it
/// to any principal.
/// </remarks>
public sealed class EntraIdAppRole
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EntraIdAppRole"/> class.
    /// </summary>
    /// <param name="value">
    /// The value emitted in the <c>roles</c> claim, for example <c>Admin</c>. It must be unique for the application,
    /// at most 120 characters, must not start with a period, and may contain only ASCII letters, digits and the punctuation that Microsoft Graph allows:
    /// <c>! # $ % &amp; ' ( ) * + , - . / : ; &lt; = &gt; ? @ [ ] ^ _  { | } ~</c>.
    /// </param>
    /// <param name="displayName">The display name shown in the Microsoft Entra admin center.</param>
    /// <param name="description">The description shown in assignment and consent experiences.</param>
    /// <param name="allowedMemberTypes">
    /// The member types the role can be assigned to, passed through to Microsoft Graph (for example <c>User</c> or <c>Application</c>). Defaults to <c>User</c>.
    /// </param>
    /// <param name="id">
    /// An optional role ID. When omitted, a stable ID is derived from the application's resource name and <paramref name="value"/>.
    /// </param>
    /// <param name="isEnabled">
    /// Whether the role is enabled. To retire a role, deploy it with <see langword="false"/> and leave it declared. A disabled role can't be assigned to anyone new, but Microsoft Entra keeps issuing it in tokens for existing assignments, so disabling is not access revocation; remove existing assignments separately. Microsoft Graph rejects deleting an enabled role, so delete one only after a deployment has disabled it, and only while other roles remain declared.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when an argument is invalid.</exception>
    public EntraIdAppRole(string value, string displayName, string description, IEnumerable<string>? allowedMemberTypes = null, Guid? id = null, bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (!IsValidValue(value))
        {
            throw new ArgumentException($"'{value}' is not a valid role value. Role values must be at most 120 characters, must not start with a period, and may contain only ASCII letters, digits and the punctuation that Microsoft Graph allows.", nameof(value));
        }

        var memberTypes = allowedMemberTypes?.ToArray() ?? ["User"];
        if (memberTypes.Length == 0 || memberTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty member type is required.", nameof(allowedMemberTypes));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("The role ID must not be empty.", nameof(id));
        }

        Value = value;
        DisplayName = displayName;
        Description = description;
        AllowedMemberTypes = memberTypes;
        Id = id;
        IsEnabled = isEnabled;
    }

    /// <summary>
    /// Gets the value emitted in the <c>roles</c> claim of tokens issued with this role.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the display name shown in the Microsoft Entra admin center.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description shown in assignment and consent experiences.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the member types the role can be assigned to, passed through to Microsoft Graph (for example <c>User</c> or <c>Application</c>).
    /// </summary>
    public IReadOnlyList<string> AllowedMemberTypes { get; }

    /// <summary>
    /// Gets the explicitly configured role ID, or <see langword="null"/> when the ID is derived from the application name and role value.
    /// </summary>
    public Guid? Id { get; }

    /// <summary>
    /// Gets whether the role can be assigned to new principals. A disabled role is still issued in tokens for existing assignments, so this is not access revocation. Microsoft Graph requires a role to be disabled before it is removed.
    /// </summary>
    public bool IsEnabled { get; }

    // Microsoft Graph rejects any other character, including spaces and quotation marks, and a leading period:
    // https://learn.microsoft.com/graph/api/resources/approle
    private static bool IsValidValue(string value)
    {
        const string AllowedPunctuation = "!#$%&'()*+,-./:;<=>?@[]^_`{|}~";
        return value.Length <= 120 && value[0] != '.' && value.All(c => char.IsAsciiLetterOrDigit(c) || AllowedPunctuation.Contains(c));
    }

    // Role IDs must not change across deployments, or existing assignments are orphaned. Derive the ID from names
    // with a fixed algorithm so every deployment of the same model produces the same ID.
    internal Guid GetStableId(string applicationName)
    {
        if (Id is { } id)
        {
            return id;
        }

        // A 128-bit non-cryptographic hash is sufficient because the ID only needs to be stable and collision-resistant
        // among one application's roles, not unguessable. Stamp the RFC 9562 version 8 (custom) and variant bits so the
        // result is a well-formed GUID, using big-endian layout so the text form is identical on every platform.
        // https://www.rfc-editor.org/rfc/rfc9562#name-uuid-version-8
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes($"aspire-entra-app-role/{applicationName}/{Value}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}
