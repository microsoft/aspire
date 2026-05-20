// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Serializes tests that launch nested <c>dotnet build</c> or <c>dotnet run</c> tool processes.
/// Those child processes share repository-level build output directories, so running them in
/// parallel can race while generating intermediate files.
/// </summary>
[CollectionDefinition("Tool build tests", DisableParallelization = true)]
public sealed class ToolBuildCollection
{
    public const string Name = "Tool build tests";
}
