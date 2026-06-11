// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Utils;

internal static class ImageNameGenerator
{
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Utility method for image name generation not intended for polyglot use.")]
    public static string GenerateImageName<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        // Resource names are already constrained to [a-zA-Z0-9-_] by the resource name validation
        // so we just have to lowercase it and return it.
        return builder.Resource.Name.ToLowerInvariant();
    }

    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Utility method for image tag generation not intended for polyglot use.")]
    public static string GenerateImageTag<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        var bytes = Encoding.UTF8.GetBytes($"{builder.ApplicationBuilder.AppHostDirectory}{DateTime.UtcNow.Ticks}");
        var hash = SHA1.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex;
    }
}
