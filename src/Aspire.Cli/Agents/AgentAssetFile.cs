// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies how installed file content is compared with an agent asset payload.
/// </summary>
internal enum AgentAssetFileComparison
{
    /// <summary>
    /// File bytes must match exactly.
    /// </summary>
    ExactBytes,

    /// <summary>
    /// UTF-8 text is compared after normalizing line endings.
    /// </summary>
    NormalizedUtf8Text,
}

/// <summary>
/// Represents a validated file contained by an <see cref="AgentFileAssetDefinition"/>.
/// </summary>
internal sealed class AgentAssetFile
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentAssetFile(string relativePath, string content)
        : this(
            relativePath,
            Encoding.UTF8.GetBytes(content),
            AgentAssetFileComparison.NormalizedUtf8Text)
    {
    }

    public AgentAssetFile(
        string relativePath,
        ReadOnlySpan<byte> content,
        AgentAssetFileComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        RelativePath = relativePath;
        Bytes = content.ToArray();
        Comparison = comparison;
    }

    /// <summary>
    /// Gets the path relative to the asset directory.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the immutable file content.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Gets the file content decoded as strict UTF-8 text.
    /// </summary>
    public string Content => DecodeText(Bytes.Span);

    /// <summary>
    /// Gets how existing file content is compared with this payload.
    /// </summary>
    public AgentAssetFileComparison Comparison { get; }

    /// <summary>
    /// Gets whether existing file bytes represent the same content.
    /// </summary>
    public bool ContentEquals(ReadOnlySpan<byte> existingContent)
    {
        if (Comparison is AgentAssetFileComparison.ExactBytes)
        {
            return Bytes.Span.SequenceEqual(existingContent);
        }

        try
        {
            return string.Equals(
                Content.ReplaceLineEndings("\n"),
                DecodeText(existingContent).ReplaceLineEndings("\n"),
                StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes strict UTF-8 content, discarding a leading byte order mark when present.
    /// </summary>
    internal static string DecodeText(ReadOnlySpan<byte> content)
    {
        var text = s_strictUtf8.GetString(content);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
