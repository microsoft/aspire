// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal static class AtsSurfaceParser
{
    public static AtsSurface Parse(string packageName, string content, IReadOnlyCollection<string>? knownPackageNames = null)
    {
        var packageNames = GetKnownPackageNames(packageName, knownPackageNames);
        var handleTypes = new Dictionary<string, AtsHandleType>(StringComparer.Ordinal);
        var dtoTypes = new Dictionary<string, AtsDtoTypeBuilder>(StringComparer.Ordinal);
        var enumTypes = new Dictionary<string, AtsEnumType>(StringComparer.Ordinal);
        var exportedValues = new Dictionary<string, AtsExportedValue>(StringComparer.Ordinal);
        var capabilities = new Dictionary<string, AtsCapability>(StringComparer.Ordinal);

        var section = AtsSection.None;
        AtsDtoTypeBuilder? currentDto = null;
        var skippingDto = false;

        foreach (var rawLine in content.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                section = trimmed switch
                {
                    "# Handle Types" => AtsSection.HandleTypes,
                    "# DTO Types" => AtsSection.DtoTypes,
                    "# Enum Types" => AtsSection.EnumTypes,
                    "# Exported Values" => AtsSection.ExportedValues,
                    "# Capabilities" => AtsSection.Capabilities,
                    _ => AtsSection.None
                };
                currentDto = null;
                skippingDto = false;
                continue;
            }

            switch (section)
            {
                case AtsSection.HandleTypes:
                    var handle = ParseHandleType(trimmed);
                    if (IsOwnedByPackage(handle.TypeId, packageName, packageNames))
                    {
                        handleTypes.Add(handle.TypeId, handle);
                    }
                    break;

                case AtsSection.DtoTypes:
                    if (rawLine.StartsWith("  ", StringComparison.Ordinal))
                    {
                        if (currentDto is null)
                        {
                            if (skippingDto)
                            {
                                continue;
                            }

                            throw new InvalidDataException($"DTO property '{rawLine}' appeared before a DTO type.");
                        }

                        var property = ParseDtoProperty(trimmed);
                        currentDto.Properties.Add(property.Name, property);
                    }
                    else
                    {
                        currentDto = new AtsDtoTypeBuilder(StripDescription(trimmed));
                        if (IsOwnedByPackage(currentDto.TypeId, packageName, packageNames))
                        {
                            dtoTypes.Add(currentDto.TypeId, currentDto);
                            skippingDto = false;
                        }
                        else
                        {
                            currentDto = null;
                            skippingDto = true;
                        }
                    }
                    break;

                case AtsSection.EnumTypes:
                    var enumType = ParseEnumType(trimmed);
                    if (IsOwnedByPackage(enumType.TypeId, packageName, packageNames))
                    {
                        enumTypes.Add(enumType.TypeId, enumType);
                    }
                    break;

                case AtsSection.ExportedValues:
                    var exportedValue = ParseExportedValue(trimmed);
                    exportedValues.Add(exportedValue.Path, exportedValue);
                    break;

                case AtsSection.Capabilities:
                    var capability = ParseCapability(trimmed);
                    if (IsOwnedByPackage(capability.CapabilityId, packageName, packageNames))
                    {
                        capabilities.Add(capability.CapabilityId, capability);
                    }
                    break;
            }
        }

        return new AtsSurface(
            packageName,
            handleTypes,
            dtoTypes.ToDictionary(
                static pair => pair.Key,
                static pair => new AtsDtoType(pair.Value.TypeId, pair.Value.Properties),
                StringComparer.Ordinal),
            enumTypes,
            exportedValues,
            capabilities);
    }

    private static AtsHandleType ParseHandleType(string line)
    {
        var bracketIndex = line.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex < 0)
        {
            return new AtsHandleType(line, new HashSet<string>(StringComparer.Ordinal));
        }

        var typeId = line[..bracketIndex];
        var flagsText = line[(bracketIndex + 2)..].TrimEnd(']');
        var flags = flagsText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        return new AtsHandleType(typeId, flags);
    }

    private static AtsDtoProperty ParseDtoProperty(string line)
    {
        var separatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new InvalidDataException($"Invalid DTO property line '{line}'.");
        }

        var nameText = line[..separatorIndex];
        var isOptional = nameText.EndsWith('?');
        var name = isOptional ? nameText[..^1] : nameText;
        var typeId = StripDescription(line[(separatorIndex + 2)..]);

        return new AtsDtoProperty(name, typeId, isOptional);
    }

    private static AtsEnumType ParseEnumType(string line)
    {
        var separatorIndex = line.IndexOf(" = ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new InvalidDataException($"Invalid enum line '{line}'.");
        }

        var typeId = line[..separatorIndex];
        var values = line[(separatorIndex + 3)..]
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return new AtsEnumType(typeId, values);
    }

    private static AtsExportedValue ParseExportedValue(string line)
    {
        var pathSeparatorIndex = line.IndexOf(": ", StringComparison.Ordinal);
        if (pathSeparatorIndex < 0)
        {
            throw new InvalidDataException($"Invalid exported value line '{line}'.");
        }

        var typeSeparatorIndex = line.IndexOf(" = ", pathSeparatorIndex + 2, StringComparison.Ordinal);
        if (typeSeparatorIndex < 0)
        {
            throw new InvalidDataException($"Invalid exported value line '{line}'.");
        }

        var path = line[..pathSeparatorIndex];
        var typeId = line[(pathSeparatorIndex + 2)..typeSeparatorIndex];
        var value = StripDescription(line[(typeSeparatorIndex + 3)..]);

        return new AtsExportedValue(path, typeId, value);
    }

    private static AtsCapability ParseCapability(string line)
    {
        var openParenIndex = line.IndexOf('(');
        var closeParenIndex = line.LastIndexOf(") -> ", StringComparison.Ordinal);
        if (openParenIndex < 0 || closeParenIndex < openParenIndex)
        {
            throw new InvalidDataException($"Invalid capability line '{line}'.");
        }

        var capabilityId = line[..openParenIndex];
        var parametersText = line[(openParenIndex + 1)..closeParenIndex];
        var returnTypeId = line[(closeParenIndex + 5)..];
        var parameters = string.IsNullOrWhiteSpace(parametersText)
            ? []
            : parametersText
                .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseParameter)
                .ToArray();

        return new AtsCapability(capabilityId, parameters, returnTypeId);
    }

    private static AtsParameter ParseParameter(string parameterText)
    {
        var separatorIndex = parameterText.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new InvalidDataException($"Invalid parameter '{parameterText}'.");
        }

        var nameText = parameterText[..separatorIndex];
        var isOptional = nameText.EndsWith('?');
        var name = isOptional ? nameText[..^1] : nameText;
        var typeId = parameterText[(separatorIndex + 2)..];

        return new AtsParameter(name, typeId, isOptional);
    }

    private static string StripDescription(string value)
    {
        var descriptionIndex = value.IndexOf(" # ", StringComparison.Ordinal);
        return descriptionIndex < 0 ? value : value[..descriptionIndex];
    }

    private static HashSet<string> GetKnownPackageNames(string packageName, IReadOnlyCollection<string>? knownPackageNames)
    {
        var packageNames = new HashSet<string>(StringComparer.Ordinal)
        {
            packageName
        };

        if (knownPackageNames is not null)
        {
            foreach (var knownPackageName in knownPackageNames)
            {
                if (!string.IsNullOrWhiteSpace(knownPackageName))
                {
                    packageNames.Add(knownPackageName);
                }
            }
        }

        return packageNames;
    }

    private static bool IsOwnedByPackage(string symbolId, string packageName, IReadOnlySet<string> knownPackageNames)
    {
        var normalizedSymbolId = symbolId.StartsWith("enum:", StringComparison.Ordinal)
            ? symbolId["enum:".Length..]
            : symbolId;

        if (normalizedSymbolId.StartsWith($"{packageName}/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalizedSymbolId.StartsWith($"{packageName}.", StringComparison.Ordinal))
        {
            return false;
        }

        return !TryGetMostSpecificDottedOwner(normalizedSymbolId, knownPackageNames, out var ownerPackageName) ||
            string.Equals(ownerPackageName, packageName, StringComparison.Ordinal);
    }

    private static bool TryGetMostSpecificDottedOwner(string symbolId, IReadOnlySet<string> knownPackageNames, out string packageName)
    {
        packageName = string.Empty;

        foreach (var knownPackageName in knownPackageNames)
        {
            if (symbolId.Length <= knownPackageName.Length ||
                symbolId[knownPackageName.Length] != '.' ||
                !symbolId.StartsWith(knownPackageName, StringComparison.Ordinal))
            {
                continue;
            }

            if (knownPackageName.Length > packageName.Length)
            {
                packageName = knownPackageName;
            }
        }

        return packageName.Length > 0;
    }

    private sealed class AtsDtoTypeBuilder(string typeId)
    {
        public string TypeId { get; } = typeId;
        public Dictionary<string, AtsDtoProperty> Properties { get; } = new(StringComparer.Ordinal);
    }

    private enum AtsSection
    {
        None,
        HandleTypes,
        DtoTypes,
        EnumTypes,
        ExportedValues,
        Capabilities
    }
}
