// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.TypeScript;

/// <summary>
/// Helpers for reading static resource files embedded into the
/// <c>Aspire.Hosting.CodeGeneration.TypeScript</c> assembly.
/// </summary>
internal static class EmbeddedResources
{
    private const string ResourceNamespace = "Aspire.Hosting.CodeGeneration.TypeScript.Resources";

    /// <summary>
    /// Reads the contents of an embedded resource by file name (e.g. <c>"eslint.config.mjs"</c>).
    /// </summary>
    /// <param name="name">The resource file name as it lives under <c>Resources/</c>.</param>
    /// <returns>The UTF-8 text content of the resource.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the resource is not embedded.</exception>
    public static string Read(string name)
    {
        var assembly = typeof(EmbeddedResources).Assembly;
        var resourceName = $"{ResourceNamespace}.{name}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
