// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;

namespace Aspire.Cli.Tests.Templating;

public class OutputPathHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void GetUniqueDefaultOutputPath_ReturnsTemplateName_WhenDirectoryDoesNotExist()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("aspire-starter", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./aspire-starter", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_ReturnsTemplateName_WhenDirectoryExistsButIsEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory("aspire-starter");

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("aspire-starter", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./aspire-starter", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_AppendsSuffix_WhenDirectoryExistsAndIsNonEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.CreateDirectory("aspire-starter");
        File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("aspire-starter", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./aspire-starter-2", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_IncrementsUntilUnique()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);

        // Create non-empty directories for aspire-starter and aspire-starter-2
        foreach (var name in new[] { "aspire-starter", "aspire-starter-2" })
        {
            var dir = workspace.CreateDirectory(name);
            File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");
        }

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("aspire-starter", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./aspire-starter-3", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_SkipsNonEmptyAndUsesEmptySlot()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);

        // aspire-starter is non-empty, aspire-starter-2 is empty
        var dir = workspace.CreateDirectory("aspire-starter");
        File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");
        workspace.CreateDirectory("aspire-starter-2");

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("aspire-starter", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./aspire-starter-2", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_StripsInvalidPathCharacters()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("my\0app", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./myapp", result);
    }

    [Fact]
    public void GetUniqueDefaultOutputPath_FallsBackToOutput_WhenAllCharsInvalid()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);

        var result = OutputPathHelper.GetUniqueDefaultOutputPath("\0", workspace.WorkspaceRoot.FullName);

        Assert.Equal("./output", result);
    }

    [Fact]
    public void ValidateOutputPath_ReturnsNull_WhenDirectoryDoesNotExist()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "new-dir");

        var result = OutputPathHelper.ValidateOutputPath(path, workspace.WorkspaceRoot.FullName);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateOutputPath_ReturnsNull_WhenDirectoryExistsButIsEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.CreateDirectory("empty-dir");

        var result = OutputPathHelper.ValidateOutputPath(dir.FullName, workspace.WorkspaceRoot.FullName);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateOutputPath_ReturnsError_WhenDirectoryExistsAndIsNonEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.CreateDirectory("non-empty-dir");
        File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");

        var result = OutputPathHelper.ValidateOutputPath(dir.FullName, workspace.WorkspaceRoot.FullName);

        Assert.NotNull(result);
        Assert.Contains(dir.FullName, result);
    }

    [Fact]
    public void CreateOutputPathValidator_ReturnsSuccess_WhenDirectoryDoesNotExist()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var validator = OutputPathHelper.CreateOutputPathValidator(workspace.WorkspaceRoot.FullName);

        var result = validator("./new-dir");

        Assert.True(result.Successful);
    }

    [Fact]
    public void CreateOutputPathValidator_ReturnsSuccess_WhenDirectoryExistsButIsEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory("empty-dir");

        var validator = OutputPathHelper.CreateOutputPathValidator(workspace.WorkspaceRoot.FullName);

        var result = validator("./empty-dir");

        Assert.True(result.Successful);
    }

    [Fact]
    public void CreateOutputPathValidator_ReturnsError_WhenDirectoryExistsAndIsNonEmpty()
    {
        using var workspace = Utils.TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.CreateDirectory("non-empty-dir");
        File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");

        var validator = OutputPathHelper.CreateOutputPathValidator(workspace.WorkspaceRoot.FullName);

        var result = validator("./non-empty-dir");

        Assert.False(result.Successful);
    }
}
