// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Resolves the assembly name used by SDK commands for project references.
/// </summary>
internal static class IntegrationAssemblyNameResolver
{
    public static string Resolve(FileInfo projectFile)
    {
        ArgumentNullException.ThrowIfNull(projectFile);

        var fallbackName = Path.GetFileNameWithoutExtension(projectFile.Name);
        if (!projectFile.Exists)
        {
            return fallbackName;
        }

        try
        {
            var document = XDocument.Load(projectFile.FullName);
            var assemblyName = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName" && !string.IsNullOrWhiteSpace(element.Value))?
                .Value
                .Trim();

            return string.IsNullOrWhiteSpace(assemblyName) ? fallbackName : assemblyName;
        }
        catch
        {
            return fallbackName;
        }
    }
}