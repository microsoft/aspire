// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for <see cref="IDistributedApplicationBuilder"/> to create an <see cref="IAspireStore"/> instance.
/// </summary>
public static class AspireStoreExtensions
{
    /// <summary>
    /// Gets a deterministic file path that is a copy of the <paramref name="sourceFilename"/>.
    /// The resulting file name will depend on the content of the file.
    /// </summary>
    /// <param name="aspireStore">The <see cref="IAspireStore"/> instance.</param>
    /// <param name="filenameTemplate">A file name to base the result on.</param>
    /// <param name="sourceFilename">An existing file.</param>
    /// <returns>A deterministic file path with the same content as <paramref name="sourceFilename"/>.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the source file does not exist.</exception>
    [AspireExport(Description = "Gets a deterministic file path for the specified file contents")]
    public static string GetFileNameWithContent(this IAspireStore aspireStore, string filenameTemplate, string sourceFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filenameTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilename);

        if (!File.Exists(sourceFilename))
        {
            throw new FileNotFoundException("The source file does not exist.", sourceFilename);
        }

        using var sourceStream = File.OpenRead(sourceFilename);

        return aspireStore.GetFileNameWithContent(filenameTemplate, sourceStream);
    }
}
