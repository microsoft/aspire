// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Specifies the type of a build secret.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum BuildImageSecretType
{
    /// <summary>
    /// The secret value is provided via an environment variable.
    /// </summary>
    Environment,

    /// <summary>
    /// The secret value is a file path.
    /// </summary>
    File
}

/// <summary>
/// Represents a resolved build secret with its value and type.
/// </summary>
/// <param name="Value">The resolved secret value. For <see cref="BuildImageSecretType.Environment"/> secrets, this is the secret content.
/// For <see cref="BuildImageSecretType.File"/> secrets, this is the file path.</param>
/// <param name="Type">The type of the build secret, indicating whether it is environment-based or file-based.</param>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public record BuildImageSecretValue(string? Value, BuildImageSecretType Type);
