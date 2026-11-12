// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

// Retained callable APIs require their wire types even when those types are obsolete.
// Unused obsolete types and obsolete data members are still filtered.
#pragma warning disable CS0612, CS0618

[AspireDto]
public sealed class GoObsoleteDto
{
    public string Current { get; set; } = "";

    [Obsolete]
    public string Legacy { get; set; } = "";
}

[AspireDto]
[Obsolete]
public sealed class GoObsoleteWireDto
{
    public string Value { get; set; } = "";
}

[AspireDto]
[Obsolete]
public sealed class GoUnusedObsoleteDto
{
    public string Value { get; set; } = "";
}

public enum GoObsoleteEnum
{
    Current,

    [Obsolete]
    Legacy
}

[AspireExport]
[Obsolete]
public sealed class GoObsoleteWireHandle
{
}

[AspireExport(ExposeProperties = true, ExposeMethods = true)]
public sealed class GoObsoleteContext
{
    public string Current { get; set; } = "";

    [Obsolete]
    public string Legacy { get; set; } = "";

    public GoObsoleteDto GetDto() => new();

    public GoObsoleteEnum GetEnum() => GoObsoleteEnum.Current;

    public GoObsoleteWireDto GetObsoleteWireDto() => new();

    public GoObsoleteWireHandle GetObsoleteWireHandle() => new();

    [Obsolete]
    public string GetLegacyValue() => "";

    [AspireValue("GoObsoleteValues")]
    public static string CurrentValue => "current";

    [AspireValue("GoObsoleteValues")]
    [Obsolete]
    public static string LegacyValue => "legacy";
}

#pragma warning restore CS0612, CS0618
