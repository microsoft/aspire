// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal sealed record AtsSurface(
    string PackageName,
    IReadOnlyDictionary<string, AtsHandleType> HandleTypes,
    IReadOnlyDictionary<string, AtsDtoType> DtoTypes,
    IReadOnlyDictionary<string, AtsEnumType> EnumTypes,
    IReadOnlyDictionary<string, AtsExportedValue> ExportedValues,
    IReadOnlyDictionary<string, AtsCapability> Capabilities);

internal sealed record AtsHandleType(string TypeId, IReadOnlySet<string> Flags);

internal sealed record AtsDtoType(string TypeId, IReadOnlyDictionary<string, AtsDtoProperty> Properties);

internal sealed record AtsDtoProperty(string Name, string TypeId, bool IsOptional);

internal sealed record AtsEnumType(string TypeId, IReadOnlyList<string> Values);

internal sealed record AtsExportedValue(string Path, string TypeId, string Value);

internal sealed record AtsCapability(string CapabilityId, IReadOnlyList<AtsParameter> Parameters, string ReturnTypeId);

internal sealed record AtsParameter(string Name, string TypeId, bool IsOptional);

internal sealed class AtsSurfaceSet
{
    private AtsSurfaceSet(IReadOnlyDictionary<string, AtsSurface> surfaces)
    {
        Surfaces = surfaces;
    }

    public IReadOnlyDictionary<string, AtsSurface> Surfaces { get; }

    public static AtsSurfaceSet Load(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Surface directory '{rootPath}' does not exist.");
        }

        var surfaces = new Dictionary<string, AtsSurface>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.ats.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var packageName = GetPackageName(file);
            if (surfaces.ContainsKey(packageName))
            {
                throw new InvalidOperationException($"Duplicate ATS surface for package '{packageName}' under '{rootPath}'.");
            }

            surfaces.Add(packageName, AtsSurfaceParser.Parse(packageName, File.ReadAllText(file)));
        }

        return new AtsSurfaceSet(surfaces);
    }

    private static string GetPackageName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        const string suffix = ".ats.txt";

        if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ATS surface file '{filePath}' does not end with '{suffix}'.");
        }

        return fileName[..^suffix.Length];
    }
}
