// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Command for dumping ATS capabilities from Aspire integration libraries.
/// Supports multiple output formats for different use cases.
/// 
/// Usage:
///   aspire sdk dump                                                               # Core Aspire.Hosting only
///   aspire sdk dump Aspire.Hosting.Redis@13.2.0                                   # Single package
///   aspire sdk dump Aspire.Hosting.Redis@13.2.0 Aspire.Hosting.PostgreSQL@13.2.0  # Multiple packages
///   aspire sdk dump ./MyIntegration.csproj Aspire.Hosting.Redis@13.2.0            # Mix of project and packages
///   aspire sdk dump --format json                                                 # Machine-readable JSON
///   aspire sdk dump --format ci -o capabilities.txt                               # Stable text for git diffing
/// </summary>
internal sealed class SdkDumpCommand : BaseCommand
{
    private readonly IAppHostServerProjectFactory _appHostServerProjectFactory;
    private readonly ILogger<SdkDumpCommand> _logger;

    private static readonly Argument<string[]> s_integrationArgument = new("integrations")
    {
        Description = "Integrations to scan. Each can be a .csproj path or a NuGet package in PackageName@Version format. If not specified, dumps core Aspire.Hosting capabilities.",
        Arity = ArgumentArity.ZeroOrMore
    };
    private static readonly Option<FileInfo?> s_outputOption = new("--output", "-o")
    {
        Description = "Output file. If not specified, outputs to stdout."
    };
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = "Output format: Pretty (default), Json (machine-readable), or Ci (stable text for diffing)."
    };

    public SdkDumpCommand(
        IAppHostServerProjectFactory appHostServerProjectFactory,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ILogger<SdkDumpCommand> logger,
        AspireCliTelemetry telemetry)
        : base("dump", "Dump ATS capabilities from Aspire integration libraries.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _logger = logger;

        Arguments.Add(s_integrationArgument);
        Options.Add(s_outputOption);
        Options.Add(s_formatOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var integrationArgs = parseResult.GetValue(s_integrationArgument) ?? [];
        var outputFile = parseResult.GetValue(s_outputOption);
        var format = parseResult.GetValue(s_formatOption);

        // Parse each integration argument: either a .csproj path or PackageName@Version
        var integrations = new List<IntegrationReference>();

        foreach (var arg in integrationArgs)
        {
            if (arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var projectFile = new FileInfo(arg);
                if (!projectFile.Exists)
                {
                    InteractionService.DisplayError($"Integration project not found: {projectFile.FullName}");
                    return ExitCodeConstants.FailedToFindProject;
                }

                integrations.Add(IntegrationReference.FromProject(
                    IntegrationAssemblyNameResolver.Resolve(projectFile),
                    projectFile.FullName));
            }
            else if (arg.Contains('@'))
            {
                var atIndex = arg.LastIndexOf('@');
                var packageName = arg[..atIndex];
                var packageVersion = arg[(atIndex + 1)..];

                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(packageVersion) || packageName.Contains('@'))
                {
                    InteractionService.DisplayError($"Invalid package format '{arg}'. Expected PackageName@Version (e.g. Aspire.Hosting.Redis@9.2.0).");
                    return ExitCodeConstants.InvalidCommand;
                }

                if (!SemVersion.TryParse(packageVersion, SemVersionStyles.Any, out _))
                {
                    InteractionService.DisplayError($"Invalid version '{packageVersion}' in '{arg}'. Expected a valid NuGet version (e.g. 9.2.0).");
                    return ExitCodeConstants.InvalidCommand;
                }

                _logger.LogDebug("Parsed package reference {PackageName} version {Version}", packageName, packageVersion);
                integrations.Add(IntegrationReference.FromPackage(packageName, packageVersion));
            }
            else
            {
                InteractionService.DisplayError($"Invalid integration argument '{arg}'. Expected a .csproj path or PackageName@Version format.");
                return ExitCodeConstants.InvalidCommand;
            }
        }

        // For file output, skip the interactive spinner
        if (outputFile is not null)
        {
            return await DumpCapabilitiesAsync(integrations, outputFile, format, cancellationToken);
        }

        return await InteractionService.ShowStatusAsync(
            "Scanning capabilities...",
            async () => await DumpCapabilitiesAsync(integrations, outputFile, format, cancellationToken),
            emoji: KnownEmojis.MagnifyingGlassTiltedLeft);
    }

    private async Task<int> DumpCapabilitiesAsync(
        List<IntegrationReference> integrations,
        FileInfo? outputFile,
        OutputFormat format,
        CancellationToken cancellationToken)
    {
        // Use a temporary directory for the AppHost server
        var tempDir = Path.Combine(Path.GetTempPath(), "aspire-sdk-dump", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(tempDir, cancellationToken);

            _logger.LogDebug("Building AppHost server for capability scanning with {Count} integrations", integrations.Count);

            var prepareResult = await appHostServerProject.PrepareAsync(
                VersionHelper.GetDefaultTemplateVersion(),
                integrations,
                cancellationToken);

            if (!prepareResult.Success)
            {
                InteractionService.DisplayError("Failed to build capability scanner.");
                if (prepareResult.Output is not null)
                {
                    foreach (var (_, line) in prepareResult.Output.GetLines())
                    {
                        InteractionService.DisplayMessage(KnownEmojis.Wrench, line);
                    }
                }
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            await using var serverSession = AppHostServerSession.Start(
                appHostServerProject,
                environmentVariables: null,
                debug: false,
                _logger);

            // Connect and get capabilities
            var rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

            var exportAssemblyNames = integrations.Count > 0
                ? integrations.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : null;

            _logger.LogDebug("Fetching capabilities via RPC");
            var capabilities = exportAssemblyNames is not null
                ? await rpcClient.GetCapabilitiesForAssembliesAsync(exportAssemblyNames, cancellationToken)
                : await rpcClient.GetCapabilitiesAsync(cancellationToken);

            // Output Info-level diagnostics to stderr via logger (shown with -d flag)
            var infoDiagnostics = capabilities.Diagnostics.Where(d => d.Severity == "Info").ToList();
            foreach (var diag in infoDiagnostics)
            {
                var location = string.IsNullOrEmpty(diag.Location) ? "" : $" [{diag.Location}]";
                _logger.LogDebug("{Message}{Location}", diag.Message, location);
            }

            // Remove Info diagnostics from output (they go to stderr only)
            capabilities.Diagnostics.RemoveAll(d => d.Severity == "Info");

            // Stamp package versions for integrations that have them
            var packageVersions = integrations
                .Where(i => i.IsPackageReference)
                .Select(i => new PackageInfo { Name = i.Name, Version = i.Version! })
                .ToList();
            if (packageVersions.Count > 0)
            {
                capabilities.Packages = packageVersions;
            }

            // Format the output
            var output = format switch
            {
                OutputFormat.Json => FormatJson(capabilities),
                OutputFormat.Ci => FormatCi(capabilities),
                _ => FormatPretty(capabilities)
            };

            // Write output
            if (outputFile is not null)
            {
                var outputDir = outputFile.Directory;
                if (outputDir is not null && !outputDir.Exists)
                {
                    outputDir.Create();
                }
                await File.WriteAllTextAsync(outputFile.FullName, output, cancellationToken);
                InteractionService.DisplaySuccess($"Capabilities written to {outputFile.FullName}");
            }
            else
            {
                // Output to stdout
                InteractionService.DisplayRawText(output, consoleOverride: ConsoleOutput.Standard);
            }

            // Return error code if there are errors in diagnostics
            var hasErrors = capabilities.Diagnostics.Exists(d => d.Severity == "Error");
            return hasErrors ? ExitCodeConstants.InvalidCommand : ExitCodeConstants.Success;
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }
        }
    }

    #region Output Formatters

    private static string FormatJson(CapabilitiesInfo capabilities)
    {
        return JsonSerializer.Serialize(capabilities, CapabilitiesJsonContext.Default.CapabilitiesInfo);
    }

    private static string FormatCi(CapabilitiesInfo capabilities)
    {
        var sb = new StringBuilder();

        // Header (no timestamp for stable diffs)
        sb.AppendLine("# Aspire Type System Capabilities");
        sb.AppendLine("# Generated by: aspire sdk dump --format ci");
        sb.AppendLine();

        // Diagnostics
        if (capabilities.Diagnostics.Count > 0)
        {
            sb.AppendLine("# Diagnostics");
            foreach (var d in capabilities.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Location))
            {
                var loc = string.IsNullOrEmpty(d.Location) ? "" : string.Format(CultureInfo.InvariantCulture, " [{0}]", d.Location);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1}{2}", d.Severity.ToLowerInvariant(), d.Message, loc));
            }
            sb.AppendLine();
        }

        // Handle Types
        sb.AppendLine("# Handle Types");
        foreach (var t in capabilities.HandleTypes.OrderBy(t => t.AtsTypeId))
        {
            var flags = new List<string>();
            if (t.IsInterface)
            {
                flags.Add("interface");
            }
            if (t.ExposeProperties)
            {
                flags.Add("ExposeProperties");
            }
            if (t.ExposeMethods)
            {
                flags.Add("ExposeMethods");
            }
            var flagStr = flags.Count > 0 ? string.Format(CultureInfo.InvariantCulture, " [{0}]", string.Join(", ", flags)) : "";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}{1}", t.AtsTypeId, flagStr));
        }
        sb.AppendLine();

        // DTO Types
        if (capabilities.DtoTypes.Count > 0)
        {
            sb.AppendLine("# DTO Types");
            foreach (var t in capabilities.DtoTypes.OrderBy(t => t.TypeId))
            {
                if (!string.IsNullOrEmpty(t.Description))
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} # {1}", t.TypeId, t.Description));
                }
                else
                {
                    sb.AppendLine(t.TypeId);
                }
                foreach (var p in t.Properties.OrderBy(p => p.Name))
                {
                    var optional = p.IsOptional ? "?" : "";
                    var desc = !string.IsNullOrEmpty(p.Description) ? string.Format(CultureInfo.InvariantCulture, " # {0}", p.Description) : "";
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0}{1}: {2}{3}", p.Name, optional, p.Type?.TypeId ?? "unknown", desc));
                }
            }
            sb.AppendLine();
        }

        // Enum Types
        if (capabilities.EnumTypes.Count > 0)
        {
            sb.AppendLine("# Enum Types");
            foreach (var t in capabilities.EnumTypes.OrderBy(t => t.TypeId))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} = {1}", t.TypeId, string.Join(" | ", t.Values)));
            }
            sb.AppendLine();
        }

        // Capabilities
        sb.AppendLine("# Capabilities");
        foreach (var c in capabilities.Capabilities.OrderBy(c => c.CapabilityId))
        {
            var paramStr = string.Join(", ", c.Parameters.Select(p =>
            {
                var optional = p.IsOptional ? "?" : "";
                return string.Format(CultureInfo.InvariantCulture, "{0}{1}: {2}", p.Name, optional, p.Type?.TypeId ?? "unknown");
            }));
            var returnStr = c.ReturnType?.TypeId ?? "void";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}({1}) -> {2}", c.CapabilityId, paramStr, returnStr));
        }

        return sb.ToString();
    }

    private static string FormatPretty(CapabilitiesInfo capabilities)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("================================================================================");
        sb.AppendLine("                    Aspire Type System Capabilities                             ");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // Summary
        var errorCount = capabilities.Diagnostics.Count(d => d.Severity == "Error");
        var warningCount = capabilities.Diagnostics.Count(d => d.Severity == "Warning");
        sb.AppendLine("Summary");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   Handle Types:  {0}", capabilities.HandleTypes.Count));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   DTO Types:     {0}", capabilities.DtoTypes.Count));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   Enum Types:    {0}", capabilities.EnumTypes.Count));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   Capabilities:  {0}", capabilities.Capabilities.Count));
        if (errorCount > 0 || warningCount > 0)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   Diagnostics:   {0} errors, {1} warnings", errorCount, warningCount));
        }
        sb.AppendLine();

        // Diagnostics
        if (capabilities.Diagnostics.Count > 0)
        {
            sb.AppendLine("Diagnostics");
            sb.AppendLine("--------------------------------------------------------------------------------");
            foreach (var d in capabilities.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Location))
            {
                var icon = d.Severity == "Error" ? "[ERROR]" : "[WARN]";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   {0} {1}", icon, d.Message));
                if (!string.IsNullOrEmpty(d.Location))
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      -> {0}", d.Location));
                }
            }
            sb.AppendLine();
        }

        // Handle Types
        sb.AppendLine("Handle Types (passed by reference)");
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var t in capabilities.HandleTypes.OrderBy(t => t.AtsTypeId))
        {
            var flags = new List<string>();
            if (t.IsInterface)
            {
                flags.Add("interface");
            }
            if (t.ExposeProperties)
            {
                flags.Add("properties");
            }
            if (t.ExposeMethods)
            {
                flags.Add("methods");
            }
            var flagStr = flags.Count > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0})", string.Join(", ", flags)) : "";

            // Extract short name from AtsTypeId
            var shortName = t.AtsTypeId.Contains('/')
                ? t.AtsTypeId.Split('/')[1]
                : t.AtsTypeId;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   {0}{1}", shortName, flagStr));
        }
        sb.AppendLine();

        // DTO Types
        if (capabilities.DtoTypes.Count > 0)
        {
            sb.AppendLine("DTO Types (serialized as JSON)");
            sb.AppendLine("--------------------------------------------------------------------------------");
            foreach (var t in capabilities.DtoTypes.OrderBy(t => t.Name))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   {0}", t.Name));
                if (!string.IsNullOrEmpty(t.Description))
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      {0}", t.Description));
                }
                foreach (var p in t.Properties.OrderBy(p => p.Name))
                {
                    var optional = p.IsOptional ? "?" : "";
                    var typeId = p.Type?.TypeId ?? "unknown";
                    // Simplify type display
                    var simpleType = SimplifyTypeName(typeId);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      - {0}{1}: {2}", p.Name, optional, simpleType));
                    if (!string.IsNullOrEmpty(p.Description))
                    {
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "         {0}", p.Description));
                    }
                }
            }
            sb.AppendLine();
        }

        // Enum Types
        if (capabilities.EnumTypes.Count > 0)
        {
            sb.AppendLine("Enum Types");
            sb.AppendLine("--------------------------------------------------------------------------------");
            foreach (var t in capabilities.EnumTypes.OrderBy(t => t.Name))
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   {0}", t.Name));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      {0}", string.Join(" | ", t.Values)));
            }
            sb.AppendLine();
        }

        // Capabilities (grouped by category if available)
        sb.AppendLine("Capabilities");
        sb.AppendLine("--------------------------------------------------------------------------------");

        var capsByTarget = capabilities.Capabilities
            .GroupBy(c => c.OwningTypeName ?? "Extension Methods")
            .OrderBy(g => g.Key is null or "Extension Methods") // Sort nulls/extension methods last
            .ThenBy(g => g.Key);

        foreach (var group in capsByTarget)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "   [{0}]", group.Key));
            foreach (var c in group.OrderBy(c => c.MethodName))
            {
                var paramStr = string.Join(", ", c.Parameters.Select(p =>
                {
                    var optional = p.IsOptional ? "?" : "";
                    var simpleType = SimplifyTypeName(p.Type?.TypeId ?? "unknown");
                    return string.Format(CultureInfo.InvariantCulture, "{0}{1}: {2}", p.Name, optional, simpleType);
                }));
                var returnType = SimplifyTypeName(c.ReturnType?.TypeId ?? "void");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      {0}({1}) -> {2}", c.MethodName, paramStr, returnType));
                if (!string.IsNullOrEmpty(c.Description))
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "         {0}", c.Description));
                }
            }
        }

        return sb.ToString();
    }

    private static string SimplifyTypeName(string typeId)
    {
        // Remove assembly prefix
        if (typeId.Contains('/'))
        {
            typeId = typeId.Split('/')[1];
        }
        // Remove namespace
        var lastDot = typeId.LastIndexOf('.');
        if (lastDot > 0)
        {
            typeId = typeId[(lastDot + 1)..];
        }
        return typeId;
    }

    #endregion

    private enum OutputFormat
    {
        Pretty,
        Json,
        Ci
    }
}

#region Response DTOs (matching server response)

internal sealed class CapabilitiesInfo
{
    public List<PackageInfo> Packages { get; set; } = [];
    public List<CapabilityInfo> Capabilities { get; set; } = [];
    public List<HandleTypeInfo> HandleTypes { get; set; } = [];
    public List<DtoTypeInfo> DtoTypes { get; set; } = [];
    public List<EnumTypeInfo> EnumTypes { get; set; } = [];
    public List<DiagnosticInfo> Diagnostics { get; set; } = [];
}

internal sealed class PackageInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

internal sealed class CapabilityInfo
{
    public string CapabilityId { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string? OwningTypeName { get; set; }
    public string QualifiedMethodName { get; set; } = "";
    public string? Description { get; set; }
    public string CapabilityKind { get; set; } = "";
    public string? TargetTypeId { get; set; }
    public string? TargetParameterName { get; set; }
    public bool ReturnsBuilder { get; set; }
    public List<ParameterInfo> Parameters { get; set; } = [];
    public TypeRefInfo? ReturnType { get; set; }
    public TypeRefInfo? TargetType { get; set; }
    public List<TypeRefInfo> ExpandedTargetTypes { get; set; } = [];
}

internal sealed class ParameterInfo
{
    public string Name { get; set; } = "";
    public TypeRefInfo? Type { get; set; }
    public bool IsOptional { get; set; }
    public bool IsNullable { get; set; }
    public bool IsCallback { get; set; }
    public List<CallbackParameterInfo>? CallbackParameters { get; set; }
    public TypeRefInfo? CallbackReturnType { get; set; }
    public string? DefaultValue { get; set; }
}

internal sealed class CallbackParameterInfo
{
    public string Name { get; set; } = "";
    public TypeRefInfo? Type { get; set; }
}

internal sealed class TypeRefInfo
{
    public string TypeId { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsInterface { get; set; }
    public bool IsReadOnly { get; set; }
    public TypeRefInfo? ElementType { get; set; }
    public TypeRefInfo? KeyType { get; set; }
    public TypeRefInfo? ValueType { get; set; }
    public List<TypeRefInfo>? UnionTypes { get; set; }
}

internal sealed class HandleTypeInfo
{
    public string AtsTypeId { get; set; } = "";
    public bool IsInterface { get; set; }
    public bool ExposeProperties { get; set; }
    public bool ExposeMethods { get; set; }
    public List<TypeRefInfo> ImplementedInterfaces { get; set; } = [];
    public List<TypeRefInfo> BaseTypeHierarchy { get; set; } = [];
}

internal sealed class DtoTypeInfo
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<DtoPropertyInfo> Properties { get; set; } = [];
}

internal sealed class DtoPropertyInfo
{
    public string Name { get; set; } = "";
    public TypeRefInfo? Type { get; set; }
    public bool IsOptional { get; set; }
    public string? Description { get; set; }
}

internal sealed class EnumTypeInfo
{
    public string TypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Values { get; set; } = [];
}

internal sealed class DiagnosticInfo
{
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Location { get; set; }
}

#endregion

#region JSON Source Generation Context

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CapabilitiesInfo))]
[JsonSerializable(typeof(PackageInfo))]
[JsonSerializable(typeof(CapabilityInfo))]
[JsonSerializable(typeof(ParameterInfo))]
[JsonSerializable(typeof(CallbackParameterInfo))]
[JsonSerializable(typeof(TypeRefInfo))]
[JsonSerializable(typeof(HandleTypeInfo))]
[JsonSerializable(typeof(DtoTypeInfo))]
[JsonSerializable(typeof(DtoPropertyInfo))]
[JsonSerializable(typeof(EnumTypeInfo))]
[JsonSerializable(typeof(DiagnosticInfo))]
internal partial class CapabilitiesJsonContext : JsonSerializerContext
{
}

#endregion
