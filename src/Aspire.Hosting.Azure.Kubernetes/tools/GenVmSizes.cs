// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#:property PublishAot=false
#:property NoWarn=CS1591

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// =============================================================================
// GenVmSizes — AKS Node Pool VM Size Code Generator
// =============================================================================
//
// PURPOSE
// -------
// This tool generates AksNodeVmSizes.Generated.cs, which contains string
// constants for Azure VM sizes that can be used with AKS node pools. These
// constants provide IntelliSense discoverability and compile-time safety when
// configuring AKS node pool VM sizes in Aspire hosting code.
//
// HOW IT WORKS
// ------------
// 1. Queries the Microsoft.Compute/skus REST API without a location filter.
//    This single paginated call returns every VM SKU across every Azure region
//    in the subscription, giving us a comprehensive union of all available sizes.
//
// 2. Deduplicates by VM name (many entries exist — one per region per SKU) and
//    keeps the first entry alphabetically by location for stable capability
//    metadata (vCPUs, RAM, GPU count, etc.) across runs.
//
// 3. Applies AKS compatibility filters based on the documented restrictions at:
//    https://learn.microsoft.com/azure/aks/quotas-skus-regions#restricted-vm-sizes
//
//    - Excludes VM sizes with fewer than 2 vCPUs
//    - Excludes VM sizes with fewer than 2 GB RAM
//    - Excludes Basic tier VMs
//
// 4. Groups the filtered VM sizes by their Azure SKU family name and generates
//    a C# file with nested static classes containing string constants.
//
// LIMITATIONS AND CAVEATS
// -----------------------
// There is NO dedicated Azure API that returns "only VM sizes valid for AKS
// node pools." The closest option is `az aks nodepool list-available-sizes`,
// but that requires an existing AKS cluster and only returns sizes for the
// cluster's region — it cannot be used standalone or globally.
//
// As a result, the filtering applied here is HEURISTIC-BASED. It removes
// sizes that are documented as incompatible with AKS, but it does not
// guarantee that every remaining size will be accepted by AKS when creating
// a node pool. AKS may silently reject certain sizes for reasons not exposed
// in the Compute SKUs API (e.g., sizes blocked by AKS for compatibility
// testing, sizes in preview requiring feature flags, or region-specific
// restrictions).
//
// In practice, the generated list is a superset of what AKS actually accepts.
// This is acceptable for providing IntelliSense suggestions — users will get
// a validation error from AKS at deployment time if they pick an unsupported
// size, but they won't miss valid sizes due to overly aggressive filtering.
//
// If Azure ever exposes a standalone API for AKS-compatible VM sizes, this
// tool should be updated to use it instead of the heuristic approach.
//
// REQUIREMENTS
// ------------
// - Azure CLI (`az`) must be installed and in PATH
// - Must be logged in (`az login`) with access to a subscription
// - Must be targeting the public Azure cloud (AzureCloud)
// - Set AZURE_SUBSCRIPTION_ID env var, or the default subscription is used
//
// USAGE
// -----
// From the repository root:
//   dotnet run --project src/Aspire.Hosting.Azure.Kubernetes/tools GenVmSizes.cs
//
// =============================================================================

// Fetch VM sizes from Azure REST API using the 'az' CLI.
// Uses the unfiltered Compute SKUs endpoint which returns all SKUs across
// all regions in a single paginated response, ensuring comprehensive coverage.
var subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
if (string.IsNullOrWhiteSpace(subscriptionId))
{
    // Try to get default subscription from az CLI
    subscriptionId = await RunAzCommand("account show --query id -o tsv").ConfigureAwait(false);
    subscriptionId = subscriptionId?.Trim();
}

if (string.IsNullOrWhiteSpace(subscriptionId))
{
    Console.Error.WriteLine("Error: No Azure subscription found. Set AZURE_SUBSCRIPTION_ID or run 'az login'.");
    return 1;
}

// Verify we're targeting the public Azure cloud
var cloudName = await RunAzCommand("cloud show --query name -o tsv").ConfigureAwait(false);
cloudName = cloudName?.Trim();
if (string.IsNullOrWhiteSpace(cloudName))
{
    Console.Error.WriteLine("Error: Failed to determine the active Azure cloud. Ensure the Azure CLI is installed and that you are logged in with 'az login'.");
    return 1;
}
if (!string.Equals(cloudName, "AzureCloud", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Error: This tool is intended for the public Azure cloud (AzureCloud), but the active cloud is '{cloudName}'.");
    Console.Error.WriteLine("Switch to AzureCloud with: az cloud set --name AzureCloud");
    return 1;
}

Console.WriteLine($"Using subscription: {subscriptionId}");

// Query all VM SKUs across all regions in a single paginated call.
// This returns the full union of VM sizes available anywhere in the subscription,
// eliminating the need to enumerate individual regions.
var allSkus = new List<ResourceSku>();
string? nextUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus?api-version=2021-07-01";
var pageCount = 0;

while (nextUrl is not null)
{
    pageCount++;
    Console.WriteLine($"Fetching VM SKUs (page {pageCount})...");

    var json = await RunAzCommand($"rest --method get --url \"{nextUrl}\"").ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(json))
    {
        Console.Error.WriteLine($"Error: Failed to fetch VM SKUs (page {pageCount}).");
        return 1;
    }

    var skuResponse = JsonSerializer.Deserialize<SkuResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (skuResponse?.Value is not null)
    {
        allSkus.AddRange(skuResponse.Value);
        Console.WriteLine($"  Received {skuResponse.Value.Count} SKUs (total so far: {allSkus.Count})");
    }

    nextUrl = skuResponse?.NextLink;
}

if (allSkus.Count == 0)
{
    Console.Error.WriteLine("Error: No VM SKUs returned from Azure.");
    return 1;
}

// Filter to virtualMachines, apply AKS node pool compatibility filters, and
// deduplicate by name deterministically.
//
// AKS restrictions (https://learn.microsoft.com/azure/aks/quotas-skus-regions#restricted-vm-sizes):
// - VM sizes with fewer than 2 vCPUs are restricted
// - VM sizes with fewer than 2 GB RAM are restricted for user node pools
// - Basic tier VMs are not supported
//
// Note: Av1 series VMs are "not recommended" by AKS docs but are not explicitly
// restricted, so they are included in the generated output.
//
// The unfiltered API returns one entry per (SKU, location) pair, so there are
// many duplicates by name. We sort by location before grouping to ensure stable
// capability data regardless of API response ordering.
var vmSkus = allSkus
    .Where(s => s.ResourceType == "virtualMachines" && !string.IsNullOrEmpty(s.Name))
    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
    .ThenBy(s => s.Locations?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
    .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
    .Select(g => g.First())
    .Select(s => new VmSizeInfo
    {
        Name = s.Name!,
        Family = s.Family ?? "Other",
        Tier = s.Tier ?? "",
        VCpus = s.GetCapabilityValue("vCPUs"),
        MemoryGB = s.GetCapabilityValue("MemoryGB"),
        MaxDataDiskCount = s.GetCapabilityValue("MaxDataDiskCount"),
        PremiumIO = s.GetCapabilityBool("PremiumIO"),
        AcceleratedNetworking = s.GetCapabilityBool("AcceleratedNetworkingEnabled"),
        GpuCount = s.GetCapabilityValue("GPUs"),
    })
    .Where(IsAksCompatible)
    .OrderBy(s => s.Family, StringComparer.OrdinalIgnoreCase)
    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"Found {vmSkus.Count} AKS-compatible VM sizes");

var code = VmSizeClassGenerator.GenerateCode("Aspire.Hosting.Azure.Kubernetes", vmSkus);
File.WriteAllText(Path.Combine("..", "AksNodeVmSizes.Generated.cs"), code);
Console.WriteLine($"Generated AksNodeVmSizes.Generated.cs with {vmSkus.Count} VM sizes");

return 0;

static bool IsAksCompatible(VmSizeInfo vm)
{
    // AKS requires at least 2 vCPUs
    if (double.TryParse(vm.VCpus, CultureInfo.InvariantCulture, out var vcpus) && vcpus < 2)
    {
        return false;
    }

    // AKS requires at least 2 GB RAM for user node pools
    if (double.TryParse(vm.MemoryGB, CultureInfo.InvariantCulture, out var memGb) && memGb < 2)
    {
        return false;
    }

    // Basic tier VMs are not supported
    if (vm.Tier.Equals("Basic", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    // Basic_* SKUs (naming convention) are not supported
    if (vm.Name.StartsWith("Basic_", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return true;
}

static async Task<string?> RunAzCommand(string arguments)
{
    // Resolve 'az' CLI path. On Windows, az is a .cmd batch file so we need
    // to resolve it explicitly since Process.Start may not find .cmd files
    // in PATH when UseShellExecute=false.
    var azPath = FindAzCli();
    if (azPath is null)
    {
        Console.Error.WriteLine("Error: 'az' CLI not found. Ensure Azure CLI is installed and in PATH.");
        return null;
    }

    var psi = new ProcessStartInfo
    {
        FileName = azPath,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var process = Process.Start(psi);
    if (process is null)
    {
        return null;
    }

    // Read stdout and stderr concurrently to avoid deadlock when
    // the process fills the stderr pipe buffer.
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync().ConfigureAwait(false);

    var output = await stdoutTask.ConfigureAwait(false);
    var stderr = await stderrTask.ConfigureAwait(false);

    if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
    {
        Console.Error.WriteLine($"az {arguments}: {stderr.Trim()}");
    }

    return process.ExitCode == 0 ? output : null;
}

static string? FindAzCli()
{
    const string commandName = "az";

    var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    string[] candidates;
    if (OperatingSystem.IsWindows())
    {
        // Use PATHEXT to discover valid executable extensions (e.g., .cmd, .bat, .exe)
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        candidates = [.. pathExt.Select(ext => commandName + ext)];
    }
    else
    {
        candidates = [commandName];
    }

    foreach (var dir in pathDirs)
    {
        foreach (var candidate in candidates)
        {
            var fullPath = Path.Combine(dir, candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
    }

    return null;
}

public sealed class SkuResponse
{
    [JsonPropertyName("value")]
    public List<ResourceSku>? Value { get; set; }

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

public sealed class ResourceSku
{
    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("locations")]
    public List<string>? Locations { get; set; }

    [JsonPropertyName("capabilities")]
    public List<SkuCapability>? Capabilities { get; set; }

    public string? GetCapabilityValue(string name)
    {
        return Capabilities?.FirstOrDefault(c => c.Name == name)?.Value;
    }

    public bool GetCapabilityBool(string name)
    {
        var value = GetCapabilityValue(name);
        return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SkuCapability
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class VmSizeInfo
{
    public string Name { get; set; } = "";
    public string Family { get; set; } = "";
    public string Tier { get; set; } = "";
    public string? VCpus { get; set; }
    public string? MemoryGB { get; set; }
    public string? MaxDataDiskCount { get; set; }
    public bool PremiumIO { get; set; }
    public bool AcceleratedNetworking { get; set; }
    public string? GpuCount { get; set; }
}

internal static partial class VmSizeClassGenerator
{
    public static string GenerateCode(string ns, List<VmSizeInfo> sizes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
        sb.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
        sb.AppendLine();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// This file is generated by the GenVmSizes tool. Do not edit manually.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Provides well-known Azure VM size constants for use with AKS node pools.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("/// <remarks>");
        sb.AppendLine("/// This class is auto-generated from Azure Resource SKUs across all public Azure regions,");
        sb.AppendLine("/// filtered to VM sizes compatible with AKS node pools (minimum 2 vCPUs, 2 GB RAM,");
        sb.AppendLine("/// excluding Basic tier VMs).");
        sb.AppendLine("/// To update, run the GenVmSizes tool:");
        sb.AppendLine("/// <code>dotnet run --project src/Aspire.Hosting.Azure.Kubernetes/tools GenVmSizes.cs</code>");
        sb.AppendLine("/// VM size availability varies by region. Not all sizes may be available in every region.");
        sb.AppendLine("/// </remarks>");
        sb.AppendLine("public static partial class AksNodeVmSizes");
        sb.AppendLine("{");

        var groups = sizes.GroupBy(s => s.Family)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var firstClass = true;
        foreach (var group in groups)
        {
            if (!firstClass)
            {
                sb.AppendLine();
            }
            firstClass = false;

            var className = FamilyToClassName(group.Key);
            var familyDisplayName = group.Key.EndsWith("Family", StringComparison.OrdinalIgnoreCase)
                ? group.Key
                : group.Key + " family";

            sb.AppendLine("    /// <summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    /// VM sizes in the {EscapeXml(familyDisplayName)}.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public static class {className}");
            sb.AppendLine("    {");

            var firstField = true;
            foreach (var size in group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!firstField)
                {
                    sb.AppendLine();
                }
                firstField = false;

                var fieldName = VmSizeToFieldName(size.Name);
                var description = BuildDescription(size);

                sb.AppendLine("        /// <summary>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        /// {EscapeXml(description)}");
                sb.AppendLine("        /// </summary>");
                sb.AppendLine("        [AspireValue(\"AksNodeVmSizes\")]");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        public const string {fieldName} = \"{size.Name}\";");
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildDescription(VmSizeInfo size)
    {
        var parts = new List<string> { size.Name };
        if (size.VCpus is not null)
        {
            parts.Add($"{size.VCpus} vCPUs");
        }
        if (size.MemoryGB is not null)
        {
            parts.Add($"{size.MemoryGB} GB RAM");
        }
        if (size.GpuCount is not null && size.GpuCount != "0")
        {
            parts.Add($"{size.GpuCount} GPU(s)");
        }
        if (size.PremiumIO)
        {
            parts.Add("Premium SSD");
        }
        return string.Join(" — ", parts);
    }

    private static string FamilyToClassName(string family)
    {
        // Convert family names like "standardDSv2Family" to "StandardDSv2"
        var name = family.Replace("Family", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("_", "");

        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name[1..];
        }

        // Clean non-identifier chars
        return CleanIdentifier(name);
    }

    private static string VmSizeToFieldName(string vmSize)
    {
        // "Standard_D4s_v5" → "StandardD4sV5"
        var parts = vmSize.Split('_');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    sb.Append(part[1..]);
                }
            }
        }
        return CleanIdentifier(sb.ToString());
    }

    private static string CleanIdentifier(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString();

        // Ensure doesn't start with a digit
        if (result.Length > 0 && char.IsDigit(result[0]))
        {
            result = "_" + result;
        }

        return result;
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");
}
