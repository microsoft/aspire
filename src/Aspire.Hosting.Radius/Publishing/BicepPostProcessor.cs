// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Azure.Provisioning;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Utility methods for Bicep post-processing: identifier sanitization,
/// recipe reference validation, and <c>extension radius</c> injection.
/// </summary>
internal static partial class BicepPostProcessor
{
    /// <summary>
    /// Compiles <see cref="RadiusInfrastructureOptions"/> to Bicep using the
    /// Azure.Provisioning SDK pipeline (<c>Infrastructure.Build().Compile()</c>)
    /// and prepends the <c>extension radius</c> directive.
    /// </summary>
    internal static string CompileBicep(RadiusInfrastructureOptions options, string environmentName, ILogger logger)
    {
        // Validate recipe references before compiling
        ValidateRecipeReferences(options, logger);

        var infra = new RadiusResourceInfrastructure(environmentName);

        // Add all constructs in block order:
        // 1. Recipe packs (referenced by environments)
        // 2. UDT environments
        // 3. UDT applications
        // 4. Legacy environments (Applications.Core/environments, fallback types)
        // 5. Legacy applications (Applications.Core/applications)
        // 6. Resource type instances
        // 7. Containers
        foreach (var resource in options.RecipePacks)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.Environments)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.Applications)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.LegacyEnvironments)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.LegacyApplications)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.ResourceTypeInstances)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.Containers)
        {
            infra.Add(resource);
        }

        foreach (var resource in options.LegacyContainers)
        {
            infra.Add(resource);
        }

        var plan = infra.Build(new ProvisioningBuildOptions());
        var compiled = plan.Compile();

        // The SDK generates a single file named "{infraName}.bicep"
        var bicepContent = compiled.Values.First().ToString();

        // Prepend `extension radius` directive (not natively supported by the SDK)
        var withExtension = $"extension radius\n\n{bicepContent}";

        // Ensure every Radius.Core/recipePacks resource has a `properties` block.
        // Azure.Provisioning's BicepDictionary serializer omits empty dictionaries,
        // so a recipe pack with no entries renders as `name: 'default' }` only,
        // which fails Bicep BCP035 because the recipePacks UDT requires `properties`.
        return EnsureRecipePackProperties(withExtension);
    }

    /// <summary>
    /// Injects <c>properties: { recipes: {} }</c> into <c>Radius.Core/recipePacks</c>
    /// resource blocks that lack a <c>properties:</c> key. See <see cref="CompileBicep"/>
    /// for the underlying SDK behaviour this works around.
    /// </summary>
    internal static string EnsureRecipePackProperties(string bicep)
    {
        return RecipePackWithoutProperties().Replace(bicep, m =>
        {
            var indent = m.Groups["indent"].Value;
            var body = m.Value.TrimEnd();
            if (body.EndsWith('}'))
            {
                body = body[..^1].TrimEnd();
            }
            return $"{body}\n{indent}  properties: {{\n{indent}    recipes: {{}}\n{indent}  }}\n{indent}}}";
        });
    }

    [GeneratedRegex(
        @"(?<indent>[ \t]*)resource[ \t]+\w+[ \t]+'Radius\.Core/recipePacks@[^']+'[ \t]*=[ \t]*\{[^{}]*?\n[ \t]*\}",
        RegexOptions.Singleline)]
    private static partial Regex RecipePackWithoutProperties();

    /// <summary>
    /// Generates the companion bicepconfig.json content.
    /// </summary>
    internal static string RenderBicepConfig()
    {
        // The radius extension version is pinned (not `:latest`) so an upstream
        // tag move can't change the schema this AppHost emits against. See
        // RadiusBicepExtension for the version pin policy.
        // No aws extension is registered here — this integration does not emit
        // AWS resources, so listing it would pull a large extension package
        // for nothing and produce confusing "unknown resource" diagnostics if
        // future code accidentally referenced an AWS type.
        return $$"""
            {
                "experimentalFeaturesEnabled": {
                    "extensibility": true
                },
                "extensions": {
                    "radius": "{{RadiusBicepExtension.Reference}}"
                }
            }
            """;
    }

    /// <summary>
    /// Sanitizes a resource name into a valid Bicep identifier.
    /// </summary>
    internal static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "resource";
        }

        // Replace non-alphanumeric/underscore characters with underscores
        var sanitized = InvalidIdentifierChars().Replace(name, "_");

        // Names starting with a digit are prefixed with 'r'
        if (char.IsDigit(sanitized[0]))
        {
            sanitized = "r" + sanitized;
        }

        // "radius" collides with the `extension radius` directive
        if (string.Equals(sanitized, "radius", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = "radiusenv";
        }

        return sanitized;
    }

    /// <summary>
    /// Converts a .NET value to a type compatible with <c>BicepValue</c> assignment.
    /// </summary>
    internal static object ToBicepLiteral(object value)
    {
        return value switch
        {
            string or int or long or bool or double or float or decimal => value,
            _ => throw new NotSupportedException(
                $"Bicep parameter type '{value.GetType().Name}' is not supported. " +
                $"Supported types: string, int, long, double, float, decimal, bool.")
        };
    }

    private static void ValidateRecipeReferences(RadiusInfrastructureOptions options, ILogger logger)
    {
        // Collect all recipe type keys from both UDT recipe packs and legacy
        // environments (which carry recipes inline).
        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in options.RecipePacks)
        {
            foreach (var key in pack.Recipes.Keys)
            {
                registeredTypes.Add(key);
            }
        }

        foreach (var legacyEnv in options.LegacyEnvironments)
        {
            foreach (var key in legacyEnv.Recipes.Keys)
            {
                registeredTypes.Add(key);
            }
        }

        // Check resource type instances for recipes referencing unregistered types
        foreach (var instance in options.ResourceTypeInstances)
        {
            if (!instance.RecipeName.IsEmpty && !registeredTypes.Contains(instance.RadiusType))
            {
                logger.LogWarning(
                    "Resource '{ResourceName}' references a recipe but resource type '{ResourceType}' is not registered in any recipe pack or legacy environment.",
                    instance.BicepIdentifier,
                    instance.RadiusType);
            }
        }
    }

    [GeneratedRegex("[^a-zA-Z0-9_]")]
    private static partial Regex InvalidIdentifierChars();
}
