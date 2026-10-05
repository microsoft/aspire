// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Rust.Tests;

// The BuildKit target cache id is derived from the crate's canonical directory so unrelated app hosts cannot
// share one Cargo target directory. Every test crate lives in a fresh temporary directory, so the id changes
// on every run and cannot appear in a snapshot. PublishScopesTheTargetCacheToTheCrateDirectory asserts the
// value itself; snapshots only need to record where it appears in the generated Dockerfile.
internal static partial class RustDockerfileSnapshotScrubber
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        VerifierSettings.AddScrubber(builder =>
        {
            var scrubbed = TargetCacheIdPattern().Replace(builder.ToString(), "aspire-rust-{cacheId}");
            builder.Clear();
            builder.Append(scrubbed);
        });
    }

    [GeneratedRegex("aspire-rust-[0-9a-f]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex TargetCacheIdPattern();
}
