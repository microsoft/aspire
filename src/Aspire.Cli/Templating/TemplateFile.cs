// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Represents a single file in a template tree. The relative path uses
/// forward slashes as separators regardless of the source; the renderer
/// translates to the platform separator when writing to disk.
/// </summary>
/// <param name="RelativePath">
/// Path of the file relative to the template root, using forward slashes
/// (e.g. <c>src/App.tsx</c>, <c>.template.config/template.json</c>).
/// </param>
/// <param name="OpenRead">
/// Opens a fresh, readable, seekable-or-not <see cref="Stream"/> over the
/// file's bytes. The caller disposes the stream. Implementations should not
/// cache or share streams between calls.
/// </param>
internal sealed record TemplateFile(string RelativePath, Func<Stream> OpenRead);
