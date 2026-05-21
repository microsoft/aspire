// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal static class AtsCompatibilityComparer
{
    public static IReadOnlyList<ApiCompatDiagnostic> Compare(
        AtsSurfaceSet baselineSet,
        AtsSurfaceSet currentSet,
        IReadOnlySet<string>? excludedPackages = null)
    {
        var diagnostics = new List<ApiCompatDiagnostic>();

        foreach (var (packageName, baseline) in baselineSet.Surfaces.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (excludedPackages?.Contains(packageName) == true)
            {
                continue;
            }

            if (!currentSet.Surfaces.TryGetValue(packageName, out var current))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "package-removed",
                    packageName,
                    "*",
                    $"Package '{packageName}' has an ATS baseline but no current ATS surface."));
                continue;
            }

            CompareSurface(baseline, current, diagnostics);
        }

        return diagnostics;
    }

    private static void CompareSurface(AtsSurface baseline, AtsSurface current, List<ApiCompatDiagnostic> diagnostics)
    {
        CompareRemoved(
            baseline.PackageName,
            baseline.HandleTypes.Keys,
            current.HandleTypes.Keys,
            "handle-removed",
            "Handle type",
            diagnostics);

        foreach (var (typeId, baselineHandle) in baseline.HandleTypes)
        {
            if (!current.HandleTypes.TryGetValue(typeId, out var currentHandle))
            {
                continue;
            }

            foreach (var flag in baselineHandle.Flags.Order(StringComparer.Ordinal))
            {
                if (!currentHandle.Flags.Contains(flag))
                {
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "handle-flag-removed",
                        baseline.PackageName,
                        $"{typeId}.{flag}",
                        $"Handle type '{typeId}' no longer has flag '{flag}'."));
                }
            }
        }

        CompareDtoTypes(baseline, current, diagnostics);
        CompareEnumTypes(baseline, current, diagnostics);
        CompareExportedValues(baseline, current, diagnostics);
        CompareCapabilities(baseline, current, diagnostics);
    }

    private static void CompareDtoTypes(AtsSurface baseline, AtsSurface current, List<ApiCompatDiagnostic> diagnostics)
    {
        CompareRemoved(
            baseline.PackageName,
            baseline.DtoTypes.Keys,
            current.DtoTypes.Keys,
            "dto-removed",
            "DTO type",
            diagnostics);

        foreach (var (typeId, baselineDto) in baseline.DtoTypes)
        {
            if (!current.DtoTypes.TryGetValue(typeId, out var currentDto))
            {
                continue;
            }

            foreach (var (propertyName, baselineProperty) in baselineDto.Properties)
            {
                var symbol = $"{typeId}.{propertyName}";
                if (!currentDto.Properties.TryGetValue(propertyName, out var currentProperty))
                {
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "dto-property-removed",
                        baseline.PackageName,
                        symbol,
                        $"DTO property '{symbol}' was removed."));
                    continue;
                }

                if (!string.Equals(baselineProperty.TypeId, currentProperty.TypeId, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "dto-property-type-changed",
                        baseline.PackageName,
                        symbol,
                        $"DTO property '{symbol}' type changed from '{baselineProperty.TypeId}' to '{currentProperty.TypeId}'."));
                }

                if (baselineProperty.IsOptional && !currentProperty.IsOptional)
                {
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "dto-property-required",
                        baseline.PackageName,
                        symbol,
                        $"DTO property '{symbol}' changed from optional to required."));
                }
            }

            foreach (var (propertyName, currentProperty) in currentDto.Properties)
            {
                if (!currentProperty.IsOptional && !baselineDto.Properties.ContainsKey(propertyName))
                {
                    var symbol = $"{typeId}.{propertyName}";
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "dto-property-added-required",
                        baseline.PackageName,
                        symbol,
                        $"DTO property '{symbol}' was added as required."));
                }
            }
        }
    }

    private static void CompareEnumTypes(AtsSurface baseline, AtsSurface current, List<ApiCompatDiagnostic> diagnostics)
    {
        CompareRemoved(
            baseline.PackageName,
            baseline.EnumTypes.Keys,
            current.EnumTypes.Keys,
            "enum-removed",
            "Enum type",
            diagnostics);

        foreach (var (typeId, baselineEnum) in baseline.EnumTypes)
        {
            if (!current.EnumTypes.TryGetValue(typeId, out var currentEnum))
            {
                continue;
            }

            var currentValues = currentEnum.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var value in baselineEnum.Values)
            {
                if (!currentValues.Contains(value))
                {
                    diagnostics.Add(new ApiCompatDiagnostic(
                        "enum-value-removed",
                        baseline.PackageName,
                        $"{typeId}.{value}",
                        $"Enum value '{typeId}.{value}' was removed."));
                }
            }
        }
    }

    private static void CompareExportedValues(AtsSurface baseline, AtsSurface current, List<ApiCompatDiagnostic> diagnostics)
    {
        CompareRemoved(
            baseline.PackageName,
            baseline.ExportedValues.Keys,
            current.ExportedValues.Keys,
            "exported-value-removed",
            "Exported value",
            diagnostics);

        foreach (var (path, baselineValue) in baseline.ExportedValues)
        {
            if (!current.ExportedValues.TryGetValue(path, out var currentValue))
            {
                continue;
            }

            if (!string.Equals(baselineValue.TypeId, currentValue.TypeId, StringComparison.Ordinal))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "exported-value-type-changed",
                    baseline.PackageName,
                    path,
                    $"Exported value '{path}' type changed from '{baselineValue.TypeId}' to '{currentValue.TypeId}'."));
            }

            if (!string.Equals(baselineValue.Value, currentValue.Value, StringComparison.Ordinal))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "exported-value-changed",
                    baseline.PackageName,
                    path,
                    $"Exported value '{path}' changed from '{baselineValue.Value}' to '{currentValue.Value}'."));
            }
        }
    }

    private static void CompareCapabilities(AtsSurface baseline, AtsSurface current, List<ApiCompatDiagnostic> diagnostics)
    {
        CompareRemoved(
            baseline.PackageName,
            baseline.Capabilities.Keys,
            current.Capabilities.Keys,
            "capability-removed",
            "Capability",
            diagnostics);

        foreach (var (capabilityId, baselineCapability) in baseline.Capabilities)
        {
            if (!current.Capabilities.TryGetValue(capabilityId, out var currentCapability))
            {
                continue;
            }

            if (!string.Equals(baselineCapability.ReturnTypeId, currentCapability.ReturnTypeId, StringComparison.Ordinal))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "capability-return-type-changed",
                    baseline.PackageName,
                    capabilityId,
                    $"Capability '{capabilityId}' return type changed from '{baselineCapability.ReturnTypeId}' to '{currentCapability.ReturnTypeId}'."));
            }

            CompareCapabilityParameters(baseline.PackageName, baselineCapability, currentCapability, diagnostics);
        }
    }

    private static void CompareCapabilityParameters(
        string packageName,
        AtsCapability baselineCapability,
        AtsCapability currentCapability,
        List<ApiCompatDiagnostic> diagnostics)
    {
        var currentByName = currentCapability.Parameters.ToDictionary(static p => p.Name, StringComparer.Ordinal);
        var baselineByName = baselineCapability.Parameters.ToDictionary(static p => p.Name, StringComparer.Ordinal);

        foreach (var baselineParameter in baselineCapability.Parameters)
        {
            var symbol = $"{baselineCapability.CapabilityId}({baselineParameter.Name})";
            if (!currentByName.TryGetValue(baselineParameter.Name, out var currentParameter))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "capability-parameter-removed",
                    packageName,
                    symbol,
                    $"Capability parameter '{symbol}' was removed."));
                continue;
            }

            if (!string.Equals(baselineParameter.TypeId, currentParameter.TypeId, StringComparison.Ordinal))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "capability-parameter-type-changed",
                    packageName,
                    symbol,
                    $"Capability parameter '{symbol}' type changed from '{baselineParameter.TypeId}' to '{currentParameter.TypeId}'."));
            }

            if (baselineParameter.IsOptional && !currentParameter.IsOptional)
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    "capability-parameter-required",
                    packageName,
                    symbol,
                    $"Capability parameter '{symbol}' changed from optional to required."));
            }
        }

        foreach (var currentParameter in currentCapability.Parameters)
        {
            if (!currentParameter.IsOptional && !baselineByName.ContainsKey(currentParameter.Name))
            {
                var symbol = $"{baselineCapability.CapabilityId}({currentParameter.Name})";
                diagnostics.Add(new ApiCompatDiagnostic(
                    "capability-parameter-added-required",
                    packageName,
                    symbol,
                    $"Capability parameter '{symbol}' was added as required."));
            }
        }

        var baselineSharedOrder = baselineCapability.Parameters
            .Select(static p => p.Name)
            .Where(currentByName.ContainsKey)
            .ToArray();
        var currentSharedOrder = currentCapability.Parameters
            .Select(static p => p.Name)
            .Where(baselineByName.ContainsKey)
            .ToArray();
        var currentParameterOrder = currentCapability.Parameters
            .Select(static p => p.Name)
            .ToArray();
        var hasInsertedParameterBeforeExistingParameter = currentCapability.Parameters
            .Select((parameter, index) => (parameter, index))
            .Any(parameterWithIndex =>
                !baselineByName.ContainsKey(parameterWithIndex.parameter.Name) &&
                currentCapability.Parameters
                    .Skip(parameterWithIndex.index + 1)
                    .Any(parameter => baselineByName.ContainsKey(parameter.Name)));

        if (!baselineSharedOrder.SequenceEqual(currentSharedOrder, StringComparer.Ordinal) || hasInsertedParameterBeforeExistingParameter)
        {
            diagnostics.Add(new ApiCompatDiagnostic(
                "capability-parameter-order-changed",
                packageName,
                baselineCapability.CapabilityId,
                $"Capability '{baselineCapability.CapabilityId}' parameter order changed from '{string.Join(", ", baselineSharedOrder)}' to '{string.Join(", ", currentParameterOrder)}'."));
        }
    }

    private static void CompareRemoved(
        string packageName,
        IEnumerable<string> baselineSymbols,
        IEnumerable<string> currentSymbols,
        string kind,
        string displayName,
        List<ApiCompatDiagnostic> diagnostics)
    {
        var currentSet = currentSymbols.ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in baselineSymbols.Order(StringComparer.Ordinal))
        {
            if (!currentSet.Contains(symbol))
            {
                diagnostics.Add(new ApiCompatDiagnostic(
                    kind,
                    packageName,
                    symbol,
                    $"{displayName} '{symbol}' was removed."));
            }
        }
    }
}
