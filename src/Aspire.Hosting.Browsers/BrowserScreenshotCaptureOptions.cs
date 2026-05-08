// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal readonly record struct BrowserScreenshotCaptureOptions(string Format, int? Quality, bool FullPage)
{
    public static BrowserScreenshotCaptureOptions Default { get; } = new("png", Quality: null, FullPage: false);

    public string FileExtension => Format switch
    {
        "jpeg" => ".jpg",
        "webp" => ".webp",
        _ => ".png"
    };

    public string MimeType => Format switch
    {
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "image/png"
    };
}
