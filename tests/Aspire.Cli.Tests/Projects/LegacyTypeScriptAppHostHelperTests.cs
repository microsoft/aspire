// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Projects;

public class LegacyTypeScriptAppHostHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void IsLegacyLayout_WithLegacyAppHostOnly_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "// legacy");

        Assert.True(LegacyTypeScriptAppHost.IsLegacyLayout(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void IsLegacyLayout_WithModernAppHostPresent_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "// legacy");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), "// modern");

        Assert.False(LegacyTypeScriptAppHost.IsLegacyLayout(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void IsLegacyLayout_WithNoAppHost_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.False(LegacyTypeScriptAppHost.IsLegacyLayout(workspace.WorkspaceRoot.FullName));
    }

    [Theory]
    [InlineData("apphost.ts", true)]
    [InlineData("APPHOST.TS", true)]
    [InlineData("apphost.mts", false)]
    [InlineData("other.ts", false)]
    public void IsLegacyAppHostFile_MatchesByName(string fileName, bool expected)
    {
        Assert.Equal(expected, LegacyTypeScriptAppHost.IsLegacyAppHostFile(new FileInfo(fileName)));
    }

    [Fact]
    public void RewriteAppHostContent_RewritesSdkImports()
    {
        var legacy =
            """
            import { createBuilder } from './.modules/aspire.js';
            import { foo } from './.modules/base.js';
            import { bar } from './.modules/transport.js';
            """;

        var expected =
            """
            import { createBuilder } from './.aspire/modules/aspire.mjs';
            import { foo } from './.aspire/modules/base.mjs';
            import { bar } from './.aspire/modules/transport.mjs';
            """;

        Assert.Equal(expected, LegacyTypeScriptAppHost.RewriteAppHostContent(legacy));
    }

    [Fact]
    public void RewriteAppHostContent_DoesNotRewriteUnrelatedUserImports()
    {
        // User imports that merely contain a generated file name as a substring (e.g. 'database.js'
        // contains 'base.js') must not be rewritten — only the './.modules/' SDK imports are.
        var legacy =
            """
            import { createBuilder } from './.modules/aspire.js';
            import { db } from './database.js';
            import { svc } from './myaspire.js';
            import { t } from './lib/transport.js';
            """;

        var expected =
            """
            import { createBuilder } from './.aspire/modules/aspire.mjs';
            import { db } from './database.js';
            import { svc } from './myaspire.js';
            import { t } from './lib/transport.js';
            """;

        Assert.Equal(expected, LegacyTypeScriptAppHost.RewriteAppHostContent(legacy));
    }

    [Theory]
    [InlineData("apphost.ts", "apphost.mts")]
    [InlineData(".modules/aspire.ts", ".aspire/modules/aspire.mts")]
    [InlineData(".modules/base.ts", ".aspire/modules/base.mts")]
    [InlineData(".modules/aspire.d.ts", ".aspire/modules/aspire.d.ts")]
    [InlineData("apphost.mts", "apphost.mts")]
    [InlineData("package.json", "package.json")]
    public void RewriteTsConfigIncludeEntry_RewritesLegacyEntries(string entry, string expected)
    {
        Assert.Equal(expected, LegacyTypeScriptAppHost.RewriteTsConfigIncludeEntry(entry));
    }
}
