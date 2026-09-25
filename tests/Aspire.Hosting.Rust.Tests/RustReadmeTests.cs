// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Tests;

namespace Aspire.Hosting.Rust.Tests;

public class RustReadmeTests
{
    [Fact]
    public async Task ReadmeDocumentsTheFirstPartyIntegration()
    {
        var readmePath = Path.Combine(MSBuildUtils.GetRepoRoot(), "src", "Aspire.Hosting.Rust", "README.md");
        var readme = await File.ReadAllTextAsync(readmePath, TestContext.Current.CancellationToken);
        var additionalDocumentation = readme[
            readme.IndexOf("## Additional documentation", StringComparison.Ordinal)..
            readme.IndexOf("## Feedback & contributing", StringComparison.Ordinal)];
        var links = Regex.Matches(additionalDocumentation, @"https://[^\s)]+")
            .Select(match => match.Value)
            .ToArray();

        Assert.Equal(
            [
                "https://aspire.dev/integrations/gallery/",
                "https://aspire.dev/",
                "https://doc.rust-lang.org/cargo/"
            ],
            links);
        Assert.StartsWith(
            """
            # Rust hosting integration

            Use this integration to model, configure, and orchestrate a Rust application resource in an Aspire solution.
            """,
            readme);
        Assert.Contains(
            "Then, in the AppHost, add a Rust application resource and reference it from another resource with either C# or TypeScript:",
            readme);
        Assert.Contains(".WithReference(rustApi)", readme);
        Assert.Contains(".withReference(rustApi)", readme);
        Assert.Contains("32-bit ARM targets map to Docker's `linux/arm` platform", readme);
        Assert.Contains("custom build image but can keep the default runtime image", readme);
        Assert.Contains("set both images in a single `WithDockerfileBaseImage` call", readme);
        Assert.Contains("does not bypass target-platform validation", readme);
    }
}
