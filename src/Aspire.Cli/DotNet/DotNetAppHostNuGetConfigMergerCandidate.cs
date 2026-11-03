// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Holds the baseline and frozen proposed file for a .NET AppHost configuration merge.
/// </summary>
internal sealed class DotNetAppHostNuGetConfigMergerCandidate
{
    private readonly byte[]? _originalContent;
    private readonly byte[] _proposedContent;

    internal DotNetAppHostNuGetConfigMergerCandidate(FileInfo targetFile, byte[]? originalContent, byte[] proposedContent)
    {
        TargetFile = targetFile;
        _originalContent = originalContent is null ? null : [.. originalContent];
        _proposedContent = [.. proposedContent];
    }

    public FileInfo TargetFile { get; }

    public ReadOnlyMemory<byte>? OriginalContent => _originalContent is null
        ? default(ReadOnlyMemory<byte>?)
        : new ReadOnlyMemory<byte>(_originalContent);

    public ReadOnlyMemory<byte> ProposedContent => _proposedContent;

    public XmlDocument? GetOriginalDocument() => _originalContent is null ? null : LoadDocument(_originalContent);

    public XmlDocument GetProposedDocument() => LoadDocument(_proposedContent);

    private static XmlDocument LoadDocument(byte[] content)
    {
        var document = new XmlDocument();
        using var stream = new MemoryStream(content, writable: false);
        document.Load(stream);
        return document;
    }
}
