// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#:property PublishAot=false

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? throw new InvalidOperationException("GITHUB_TOKEN is not set");
using var ghClient = new GitHubModelClient(token);
var ghModels = await ghClient.GetModelsAsync().ConfigureAwait(false);
var ghGenerator = new GitHubModelClassGenerator();
var ghCode = GitHubModelClassGenerator.GenerateCode("Aspire.Hosting.GitHub", ghModels);
File.WriteAllText(Path.Combine("..", "GitHubModel.Generated.cs"), ghCode);
Console.WriteLine("Generated GitHub model descriptors written to GitHubModel.Generated.cs");

public sealed class GitHubModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("registry")] public string Registry { get; set; } = string.Empty;
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("rate_limit_tier")] public string RateLimitTier { get; set; } = string.Empty;
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = new();
    [JsonPropertyName("limits")] public GitHubModelLimits? Limits { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("supported_input_modalities")] public List<string> SupportedInputModalities { get; set; } = new();
    [JsonPropertyName("supported_output_modalities")] public List<string> SupportedOutputModalities { get; set; } = new();
}

public sealed class GitHubModelLimits
{
    [JsonPropertyName("max_input_tokens")] public int? MaxInputTokens { get; set; }
    [JsonPropertyName("max_output_tokens")] public int? MaxOutputTokens { get; set; }
}

internal sealed class GitHubModelClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string? _token;
    public GitHubModelClient(string? token) => _token = token;
    public async Task<List<GitHubModel>> GetModelsAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://models.github.ai/catalog/models");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(_token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        }
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var models = await JsonSerializer.DeserializeAsync<List<GitHubModel>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ConfigureAwait(false);
        return models ?? new();
    }
    public void Dispose() => _http.Dispose();
}

internal partial class GitHubModelClassGenerator
{
    public static string GenerateCode(string ns, List<GitHubModel> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
        sb.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generated strongly typed model descriptors for GitHub Models.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public partial class GitHubModel");
        sb.AppendLine("{");
        var validModels = models.Where(m => !string.IsNullOrEmpty(m.Publisher) && !string.IsNullOrEmpty(m.Name))
                           .OrderBy(m => m.Publisher, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        var groups = validModels.GroupBy(m => m.Publisher)
                           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var firstClass = true;
        foreach (var g in groups)
        {
            if (!firstClass)
            {
                sb.AppendLine();
            }

            firstClass = false;
        
            var className = ToPascalCase(g.Key);
            sb.AppendLine("    /// <summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    /// Models published by {EscapeXml(g.Key)}.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public static partial class {className}");
            sb.AppendLine("    {");

            var firstMethod = true;
            foreach (var m in g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!firstMethod)
                {
                    sb.AppendLine();
                }

                firstMethod = false;
            
                var prop = ToPascalCase(m.Name);
                var desc = EscapeXml(Clean(m.Summary ?? $"Descriptor for {m.Name}"));
                sb.AppendLine("        /// <summary>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        /// {desc}");
                sb.AppendLine("        /// </summary>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"        public static readonly GitHubModel {prop} = new() {{ Id = \"{Esc(m.Id)}\" }};");
            }
            sb.AppendLine("    }");
        }
        sb.AppendLine();

        // Generate internal conversion method
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Gets the model identifier string for the specified <see cref=\"GitHubModelName\"/>.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static string GetModelId(GitHubModelName name) => name switch");
        sb.AppendLine("    {");
        foreach (var m in validModels)
        {
            var prop = ToPascalCase(m.Name);
            sb.AppendLine(CultureInfo.InvariantCulture, $"        GitHubModelName.{prop} => \"{Esc(m.Id)}\",");
        }
        sb.AppendLine("        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, \"Unknown GitHub model name.\")");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        sb.AppendLine();

        // Generate internal enum
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Enumerates known GitHub model names for polyglot app host support.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal enum GitHubModelName");
        sb.AppendLine("{");
        var firstEnum = true;
        foreach (var m in validModels)
        {
            if (!firstEnum)
            {
                sb.AppendLine();
            }

            firstEnum = false;

            var prop = ToPascalCase(m.Name);
            var desc = EscapeXml(Clean(m.Summary ?? $"Descriptor for {m.Name}"));
            sb.AppendLine("    /// <summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    /// {desc}");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {prop},");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    static string ToPascalCase(string modelName)
    {
        // Insert a separator when an uppercase letter is found after a lowercase letter or digit
        // e.g. OpenAI-GPT3 -> Open AI GPT3
        // e.g. DeepSeek-V3 -> Deep Seek V3
        // e.g. AI21Labs -> AI 21 Labs

        modelName = LowerToUpper().Replace(modelName, "$1 $2");
        modelName = LetterToDigit().Replace(modelName, "$1 $2");

        // Convert model name to PascalCase method name
        var parts = modelName
            .Replace("-", " ")
            .Replace(".", " ")
            .Replace("_", " ")
            .Replace("+", " Plus ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var result = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                // Handle special cases for numbers and versions
                if (char.IsDigit(part[0]))
                {
                    result.Append(part);
                }
                else
                {
                    if (part.All(char.IsUpper) && part.Length < 3)
                    {
                        // Keep acronyms in uppercase when less than 3 chars
                        result.Append(part);
                        continue;
                    }
                    else
                    {
                        // Convert to PascalCase
                        result.Append(char.ToUpper(part[0]));
                        if (part.Length > 1)
                        {
                            result.Append(part[1..].ToLower());
                        }
                    }
                }
            }
        }

        return result.ToString();
    }
    private static string Clean(string s) => s.Replace('\n', ' ').Replace('\r', ' ').Replace("  ", " ").Trim();
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeXml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    public static partial Regex LowerToUpper();

    [GeneratedRegex("([a-zA-Z])([0-9])")]
    public static partial Regex LetterToDigit();

}
