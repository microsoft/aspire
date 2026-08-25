// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREEXTENSION001 // Launch configuration types are experimental.

namespace Aspire.Hosting.Rust;

internal sealed class RustLaunchConfiguration() : ExecutableLaunchConfiguration("rust")
{
    [JsonPropertyName("cargo")]
    public RustCargoLaunchTarget? Cargo { get; set; }

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;
}

internal sealed class RustCargoLaunchTarget
{
    [JsonPropertyName("args")]
    public string[] Args { get; set; } = [];

    /// <summary>
    /// The absolute path of the executable the cargo build in <see cref="Args"/> produces.
    /// </summary>
    /// <remarks>
    /// Resolved from <c>cargo metadata</c> so the debugger can run a plain <c>cargo build</c> and launch this
    /// path, rather than parsing cargo's JSON artifact stream to discover it. Resolving it host-side also
    /// makes the debugged binary the same one <c>cargo run</c> and publishing select, which a build-side
    /// answer cannot be: <c>cargo build</c> ignores <c>default-run</c> and so reports every binary in the
    /// package.
    /// </remarks>
    [JsonPropertyName("executable_path")]
    public string ExecutablePath { get; set; } = string.Empty;
}
