// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsBundleTests
{
    private const string AspireSkillDescription = "Aspire CLI commands and workflows for distributed apps";
    private const string AspireifySkillDescription = "One-time setup: wire up AppHost with discovered projects";

    private static AgentAssetDefinition AspireAgentAssetDefinition => AgentAssetDefinition.CreateAspireSkillsBundle(
        CommonAgentApplicators.AspireSkillName,
        AspireSkillDescription,
        AgentAssetKind.Skill,
        installExcludedRelativePaths: ["evals"]);

    private static AgentAssetDefinition AspireifyAgentAssetDefinition => AgentAssetDefinition.CreateAspireSkillsBundle(
        CommonAgentApplicators.AspireifyName,
        AspireifySkillDescription,
        AgentAssetKind.Skill);

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

            var bundle = await LoadBundleAsync(new DirectoryInfo(bundleDirectory));
            var files = await bundle.GetAgentAssetFilesAsync(AspireAgentAssetDefinition, CancellationToken.None);

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
    public async Task GetAgentAssetDefinitions_ReturnsManifestSkills()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(),
                ["references/app-commands.md"] = "# App commands"
            });

            var bundle = await LoadBundleAsync(new DirectoryInfo(bundleDirectory));
            var skill = Assert.Single(bundle.GetAgentAssetDefinitions());

            Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
            Assert.Equal(AspireSkillDescription, skill.Description);
            Assert.True(skill.IsDefault);
            Assert.Equal(AgentAssetSourceKind.AspireSkillsBundle, skill.SourceKind);
            Assert.Equal(["evals"], skill.InstallExcludedRelativePaths);
            Assert.Empty(skill.ApplicableLanguages);
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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

            Assert.Contains("description", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ValidatesSkillAssetAfterAllFiles()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
            Directory.CreateDirectory(skillDirectory);
            var referencePath = Path.Combine(skillDirectory, "reference.md");
            await File.WriteAllTextAsync(referencePath, "# Reference");

            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new BundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "reference.md",
                                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                            }
                        ]
                    }
                ]
            };
            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

            Assert.Contains("failed SHA-256 verification", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillDoesNotContainSkillFile()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
            Directory.CreateDirectory(skillDirectory);
            var referencePath = Path.Combine(skillDirectory, "reference.md");
            await File.WriteAllTextAsync(referencePath, "# Reference");

            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new BundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "reference.md",
                                Sha256 = ComputeSha256(referencePath)
                            }
                        ]
                    }
                ]
            };
            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

            Assert.Contains("must contain SKILL.md", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillNamesDifferOnlyByCase()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteSkillAsync(bundleDirectory, CommonAgentApplicators.AspireSkillName, CreateSkillFileContent());
            await WriteSkillAsync(bundleDirectory, "Aspire", CreateSkillFileContent("Aspire"));

            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    CreateManifestSkill(bundleDirectory, CommonAgentApplicators.AspireSkillName, AspireSkillDescription),
                    CreateManifestSkill(bundleDirectory, "Aspire", AspireSkillDescription)
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

            Assert.Contains("duplicate skill", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Directory.CreateDirectory(Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName));
        await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName, "SKILL.md"), CreateSkillFileContent());

        try
        {
            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new BundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "../SKILL.md",
                                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                            }
                        ]
                    }
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

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
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireifyName);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var skillContent = CreateSkillFileContent(CommonAgentApplicators.AspireifyName, AspireifySkillDescription, "# Aspireify");
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
                      "name": "{{CommonAgentApplicators.AspireifyName}}",
                      "description": "{{AspireifySkillDescription}}",
                      "files": [
                        { "relativePath": "SKILL.md", "sha256": "{{ComputeSha256(skillPath)}}" }
                      ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);

            var bundle = await LoadBundleAsync(new DirectoryInfo(bundleDirectory));
            var files = await bundle.GetAgentAssetFilesAsync(AspireifyAgentAssetDefinition, CancellationToken.None);

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
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, CreateSkillFileContent());

        try
        {
            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Assets =
                [
                    new BundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha256 = ComputeSha256(skillPath)
                            }
                        ]
                    }
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(new DirectoryInfo(bundleDirectory)));

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
                supports: new BundleSupports { AspireCli = ">=99.0.0 <100.0.0" });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.4.0",
                currentSdkVersion: "13.4.0"));

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
                supports: new BundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundle = await LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.4.0-pr.17323.gf2228d9b",
                currentSdkVersion: "13.4.0");

            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_SkipCompatibilityCheck_AllowsBundleOutsideSupportsRange()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() },
                supports: new BundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundle = await LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.5.0-pr.17553.gca8e5ace",
                currentSdkVersion: "13.5.0",
                skipCompatibilityCheck: true);

            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_SkipCompatibilityCheck_StillRejectsOtherInvariants()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() });

            // Truncate the bundled SKILL.md so the SHA-256 in the manifest no longer matches.
            // The compatibility skip must not bypass content verification.
            var skillPath = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, "tampered");

            await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                currentCliVersion: "13.5.0",
                currentSdkVersion: "13.5.0",
                skipCompatibilityCheck: true));
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BundleProvider_LoadAsync_ExtensionKind_ReturnsExtensionBundleAssets()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            const string extensionName = "aspire-canvas";
            var extensionDirectory = Path.Combine(bundleDirectory, "extensions", extensionName);
            Directory.CreateDirectory(extensionDirectory);
            var extensionPath = Path.Combine(extensionDirectory, "extension.mjs");
            await File.WriteAllTextAsync(extensionPath, "export default {};");

            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new BundleAsset
                    {
                        Name = extensionName,
                        Description = "Aspire canvas extension",
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "extension.mjs",
                                Sha256 = ComputeSha256(extensionPath)
                            }
                        ]
                    }
                ]
            };
            var manifestJson = JsonSerializer.Serialize(new
            {
                version = manifest.Version,
                supports = manifest.Supports,
                extensions = manifest.Assets
            });
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "extension-manifest.json"), manifestJson);

            var bundle = await LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                assetKind: AgentAssetKind.Extension,
                currentCliVersion: "13.4.0",
                currentSdkVersion: "13.4.0",
                skipCompatibilityCheck: false);
            var extension = Assert.Single(bundle.GetAgentAssetDefinitions());
            var files = await bundle.GetAgentAssetFilesAsync(extension, CancellationToken.None);

            Assert.Equal(AgentAssetKind.Extension, bundle.AssetKind);
            Assert.Equal(AgentAssetKind.Extension, extension.AssetKind);
            Assert.Equal(AgentAssetSourceKind.AspireSkillsBundle, extension.SourceKind);
            Assert.Equal("extension.mjs", Assert.Single(files).RelativePath);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BundleProvider_LoadAsync_ExtensionKind_ThrowsWhenExtensionDoesNotContainModuleFile()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            const string extensionName = "aspire-canvas";
            var extensionDirectory = Path.Combine(bundleDirectory, "extensions", extensionName);
            Directory.CreateDirectory(extensionDirectory);
            var readmePath = Path.Combine(extensionDirectory, "README.md");
            await File.WriteAllTextAsync(readmePath, "# Aspire canvas extension");

            var manifest = new BundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new BundleAsset
                    {
                        Name = extensionName,
                        Description = "Aspire canvas extension",
                        Files =
                        [
                            new BundleFile
                            {
                                RelativePath = "README.md",
                                Sha256 = ComputeSha256(readmePath)
                            }
                        ]
                    }
                ]
            };
            var manifestJson = JsonSerializer.Serialize(new
            {
                version = manifest.Version,
                supports = manifest.Supports,
                extensions = manifest.Assets
            });
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "extension-manifest.json"), manifestJson);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(
                new DirectoryInfo(bundleDirectory),
                assetKind: AgentAssetKind.Extension,
                currentCliVersion: "13.4.0",
                currentSdkVersion: "13.4.0",
                skipCompatibilityCheck: false));

            Assert.Contains("must contain extension.mjs", exception.Message);
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
        BundleSupports? supports = null)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(skillDirectory, AspireSkillsBundle.NormalizeRelativePath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }

        var manifest = new BundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = supports ?? CreateSupports(),
            Assets =
            [
                new BundleAsset
                {
                    Name = CommonAgentApplicators.AspireSkillName,
                    Description = AspireSkillDescription,
                    InstallExcludedRelativePaths = ["evals"],
                    Files = files
                        .Select(file => new BundleFile
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

    private static BundleSupports CreateSupports()
    {
        return new BundleSupports
        {
            AspireCli = ">=0.0.0 <999.0.0",
            AspireSdk = ">=0.0.0 <999.0.0"
        };
    }

    private static async Task WriteSkillAsync(string bundleDirectory, string skillName, string content)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", skillName);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), content);
    }

    private static BundleAsset CreateManifestSkill(string bundleDirectory, string skillName, string description)
    {
        return new BundleAsset
        {
            Name = skillName,
            Description = description,
            Files =
            [
                new BundleFile
                {
                    RelativePath = "SKILL.md",
                    Sha256 = ComputeSha256(Path.Combine(bundleDirectory, "skills", skillName, "SKILL.md"))
                }
            ]
        };
    }

    private static Task WriteManifestAsync(string bundleDirectory, BundleManifest manifest)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            version = manifest.Version,
            supports = manifest.Supports,
            skills = manifest.Assets
        });
        return File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);
    }

    private static Task<AspireSkillsBundle> LoadBundleAsync(
        DirectoryInfo bundleDirectory,
        AgentAssetKind assetKind = AgentAssetKind.Skill,
        string currentCliVersion = AspireSkillsInstaller.Version,
        string currentSdkVersion = AspireSkillsInstaller.Version,
        bool skipCompatibilityCheck = false)
    {
        return new AspireSkillsBundleProvider().LoadAsync(
            assetKind,
            bundleDirectory,
            currentCliVersion,
            currentSdkVersion,
            skipCompatibilityCheck,
            CancellationToken.None);
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
