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
    /// Text is decoded with BOM detection and compared after normalizing line endings.
    /// </summary>
    NormalizedText,

    /// <summary>
    /// UTF-8 text is compared after normalizing line endings.
    /// </summary>
    NormalizedUtf8Text,
}

/// <summary>
/// Represents a validated file contained by an <see cref="AgentAssetDefinition"/>.
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
            AgentAssetFileComparison.NormalizedText)
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
    /// Gets the file content decoded according to its text comparison policy.
    /// </summary>
    public string Content => DecodeContent(Bytes.Span);

    /// <summary>
    /// Gets how existing file content is compared with this payload.
    /// </summary>
    public AgentAssetFileComparison Comparison { get; }

    /// <summary>
    /// Gets whether existing file bytes represent the same content.
    /// </summary>
    public bool ContentEquals(ReadOnlySpan<byte> existingContent)
    {
        // Identical payloads are always equivalent, including non-UTF-8 files whose
        // extensions normally indicate text (for example a UTF-16 PowerShell script).
        if (Bytes.Span.SequenceEqual(existingContent))
        {
            return true;
        }

        if (Comparison is AgentAssetFileComparison.ExactBytes)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Content.ReplaceLineEndings("\n"),
                DecodeContent(existingContent).ReplaceLineEndings("\n"),
                StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes text with the same BOM detection and replacement fallback as skill file reads.
    /// </summary>
    internal static string DecodeText(ReadOnlySpan<byte> content)
    {
        // Skills have historically used StreamReader/File.ReadAllText, including support
        // for UTF-16/UTF-32 BOMs and replacement decoding. Binary extension support must
        // not tighten that existing text contract.
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private string DecodeContent(ReadOnlySpan<byte> content)
    {
        if (Comparison is AgentAssetFileComparison.NormalizedText)
        {
            return DecodeText(content);
        }

        var text = s_strictUtf8.GetString(content);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
