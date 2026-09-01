// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

internal static class FileUploadHelpers
{
    /// <summary>
    /// Default maximum file upload size (100 MB).
    /// </summary>
    internal const long DefaultMaxFileUploadSize = 100 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum file upload size from configuration, defaulting to 100 MB.
    /// Reads from the <see cref="KnownConfigNames.MaxFileUploadSize"/> key.
    /// </summary>
    internal static long GetMaxFileUploadSize(IConfiguration configuration)
    {
        return configuration.GetValue<long?>(KnownConfigNames.MaxFileUploadSize) ?? DefaultMaxFileUploadSize;
    }
}
