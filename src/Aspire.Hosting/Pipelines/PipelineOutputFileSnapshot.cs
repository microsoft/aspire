// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

// Git reports modes for tracked files; untracked and custom-hashed files have no Git mode.
internal readonly record struct PipelineOutputFileSnapshot(bool IsLink, ReadOnlyMemory<byte> Hash, string? GitMode = null);
