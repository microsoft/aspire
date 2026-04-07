// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Documentation.Docs;

/// <summary>
/// Resolves configuration for the Aspire llms.txt docs source.
/// </summary>
internal static class DocsSourceConfiguration
{
    private const string IndexCacheKeyPrefix = "index:";

    /// <summary>
    /// Configuration key for overriding the llms.txt source URL.
    /// </summary>
    public const string LlmsTxtUrlConfigKey = "Aspire:Cli:Docs:LlmsTxtUrl";

    /// <summary>
    /// Default URL for the abridged Aspire llms.txt documentation source.
    /// </summary>
    public const string DefaultLlmsTxtUrl = "https://aspire.dev/llms-small.txt";

    /// <summary>
    /// Gets the URL used to fetch the abridged Aspire llms.txt documentation source.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <returns>The resolved documentation source URL.</returns>
    public static string GetLlmsTxtUrl(IConfiguration configuration)
        => configuration[LlmsTxtUrlConfigKey] ?? DefaultLlmsTxtUrl;

    /// <summary>
    /// Gets a source-specific cache key for the parsed llms.txt index.
    /// </summary>
    /// <param name="llmsTxtUrl">The configured documentation source URL.</param>
    /// <returns>The cache key used for the parsed documentation index.</returns>
    public static string GetIndexCacheKey(string llmsTxtUrl)
        => $"{IndexCacheKeyPrefix}{llmsTxtUrl.Trim()}";
}
