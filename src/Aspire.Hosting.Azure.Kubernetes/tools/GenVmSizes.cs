// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Fetch VM sizes from Azure REST API using the 'az' CLI
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

Console.WriteLine($"Using subscription: {subscriptionId}");

// Query all US regions for VM SKUs to build a comprehensive unified list.
// Different regions offer different VM sizes, so querying multiple regions
// ensures we capture the full set available across the US.
string[] usRegions = [
    "eastus", "eastus2", "centralus", "northcentralus", "southcentralus",
    "westus", "westus2", "westus3", "westcentralus"
];

var allSkus = new List<ResourceSku>();
foreach (var region in usRegions)
{
    Console.WriteLine($"Querying VM SKUs for {region}...");
    var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{region}'";
    var json = await RunAzCommand($"rest --method get --url \"{url}\"").ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(json))
    {
        Console.Error.WriteLine($"Warning: Failed to fetch VM SKUs for {region}, skipping.");
        continue;
    }

    var skuResponse = JsonSerializer.Deserialize<SkuResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (skuResponse?.Value is not null)
    {
        allSkus.AddRange(skuResponse.Value);
        Console.WriteLine($"  Found {skuResponse.Value.Count} SKUs in {region}");
    }
}

if (allSkus.Count == 0)
{
    Console.Error.WriteLine("Error: Failed to fetch VM SKUs from any US region.");
    return 1;
}

// Filter to virtualMachines, deduplicate by name (keep first occurrence for capability data)
var vmSkus = allSkus
    .Where(s => s.ResourceType == "virtualMachines" && !string.IsNullOrEmpty(s.Name))
    .GroupBy(s => s.Name)
    .Select(g => g.First())
    .Select(s => new VmSizeInfo
    {
        Name = s.Name!,
        Family = s.Family ?? "Other",
        VCpus = s.GetCapabilityValue("vCPUs"),
        MemoryGB = s.GetCapabilityValue("MemoryGB"),
        MaxDataDiskCount = s.GetCapabilityValue("MaxDataDiskCount"),
        PremiumIO = s.GetCapabilityBool("PremiumIO"),
        AcceleratedNetworking = s.GetCapabilityBool("AcceleratedNetworkingEnabled"),
        GpuCount = s.GetCapabilityValue("GPUs"),
    })
    .DistinctBy(s => s.Name)
    .OrderBy(s => s.Family, StringComparer.OrdinalIgnoreCase)
    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"Found {vmSkus.Count} VM sizes");

var code = VmSizeClassGenerator.GenerateCode("Aspire.Hosting.Azure.Kubernetes", vmSkus);
File.WriteAllText(Path.Combine("..", "AksNodeVmSizes.Generated.cs"), code);
Console.WriteLine($"Generated AksNodeVmSizes.Generated.cs with {vmSkus.Count} VM sizes");

return 0;

static async Task<string?> RunAzCommand(string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = "az",
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

public sealed class SkuResponse
{
    [JsonPropertyName("value")]
    public List<ResourceSku>? Value { get; set; }
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
        sb.AppendLine("/// This class is auto-generated from Azure Resource SKUs across all US regions.");
        sb.AppendLine("/// To update, run the GenVmSizes tool:");
        sb.AppendLine("/// <code>dotnet run --project src/Aspire.Hosting.Azure.Kubernetes/tools GenVmSizes.cs</code>");
        sb.AppendLine("/// VM size availability varies by region. This list is a union of sizes available");
        sb.AppendLine("/// across eastus, eastus2, centralus, northcentralus, southcentralus, westus, westus2,");
        sb.AppendLine("/// westus3, and westcentralus. Not all sizes may be available in every region.");
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

            sb.AppendLine("    /// <summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    /// VM sizes in the {EscapeXml(group.Key)} family.");
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
                sb.AppendLine("        [global::Aspire.Hosting.AspireValue(\"AksNodeVmSizes\")]");
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
