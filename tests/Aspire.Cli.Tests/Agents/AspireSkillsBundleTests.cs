// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsBundleTests
{
    [Fact]
    public async Task LoadAsync_ValidatesManifestAndReturnsInstallableFiles()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(),
                ["references/app-commands.md"] = "# App commands",
                ["evals/evals.json"] = "{}"
            });

            var bundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None);
            var files = await bundle.GetSkillFilesAsync(SkillDefinition.Aspire, CancellationToken.None);

            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
            Assert.Contains(files, file => file.RelativePath == "SKILL.md");
            Assert.Contains(files, file => file.RelativePath == Path.Combine("references", "app-commands.md"));
            Assert.DoesNotContain(files, file => file.RelativePath == Path.Combine("evals", "evals.json"));
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenHashDoesNotMatch()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent()
            }, hashOverride: "0000000000000000000000000000000000000000000000000000000000000000");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None));

            Assert.Contains("failed SHA-256 verification", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillDescriptionExceedsAgentHostLimit()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(description: new string('a', 1025))
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None));

            Assert.Contains("description", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenFilePathEscapesSkillRoot()
    {
        var bundleDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name));
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name, "SKILL.md"), CreateSkillFileContent());

        try
        {
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Skills =
                [
                    new SkillBundleSkill
                    {
                        Name = SkillDefinition.Aspire.Name,
                        Description = SkillDefinition.Aspire.Description,
                        IsDefault = true,
                        Files =
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "../SKILL.md",
                                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                            }
                        ]
                    }
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None));

            Assert.Contains("is not safe", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetSkillFilesAsync_TreatsMissingOptionalPathArraysAsEmpty()
    {
        var bundleDirectory = CreateTempDirectory();
        var skillDirectory = Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspireify.Name);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var skillContent = CreateSkillFileContent(SkillDefinition.Aspireify.Name, SkillDefinition.Aspireify.Description, "# Aspireify");
        await File.WriteAllTextAsync(skillPath, skillContent);

        try
        {
            var manifestJson =
                $$"""
                {
                  "version": "{{AspireSkillsInstaller.Version}}",
                  "supports": {
                    "aspireCli": ">=0.0.0 <999.0.0",
                    "aspireSdk": ">=0.0.0 <999.0.0"
                  },
                  "skills": [
                    {
                      "name": "{{SkillDefinition.Aspireify.Name}}",
                      "description": "{{SkillDefinition.Aspireify.Description}}",
                      "isDefault": true,
                      "files": [
                        { "relativePath": "SKILL.md", "sha256": "{{ComputeSha256(skillPath)}}" }
                      ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);

            var bundle = await AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None);
            var files = await bundle.GetSkillFilesAsync(SkillDefinition.Aspireify, CancellationToken.None);

            var skillFile = Assert.Single(files);
            Assert.Equal("SKILL.md", skillFile.RelativePath);
            Assert.Equal(skillContent, skillFile.Content);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSupportsAreMissing()
    {
        var bundleDirectory = CreateTempDirectory();
        var skillDirectory = Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, CreateSkillFileContent());

        try
        {
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Skills =
                [
                    new SkillBundleSkill
                    {
                        Name = SkillDefinition.Aspire.Name,
                        Description = SkillDefinition.Aspire.Description,
                        IsDefault = true,
                        Files =
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha256 = ComputeSha256(skillPath)
                            }
                        ]
                    }
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireSkillsBundle.LoadAsync(new DirectoryInfo(bundleDirectory), CancellationToken.None));

            Assert.Contains("supported Aspire versions", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenCurrentCliVersionIsUnsupported()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() },
                supports: new SkillBundleSupports { AspireCli = ">=99.0.0 <100.0.0" });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireSkillsBundle.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.4.0",
                currentSdkVersion: "13.4.0",
                CancellationToken.None));

            Assert.Contains("supports Aspire CLI versions", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_TreatsCurrentCliPrereleaseAsReleaseForCompatibilityRange()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() },
                supports: new SkillBundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundle = await AspireSkillsBundle.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.4.0-pr.17323.gf2228d9b",
                currentSdkVersion: "13.4.0",
                CancellationToken.None);

            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    private static async Task CreateBundleAsync(
        string bundleDirectory,
        Dictionary<string, string> files,
        string? hashOverride = null,
        SkillBundleSupports? supports = null)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", SkillDefinition.Aspire.Name);
        Directory.CreateDirectory(skillDirectory);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(skillDirectory, AspireSkillsBundle.NormalizeRelativePath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = supports ?? CreateSupports(),
            Skills =
            [
                new SkillBundleSkill
                {
                    Name = SkillDefinition.Aspire.Name,
                    Description = SkillDefinition.Aspire.Description,
                    IsDefault = true,
                    InstallExcludedRelativePaths = ["evals"],
                    Files = files
                        .Select(file => new SkillBundleFile
                        {
                            RelativePath = file.Key,
                            Sha256 = hashOverride ?? ComputeSha256(Path.Combine(skillDirectory, AspireSkillsBundle.NormalizeRelativePath(file.Key)))
                        })
                        .ToArray()
                }
            ]
        };

        await WriteManifestAsync(bundleDirectory, manifest);
    }

    private static SkillBundleSupports CreateSupports()
    {
        return new SkillBundleSupports
        {
            AspireCli = ">=0.0.0 <999.0.0",
            AspireSdk = ">=0.0.0 <999.0.0"
        };
    }

    private static Task WriteManifestAsync(string bundleDirectory, SkillBundleManifest manifest)
    {
        var manifestJson = JsonSerializer.Serialize(manifest, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest);
        return File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CreateSkillFileContent(
        string name = "aspire",
        string description = "Aspire CLI commands and workflows for distributed apps",
        string body = "# Aspire")
    {
        return $$"""
            ---
            name: {{name}}
            description: "{{description}}"
            ---

            {{body}}
            """;
    }

    private static string CreateTempDirectory()
    {
        return Directory.CreateTempSubdirectory("aspire-skills-bundle-test-").FullName;
    }
}
