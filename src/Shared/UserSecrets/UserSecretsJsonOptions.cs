// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aspire.Shared.UserSecrets;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for reading and writing user secrets JSON files.
/// Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so characters like &amp; and +
/// are preserved verbatim rather than being escaped as \u0026 and \u002B.
/// </summary>
internal static class UserSecretsJsonOptions
{
    internal static readonly JsonSerializerOptions s_instance = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
