// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#:property PublishAot=false
#:package Markdig

using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

var isFoundryLocal = args.Contains("--local");

using var mc = new ModelClient(isFoundryLocal);
var allModelsResponse = await mc.GetAllModelsAsync().ConfigureAwait(false);

// Generate C# extension methods for the models
var generatedCode = isFoundryLocal
    ? GenerateLocalCode("Aspire.Hosting.Foundry", allModelsResponse.Entities ?? [])
    : GenerateHostedCode("Aspire.Hosting.Foundry", allModelsResponse.Entities ?? []);

// Write the generated code to a file
var filename = isFoundryLocal
    ? Path.Combine("..", "FoundryModel.Local.Generated.cs")
    : Path.Combine("..", "FoundryModel.Generated.cs");

File.WriteAllText(filename, generatedCode);
Console.WriteLine($"Generated extension methods written to {Path.GetFileName(filename)}");

// Also serialize the strongly typed response for output with pretty printing
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

Console.WriteLine("\nModel data:");
Console.WriteLine(JsonSerializer.Serialize(allModelsResponse, options));

string GenerateHostedCode(string csNamespace, List<ModelEntity> models)
{
    var sb = new StringBuilder();
    // Add file header
    sb.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
    sb.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
    sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {csNamespace};");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Generated strongly typed model descriptors for Microsoft Foundry.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public partial class FoundryModel");
    sb.AppendLine("{");

    // Group models by publisher (only include models that are visible and have names & publishers)
    var modelsByPublisher = models
        .Where(m => m.Annotations?.SystemCatalogData?.Publisher != null &&
                    m.Annotations?.Name != null &&
                    IsVisible(m.Annotations?.InvisibleUntil))
        .GroupBy(m => m.Annotations!.SystemCatalogData!.Publisher!)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    var firstClass = true;
    foreach (var publisherGroup in modelsByPublisher)
    {
        if (!firstClass)
        {
            sb.AppendLine();
        }

        firstClass = false;

        sb.AppendLine(CultureInfo.InvariantCulture, $"    /// <summary>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    /// Models published by {EscapeXml(publisherGroup.Key)}.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public static partial class {ToPascalCase(publisherGroup.Key)}");
        sb.AppendLine("    {");

        var firstMethod = true;
        foreach (var model in publisherGroup.OrderBy(m => m.Annotations!.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!firstMethod)
            {
                sb.AppendLine();
            }

            firstMethod = false;

            var modelName = model.Annotations!.Name!;
            var descriptorName = ToPascalCase(modelName); // Reuse method name logic for descriptor property name
            var version = GetModelVersion(model);
            var publisher = model.Annotations!.SystemCatalogData!.Publisher!;
            var summaryElement = CreateSummaryElement(model.Annotations?.SystemCatalogData?.Summary ?? model.Annotations?.Description ?? $"Descriptor for {modelName} model");

            AppendXmlDocComment(sb, indentSpaces: 8, summaryElement);
            sb.AppendLine(CultureInfo.InvariantCulture, $"        public static readonly FoundryModel {descriptorName} = new() {{ Name = \"{EscapeStringForCSharp(modelName)}\", Version = \"{EscapeStringForCSharp(version)}\", Format = \"{EscapeStringForCSharp(publisher)}\" }};");
        }

        sb.AppendLine("    }");
    }

    sb.AppendLine("}");
    return sb.ToString();
}

string GenerateLocalCode(string csNamespace, List<ModelEntity> models)
{
    var sb = new StringBuilder();
    // Add file header
    sb.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
    sb.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
    sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {csNamespace};");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Generated strongly typed model descriptors for Microsoft Foundry.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public partial class FoundryModel");
    sb.AppendLine("{");

    // Group models by publisher (only include models that are visible and have names & publishers)
    var modelsByPublisher = models
        .Where(m => m.Annotations?.SystemCatalogData?.Publisher != null &&
                    m.Annotations?.Name != null &&
                    IsVisible(m.Annotations?.InvisibleUntil))
        .GroupBy(m => m.Annotations!.SystemCatalogData!.Publisher!)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    sb.AppendLine("    /// <summary>");
    sb.AppendLine("    /// Models available on Foundry Local.");
    sb.AppendLine("    /// </summary>");
    sb.AppendLine(CultureInfo.InvariantCulture, $"    public static partial class Local");
    sb.AppendLine("    {");

    foreach (var publisherGroup in modelsByPublisher)
    {
        var firstMethod = true;
        foreach (var model in publisherGroup
            .GroupBy(m => m.Annotations!.Tags!["alias"])
            .Select(x => x.First()).OrderBy(m => m.Annotations!.Name, StringComparer.OrdinalIgnoreCase))
        {
            var modelName = model.Annotations!.Tags!["alias"];
            var descriptorName = ToPascalCase(modelName); // Reuse method name logic for descriptor property name
            var version = GetModelVersion(model);
            var publisher = model.Annotations!.SystemCatalogData!.Publisher!;
            var summaryElement = CreateSummaryElement(model.Annotations?.SystemCatalogData?.Summary ?? model.Annotations?.Description ?? $"Descriptor for {modelName} model");

            if (!firstMethod)
            {
                sb.AppendLine();
            }

            firstMethod = false;

            AppendXmlDocComment(sb, indentSpaces: 8, summaryElement);
            sb.AppendLine(CultureInfo.InvariantCulture, $"        public static readonly FoundryModel {descriptorName} = new() {{ Name = \"{EscapeStringForCSharp(modelName)}\", Version = \"{EscapeStringForCSharp(version)}\", Format = \"{EscapeStringForCSharp(publisher)}\" }};");
        }
    }

    sb.AppendLine("    }");

    sb.AppendLine("}");
    return sb.ToString();
}

static void AppendXmlDocComment(StringBuilder sb, int indentSpaces, XElement element)
{
    var indent = new string(' ', indentSpaces);
    var xml = element.ToString();
    using var reader = new StringReader(xml);
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        sb.Append(indent).Append("/// ");
        sb.AppendLine(line);
    }
}

static XElement CreateSummaryElement(string? markdown)
{
    var sanitized = CleanDescription(markdown);
    var summary = new XElement("summary");
    foreach (var node in ConvertMarkdownToXmlNodes(sanitized))
    {
        summary.Add(node);
    }

    var count = summary.Nodes().Count();
    if (count == 0)
    {
        summary.Value = sanitized;
    }

    // If the summary contains a single <para> with only text, simplify to just text
    if (count == 1 &&
        summary.Nodes().First() is XElement paraNode &&
        paraNode.Name == "para" &&
        paraNode.Nodes().All(n => n is XText))
    {
        summary.Value = "\n" + paraNode.Value + "\n";
    }

    return summary;
}

static IEnumerable<XNode> ConvertMarkdownToXmlNodes(string markdown)
{
    if (string.IsNullOrWhiteSpace(markdown))
    {
        yield break;
    }

    var document = Markdown.Parse(markdown, MarkdownPipelineHolder.Instance);

    if (document.Count == 0)
    {
        yield break;
    }

    foreach (var block in document)
    {
        foreach (var node in ConvertMarkdownBlock(block))
        {
            yield return node;
        }
    }
}

static IEnumerable<XNode> ConvertMarkdownBlock(Block block)
{
    switch (block)
    {
        case ParagraphBlock paragraphBlock:
        {
            var para = new XElement("para");
            AddInlineContent(para, ConvertInlines(paragraphBlock.Inline));
            yield return para;
            yield break;
        }
        case HeadingBlock headingBlock:
        {
            var para = new XElement("para");
            var bold = new XElement("b");
            AddInlineContent(bold, ConvertInlines(headingBlock.Inline));
            para.Add(bold);
            yield return para;
            yield break;
        }
        case ListBlock listBlock:
        {
            var listElement = new XElement("list", new XAttribute("type", listBlock.IsOrdered ? "number" : "bullet"));
            foreach (var item in listBlock.OfType<ListItemBlock>())
            {
                listElement.Add(ConvertListItem(item));
            }
            yield return listElement;
            yield break;
        }
        case FencedCodeBlock:
        {
            var codeText = (((LeafBlock)block).Lines.ToString() ?? string.Empty).TrimEnd('\r', '\n');
            var content = string.IsNullOrEmpty(codeText) ? string.Empty : string.Concat(Environment.NewLine, codeText);
            var codeElement = new XElement("code", content);
            yield return codeElement;
            yield break;
        }
    }

    if (block is ContainerBlock containerBlock)
    {
        foreach (var child in containerBlock)
        {
            foreach (var node in ConvertMarkdownBlock(child))
            {
                yield return node;
            }
        }
        yield break;
    }

    if (block is LeafBlock leafBlock)
    {
        var text = leafBlock.Lines.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return new XElement("para", text.Trim());
        }
    }
}

static XElement ConvertListItem(ListItemBlock listItemBlock)
{
    var itemElement = new XElement("item");
    var descriptionElement = new XElement("description");

    foreach (var childBlock in listItemBlock)
    {
        foreach (var node in ConvertMarkdownBlock(childBlock))
        {
            descriptionElement.Add(node);
        }
    }

    itemElement.Add(descriptionElement);
    return itemElement;
}

static IEnumerable<object> ConvertInlines(ContainerInline? container)
{
    if (container is null)
    {
        yield break;
    }

    foreach (var inline in container)
    {
        foreach (var content in ConvertInline(inline))
        {
            yield return content;
        }
    }
}

static IEnumerable<object> ConvertInline(Inline inline)
{
    switch (inline)
    {
        case LiteralInline literalInline:
            yield return literalInline.Content.ToString();
            yield break;
        case CodeInline codeInline:
            yield return new XElement("c", codeInline.Content ?? string.Empty);
            yield break;
        case EmphasisInline emphasisInline when emphasisInline.DelimiterCount >= 2:
        {
            var bold = new XElement("b");
            AddInlineContent(bold, ConvertInlines(emphasisInline));
            yield return bold;
            yield break;
        }
        case EmphasisInline emphasisInline:
        {
            foreach (var content in ConvertInlines(emphasisInline))
            {
                yield return content;
            }
            yield break;
        }
        case LinkInline linkInline:
        {
            var seeElement = new XElement("see");
            if (!string.IsNullOrEmpty(linkInline.Url))
            {
                seeElement.SetAttributeValue("href", linkInline.Url);
            }

            var linkContent = ConvertInlines(linkInline).ToList();
            if (linkContent.Count == 0 && !string.IsNullOrEmpty(linkInline.Url))
            {
                seeElement.Value = linkInline.Url;
            }
            else
            {
                AddInlineContent(seeElement, linkContent);
            }

            yield return seeElement;
            yield break;
        }
        case LineBreakInline lineBreakInline:
            yield return lineBreakInline.IsHard ? new XElement("br") : " ";
            yield break;
        case HtmlInline htmlInline:
        {
            var tag = htmlInline.Tag;
            if (tag is not null)
            {
                // Skip closing tags - they're handled by CollectHtmlElementContent
                if (tag.StartsWith("</", StringComparison.Ordinal))
                {
                    yield break;
                }

                // Parse the HTML tag generically
                var parsedTag = ParseHtmlTag(tag);
                if (parsedTag is not null)
                {
                    var textContent = CollectHtmlElementContent(htmlInline, parsedTag.Value.TagName);
                    var converted = ConvertHtmlTagToXml(parsedTag.Value.TagName, parsedTag.Value.Attributes, textContent);
                    if (converted is not null)
                    {
                        yield return converted;
                    }
                }
            }
            yield break;
        }
        default:
        {
            if (inline is ContainerInline containerInline)
            {
                foreach (var content in ConvertInlines(containerInline))
                {
                    yield return content;
                }
            }
            else
            {
                var fallback = inline?.ToString();
                if (!string.IsNullOrEmpty(fallback))
                {
                    yield return fallback;
                }
            }

            yield break;
        }
    }
}

static string CollectHtmlElementContent(HtmlInline startTag, string elementName)
{
    var content = new StringBuilder();
    var current = startTag.NextSibling;
    var closingTag = $"</{elementName}>";

    while (current is not null)
    {
        if (current is HtmlInline html && html.Tag is not null &&
            html.Tag.StartsWith(closingTag, StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (current is LiteralInline literal)
        {
            content.Append(literal.Content.ToString());
        }
        else if (current is HtmlInline)
        {
            // Skip nested HTML tags
        }
        else
        {
            content.Append(current.ToString());
        }

        current = current.NextSibling;
    }

    return content.ToString();
}

static (string TagName, Dictionary<string, string> Attributes)? ParseHtmlTag(string tag)
{
    // Regex to extract tag name and attributes
    var match = ModelClassGenerator.HtmlTagName().Match(tag);
    if (!match.Success)
    {
        return null;
    }

    var tagName = match.Groups[1].Value;
    var attrText = match.Groups[2].Value;
    var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Simple regex to extract key="value" or key='value' pairs
    var attrMatches = ModelClassGenerator.HtmlAttribute().Matches(attrText);
    foreach (Match attr in attrMatches)
    {
        var name = attr.Groups[1].Value;
        var value = attr.Groups[2].Value;
        attributes[name] = value;
    }

    return (tagName, attributes);
}

static object? ConvertHtmlTagToXml(string tagName, Dictionary<string, string> attributes, string textContent)
{
    switch (tagName.ToLowerInvariant())
    {
        case "sup":
            // Convert <sup> to italics
            if (!string.IsNullOrEmpty(textContent))
            {
                return new XElement("i", textContent);
            }
            return null;

        case "a":
            // Convert <a href="XXX"> to <see href="XXX">
            if (attributes.TryGetValue("href", out var href) && !string.IsNullOrEmpty(href))
            {
                var seeElement = new XElement("see", new XAttribute("href", href));
                seeElement.Value = !string.IsNullOrEmpty(textContent) ? textContent : href;
                return seeElement;
            }
            // If no href, just return the text content
            return !string.IsNullOrEmpty(textContent) ? textContent : null;

        default:
            // Unknown tags - throw away the tag and return just the text content
            return !string.IsNullOrEmpty(textContent) ? textContent : null;
    }
}

static void AddInlineContent(XElement parent, IEnumerable<object> contents)
{
    foreach (var content in contents)
    {
        switch (content)
        {
            case null:
                continue;
            case XNode node:
                parent.Add(node);
                break;
            default:
                parent.Add(new XText(content.ToString() ?? string.Empty));
                break;
        }
    }
}

static string EscapeXml(string? value)
{
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }
    return value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
}

static string ToPascalCase(string modelName)
{
    // Insert a separator when an uppercase letter is found after a lowercase letter or digit
    // e.g. OpenAI-GPT3 -> Open AI GPT3
    // e.g. DeepSeek-V3 -> Deep Seek V3
    // e.g. AI21Labs -> AI 21 Labs

    modelName = ModelClassGenerator.LowerToUpper().Replace(modelName, "$1 $2");
    modelName = ModelClassGenerator.LetterToDigit().Replace(modelName, "$1 $2");

    // Convert model name to PascalCase method name
    var parts = modelName
        .Replace("-", " ")
        .Replace(".", " ")
        .Replace("_", " ")
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

static string GetModelVersion(ModelEntity model)
{
    // Try to get version from different sources
    if (!string.IsNullOrEmpty(model.Properties?.AlphanumericVersion))
    {
        return model.Properties.AlphanumericVersion;
    }

    if (!string.IsNullOrEmpty(model.Version))
    {
        return model.Version;
    }

    if (model.Properties?.Version > 0)
    {
        return model.Properties.Version.ToString(CultureInfo.InvariantCulture);
    }

    return "1"; // Default version
}

static string CleanDescription(string? description)
{
    if (string.IsNullOrWhiteSpace(description))
    {
        return "AI model deployment";
    }

    var sanitized = description
        .Replace(" on CUDA GPUs", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(" on GPUs", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(" on CPUs", string.Empty, StringComparison.OrdinalIgnoreCase);

    // Remove invisible BOM.
    sanitized = sanitized.TrimStart('\uFEFF');

    return sanitized.Trim();
}

static string EscapeStringForCSharp(string value)
{
    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");
}

static bool IsVisible(string? invisibleUntil)
{
    if (string.IsNullOrWhiteSpace(invisibleUntil))
    {
        return true; // No restriction
    }
    if (DateTime.TryParse(invisibleUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
    {
        return dt <= DateTime.UtcNow;
    }
    return true; // If parse fails, be permissive
}

public partial class ModelClassGenerator
{
    [GeneratedRegex("([a-z0-9])([A-Z])")]
    public static partial Regex LowerToUpper();

    [GeneratedRegex("([a-zA-Z])([0-9])")]
    public static partial Regex LetterToDigit();

    [GeneratedRegex(@"<(\w+)([^>]*)>")]
    public static partial Regex HtmlTagName();

    [GeneratedRegex(@"(\w+)\s*=\s*[""']([^""']*)[""']")]
    public static partial Regex HtmlAttribute();
}

public class ModelClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler _handler;
    private readonly bool _isFoundryLocal;
    public ModelClient(bool isFoundryLocal)
    {
        _handler = new HttpClientHandler()
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All
        };

        _httpClient = new HttpClient(_handler);
        _isFoundryLocal = isFoundryLocal;
    }

    public async Task<ConsolidatedResponse> GetAllModelsAsync()
    {
        var allModels = new List<ModelEntity>();
        string? continuationToken = null;
        string? previousToken;

        int pageCount = 0;

        do
        {
            pageCount++;
            Console.WriteLine($"\n=== Fetching page {pageCount} ===");

            var pageResponse = await GetModelsAsync(continuationToken).ConfigureAwait(false);

            // Deserialize the response using our models
            var apiResponse = JsonSerializer.Deserialize<ApiResponse>(pageResponse);

            if (apiResponse?.IndexEntitiesResponse?.Value != null)
            {
                foreach (var model in apiResponse.IndexEntitiesResponse.Value)
                {
                    allModels.Add(model);
                }

                Console.WriteLine($"Fetched page with {apiResponse.IndexEntitiesResponse.Value.Count} models. Total so far: {allModels.Count}");
            }
            else
            {
                Console.WriteLine("No models found in this page response.");
            }

            // Check if there's a continuation token for the next page
            previousToken = continuationToken;
            continuationToken = apiResponse?.IndexEntitiesResponse?.ContinuationToken;

            if (!string.IsNullOrEmpty(continuationToken))
            {
                Console.WriteLine($"Found continuation token for next page: {continuationToken.Substring(0, Math.Min(50, continuationToken.Length))}...");

                // Check for infinite loop (same token repeated)
                if (continuationToken == previousToken)
                {
                    Console.WriteLine("WARNING: Same continuation token received twice. Breaking to prevent infinite loop.");
                    continuationToken = null;
                }
            }

        } while (!string.IsNullOrEmpty(continuationToken));

        Console.WriteLine($"\n=== Pagination Complete ===");
        Console.WriteLine($"Total pages fetched: {pageCount}");
        Console.WriteLine($"Total models collected: {allModels.Count}");

        RunFixups(allModels);

        // Return the consolidated response using our model
        return new ConsolidatedResponse
        {
            TotalCount = allModels.Count,
            PagesCombined = "All pages fetched",
            Entities = allModels
        };
    }

    public async Task<string> GetModelsAsync(string? continuationToken = null)
    {
        var url = "https://ai.azure.com/api/eastus/ux/v1.0/entities/crossRegion";

        var request = new HttpRequestMessage(HttpMethod.Post, url);

        request.Headers.Add("User-Agent", "AzureAiStudio");

        // Build the JSON payload with optional continuation token
        var basePayload = """
        {
            "resourceIds": [
                {"resourceId": "azure-openai", "entityContainerType": "Registry"},
                {"resourceId": "azureml-openai-oss", "entityContainerType": "Registry"},
                {"resourceId": "azureml-msr", "entityContainerType": "Registry"},
                {"resourceId": "azureml", "entityContainerType": "Registry"},
                {"resourceId": "azureml-routers", "entityContainerType": "Registry"},
                {"resourceId": "azureml-phi-prod", "entityContainerType": "Registry"},
                {"resourceId": "azureml-cogsvc", "entityContainerType": "Registry"},
                {"resourceId": "azureml-meta", "entityContainerType": "Registry"},
                {"resourceId": "azureml-mistral", "entityContainerType": "Registry"},
                {"resourceId": "azureml-gretel", "entityContainerType": "Registry"},
                {"resourceId": "nvidia-ai", "entityContainerType": "Registry"},
                {"resourceId": "azureml-nvidia", "entityContainerType": "Registry"},
                {"resourceId": "azureml-ai21", "entityContainerType": "Registry"},
                {"resourceId": "azureml-nixtla", "entityContainerType": "Registry"},
                {"resourceId": "azureml-core42", "entityContainerType": "Registry"},
                {"resourceId": "azureml-cohere", "entityContainerType": "Registry"},
                {"resourceId": "azureml-restricted", "entityContainerType": "Registry"},
                {"resourceId": "HuggingFace", "entityContainerType": "Registry"},
                {"resourceId": "azureml-paige", "entityContainerType": "Registry"},
                {"resourceId": "azureml-bria", "entityContainerType": "Registry"},
                {"resourceId": "azureml-nttdata", "entityContainerType": "Registry"},
                {"resourceId": "azureml-saifr", "entityContainerType": "Registry"},
                {"resourceId": "azureml-saifr-ipp", "entityContainerType": "Registry"},
                {"resourceId": "azureml-saifr-prod", "entityContainerType": "Registry"},
                {"resourceId": "azureml-rockwellautomation", "entityContainerType": "Registry"},
                {"resourceId": "azureml-bayer", "entityContainerType": "Registry"},
                {"resourceId": "azureml-cerence", "entityContainerType": "Registry"},
                {"resourceId": "azureml-sight-machine", "entityContainerType": "Registry"},
                {"resourceId": "azureml-deepseek", "entityContainerType": "Registry"},
                {"resourceId": "azureml-stabilityai", "entityContainerType": "Registry"},
                {"resourceId": "azureml-xai", "entityContainerType": "Registry"},
                {"resourceId": "azureml-blackforestlabs", "entityContainerType": "Registry"},
                {"resourceId": "azureml-anthropic", "entityContainerType": "Registry"}
            ],
            "indexEntitiesRequest": {
                "filters": [
                    {"field": "type", "operator": "eq", "values": ["models"]},
                    {"field": "kind", "operator": "eq", "values": ["Versioned"]},
                    {"field": "properties/isAnonymous", "operator": "ne", "values": ["true"]},
                    {"field": "annotations/archived", "operator": "ne", "values": ["true"]},
                    {"field": "properties/userProperties/is-promptflow", "operator": "notexists"},
                    {"field": "labels", "operator": "eq", "values": ["latest"]},
                    {"field": "annotations/tags/deploymentOptions", "operator": "contains", "values": ["UnifiedEndpointMaaS"]}

                ],
                "freeTextSearch": "",
                "order": [{"field": "properties/name", "direction": "Asc"}],
                "pageSize": 30,
                "facets": [ ],
                "includeTotalResultCount": true,
                "searchBuilder": "AppendPrefix"
            }
        }
        """;

        if (_isFoundryLocal)
        {
            basePayload = $$"""
                {
                    "resourceIds": [
                        {"resourceId": "azureml", "entityContainerType": "Registry"}
                    ],
                    "indexEntitiesRequest": {
                        "filters": [
                            {"field": "type", "operator": "eq", "values": ["models"]},
                            {"field": "kind", "operator": "eq", "values": ["Versioned"]},
                            {"field": "labels", "operator": "eq", "values": ["latest"]},
                            {"field": "annotations/tags/foundryLocal", "operator": "eq", "values": ["", "test"]},
                            {"field": "properties/variantInfo/variantMetadata/device", "operator": "eq",
                                "values": ["cpu", "gpu", "npu"]}
                        ],
                        "freeTextSearch": "",
                        "order": [{"field": "properties/name", "direction": "Asc"}],
                        "pageSize": 30,
                        "facets": [ ],
                        "includeTotalResultCount": true,
                        "searchBuilder": "AppendPrefix"
                    }
                }
                """;
        }

        string jsonContent;
        if (continuationToken != null)
        {
            var root = JsonNode.Parse(basePayload)?.AsObject()!;
            var indexEntitiesRequest = root["indexEntitiesRequest"]?.AsObject()!;
            indexEntitiesRequest.Add("continuationToken", continuationToken);
            indexEntitiesRequest["includeTotalResultCount"] = false;

            jsonContent = root.ToJsonString();
        }
        else
        {
            jsonContent = basePayload;
        }

        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

        // Handle decompression automatically
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return content;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private void RunFixups(List<ModelEntity> allModels)
    {
        if (_isFoundryLocal)
        {
            // Exclude models that are not listed by foundry local (TBD)
            // c.f. https://github.com/microsoft/Foundry-Local/issues/245#issuecomment-3404022929
            allModels.RemoveAll(m => m.Annotations?.Tags?.TryGetValue("alias", out var alias) is true && alias is not null &&
                (alias.Contains("whisper") ||
                alias == "phi-4-reasoning" ||
                alias == "qwen2.5-1.5b-instruct-test-qnn-npu" ||
                alias == "qwen2.5-1.5b-instruct-test-openvino-npu"));
        }
        else
        {
            foreach (var model in allModels)
            {
                // Fix up Phi-4 version to 7 since 8 doesn't work in Azure.
                if (model.Annotations?.Name == "Phi-4")
                {
                    model.Properties!.AlphanumericVersion = "7";
                    model.Version = "7";
                }
            }
        }
    }
}

// Response Models
public class ApiResponse
{
    [JsonPropertyName("indexEntitiesResponse")]
    public IndexEntitiesResponse? IndexEntitiesResponse { get; set; }

    [JsonPropertyName("regionalErrors")]
    public object? RegionalErrors { get; set; }

    [JsonPropertyName("resourceSkipReasons")]
    public object? ResourceSkipReasons { get; set; }

    [JsonPropertyName("shardErrors")]
    public object? ShardErrors { get; set; }

    [JsonPropertyName("numberOfResourcesNotIncludedInSearch")]
    public int NumberOfResourcesNotIncludedInSearch { get; set; }
}

public class IndexEntitiesResponse
{
    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; set; }

    [JsonPropertyName("value")]
    public List<ModelEntity>? Value { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }
}

public class ModelEntity
{
    [JsonPropertyName("relevancyScore")]
    public double RelevancyScore { get; set; }

    [JsonPropertyName("entityResourceName")]
    public string? EntityResourceName { get; set; }

    [JsonPropertyName("highlights")]
    public Dictionary<string, object>? Highlights { get; set; }

    [JsonPropertyName("schemaId")]
    public string? SchemaId { get; set; }

    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("annotations")]
    public ModelAnnotations? Annotations { get; set; }

    [JsonPropertyName("properties")]
    public ModelProperties? Properties { get; set; }

    [JsonPropertyName("internal")]
    public Dictionary<string, object>? Internal { get; set; }

    [JsonPropertyName("updateSequence")]
    public int UpdateSequence { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("entityContainerId")]
    public string? EntityContainerId { get; set; }

    [JsonPropertyName("entityObjectId")]
    public string? EntityObjectId { get; set; }

    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; set; }

    [JsonPropertyName("relationships")]
    public List<object>? Relationships { get; set; }

    [JsonPropertyName("assetId")]
    public string? AssetId { get; set; }

    [JsonPropertyName("usage")]
    public ModelUsage? Usage { get; set; }

    [JsonPropertyName("isAFragment")]
    public bool IsAFragment { get; set; }

    [JsonPropertyName("fragmentId")]
    public string? FragmentId { get; set; }
}

public class ModelAnnotations
{
    [JsonPropertyName("invisibleUntil")]
    public string? InvisibleUntil { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }

    [JsonPropertyName("datasets")]
    public List<object>? Datasets { get; set; }

    [JsonPropertyName("sampleInputData")]
    public object? SampleInputData { get; set; }

    [JsonPropertyName("sampleOutputData")]
    public object? SampleOutputData { get; set; }

    [JsonPropertyName("resourceRequirements")]
    public object? ResourceRequirements { get; set; }

    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("systemCatalogData")]
    public SystemCatalogData? SystemCatalogData { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class SystemCatalogData
{
    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("modelCapabilities")]
    public List<string>? ModelCapabilities { get; set; }

    [JsonPropertyName("deploymentTypes")]
    public List<string>? DeploymentTypes { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("inferenceTasks")]
    public List<string>? InferenceTasks { get; set; }

    [JsonPropertyName("fineTuningTasks")]
    public List<string>? FineTuningTasks { get; set; }

    [JsonPropertyName("inferenceComputeAllowed")]
    public object? InferenceComputeAllowed { get; set; }

    [JsonPropertyName("fineTuneComputeAllowed")]
    public object? FineTuneComputeAllowed { get; set; }

    [JsonPropertyName("evaluationComputeAllowed")]
    public object? EvaluationComputeAllowed { get; set; }

    [JsonPropertyName("languages")]
    public List<string>? Languages { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("textContextWindow")]
    public int? TextContextWindow { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("inputModalities")]
    public List<string>? InputModalities { get; set; }

    [JsonPropertyName("outputModalities")]
    public List<string>? OutputModalities { get; set; }

    [JsonPropertyName("playgroundRateLimitTier")]
    public string? PlaygroundRateLimitTier { get; set; }

    [JsonPropertyName("azureOffers")]
    public List<string>? AzureOffers { get; set; }
}

public class ModelProperties
{
    [JsonPropertyName("updatedTime")]
    public string? UpdatedTime { get; set; }

    [JsonPropertyName("creationContext")]
    public CreationContext? CreationContext { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("modelFramework")]
    public string? ModelFramework { get; set; }

    [JsonPropertyName("modelFrameworkVersion")]
    public string? ModelFrameworkVersion { get; set; }

    [JsonPropertyName("modelFormat")]
    public string? ModelFormat { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("alphanumericVersion")]
    public string? AlphanumericVersion { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("modifiedTime")]
    public string? ModifiedTime { get; set; }

    [JsonPropertyName("unpack")]
    public bool Unpack { get; set; }

    [JsonPropertyName("parentModelId")]
    public string? ParentModelId { get; set; }

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("experimentName")]
    public string? ExperimentName { get; set; }

    [JsonPropertyName("derivedModelIds")]
    public object? DerivedModelIds { get; set; }

    [JsonPropertyName("userProperties")]
    public Dictionary<string, string>? UserProperties { get; set; }

    [JsonPropertyName("isAnonymous")]
    public bool IsAnonymous { get; set; }

    // Not a typo, this is an error in the original API
    [JsonPropertyName("orginAssetId")]
    public string? OriginAssetId { get; set; }

    [JsonPropertyName("intellectualProperty")]
    public IntellectualProperty? IntellectualProperty { get; set; }

    [JsonPropertyName("variantInfo")]
    public object? VariantInfo { get; set; }

    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; set; }
}

public class CreationContext
{
    [JsonPropertyName("createdTime")]
    public string? CreatedTime { get; set; }

    [JsonPropertyName("createdBy")]
    public CreatedBy? CreatedBy { get; set; }

    [JsonPropertyName("creationSource")]
    public string? CreationSource { get; set; }
}

public class CreatedBy
{
    [JsonPropertyName("userObjectId")]
    public string? UserObjectId { get; set; }

    [JsonPropertyName("userTenantId")]
    public string? UserTenantId { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

public class IntellectualProperty
{
    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }
}

public class ModelUsage
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }
}

public class ConsolidatedResponse
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("pagesCombined")]
    public string? PagesCombined { get; set; }

    [JsonPropertyName("entities")]
    public List<ModelEntity>? Entities { get; set; }
}

file static class MarkdownPipelineHolder
{
    public static MarkdownPipeline Instance { get; } = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
}
