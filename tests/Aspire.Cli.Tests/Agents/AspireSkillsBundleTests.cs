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
    private const string TestArchiveSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly AspireSkillsBundleProvider s_bundleProvider = new();

    private static SkillDefinition AspireSkillDefinition => SkillDefinition.CreateAspireSkillsBundle(
        CommonAgentApplicators.AspireSkillName,
        AspireSkillDescription,
        installExcludedRelativePaths: ["evals"]);

    private static SkillDefinition AspireifySkillDefinition => SkillDefinition.CreateAspireSkillsBundle(
        CommonAgentApplicators.AspireifySkillName,
        AspireifySkillDescription);

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

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var files = await bundle.GetSkillFilesAsync(AspireSkillDefinition, CancellationToken.None);

            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
            Assert.Equal(TestArchiveSha256, bundle.ArchiveSha256);
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
    public async Task LoadAsync_NormalizesArchiveSha256()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent()
            });
            var uppercaseArchiveSha256 = new string('A', 64);

            var bundle = await s_bundleProvider.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                $"sha256:{uppercaseArchiveSha256}",
                CancellationToken.None);

            Assert.Equal(uppercaseArchiveSha256.ToLowerInvariant(), bundle.ArchiveSha256);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_RetainsInstallableFilesAfterSourceDirectoryIsDeleted()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(),
                ["references/app-commands.md"] = "# App commands"
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            Directory.Delete(bundleDirectory, recursive: true);

            var files = await bundle.GetSkillFilesAsync(AspireSkillDefinition, CancellationToken.None);

            Assert.Collection(
                files,
                skillFile => Assert.Equal(CreateSkillFileContent(), skillFile.Content),
                referenceFile => Assert.Equal("# App commands", referenceFile.Content));
        }
        finally
        {
            if (Directory.Exists(bundleDirectory))
            {
                Directory.Delete(bundleDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSkillDefinitions_ReturnsManifestSkills()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(),
                ["references/app-commands.md"] = "# App commands"
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var skill = Assert.Single(bundle.GetSkillDefinitions());

            Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
            Assert.Equal(AspireSkillDescription, skill.Description);
            Assert.True(skill.IsDefault);
            Assert.Equal(SkillSourceKind.AspireSkillsBundle, skill.SourceKind);
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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("failed SHA-256 verification", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenManifestIsMalformed()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), "{");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Equal("Aspire skills bundle manifest is invalid.", exception.Message);
            Assert.IsType<JsonException>(exception.InnerException);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenManifestContainsNullSkill()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Skills = [null]
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Equal("Aspire skills bundle manifest contains an empty skill entry.", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenManifestContainsNullFile()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Skills =
                [
                    new SkillBundleSkill
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files = [null]
                    }
                ]
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Equal("Aspire skills bundle skill 'aspire' contains an empty file entry.", exception.Message);
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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("description", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
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

            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Skills =
                [
                    CreateManifestSkill(bundleDirectory, CommonAgentApplicators.AspireSkillName, AspireSkillDescription),
                    CreateManifestSkill(bundleDirectory, "Aspire", AspireSkillDescription)
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("duplicate skill", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillNameIsNotASafeDirectoryName()
    {
        var bundleDirectory = CreateTempDirectory();

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
                        Name = "..",
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                            }
                        ]
                    }
                ]
            };
            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("must contain only ASCII letters", exception.Message);
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
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["references/app-commands.md"] = "# App commands"
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("must contain SKILL.md", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillFileIsExcludedFromInstallation()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() },
                installExcludedRelativePaths: ["SKILL.md"]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("cannot exclude SKILL.md", exception.Message);
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
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Skills =
                [
                    new SkillBundleSkill
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

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
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireifySkillName);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        var skillContent = CreateSkillFileContent(CommonAgentApplicators.AspireifySkillName, AspireifySkillDescription, "# Aspireify");
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
                      "name": "{{CommonAgentApplicators.AspireifySkillName}}",
                      "description": "{{AspireifySkillDescription}}",
                      "files": [
                        { "relativePath": "SKILL.md", "sha256": "{{ComputeSha256(skillPath)}}" }
                      ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var files = await bundle.GetSkillFilesAsync(AspireifySkillDefinition, CancellationToken.None);

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
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Skills =
                [
                    new SkillBundleSkill
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

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

            var bundleProvider = new AspireSkillsBundleProvider("13.4.0", "13.4.0");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(bundleProvider, bundleDirectory));

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

            var bundleProvider = new AspireSkillsBundleProvider("13.4.0-pr.17323.gf2228d9b", "13.4.0");
            var bundle = await LoadBundleAsync(bundleProvider, bundleDirectory);

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
                supports: new SkillBundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundleProvider = new AspireSkillsBundleProvider("13.5.0-pr.17553.gca8e5ace", "13.5.0");
            var bundle = await LoadBundleAsync(bundleProvider, bundleDirectory, skipCompatibilityCheck: true);

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

            var bundleProvider = new AspireSkillsBundleProvider("13.5.0", "13.5.0");
            await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(
                bundleProvider,
                bundleDirectory,
                skipCompatibilityCheck: true));
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    private static Task<AspireSkillsBundle> LoadBundleAsync(
        AspireSkillsBundleProvider bundleProvider,
        string bundleDirectory,
        bool skipCompatibilityCheck = false)
    {
        return bundleProvider.LoadAsync(
            new DirectoryInfo(bundleDirectory),
            TestArchiveSha256,
            CancellationToken.None,
            skipCompatibilityCheck);
    }

    private static async Task CreateBundleAsync(
        string bundleDirectory,
        Dictionary<string, string> files,
        string? hashOverride = null,
        SkillBundleSupports? supports = null,
        IReadOnlyList<string>? installExcludedRelativePaths = null)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(skillDirectory, AspireSkillsBundleProvider.NormalizeRelativePath(relativePath));
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
                    Name = CommonAgentApplicators.AspireSkillName,
                    Description = AspireSkillDescription,
                    InstallExcludedRelativePaths = installExcludedRelativePaths?.ToArray() ?? ["evals"],
                    Files = files
                        .Select(file => new SkillBundleFile
                        {
                            RelativePath = file.Key,
                            Sha256 = hashOverride ?? ComputeSha256(Path.Combine(skillDirectory, AspireSkillsBundleProvider.NormalizeRelativePath(file.Key)))
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

    private static async Task WriteSkillAsync(string bundleDirectory, string skillName, string content)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", skillName);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), content);
    }

    private static SkillBundleSkill CreateManifestSkill(string bundleDirectory, string skillName, string description)
    {
        return new SkillBundleSkill
        {
            Name = skillName,
            Description = description,
            Files =
            [
                new SkillBundleFile
                {
                    RelativePath = "SKILL.md",
                    Sha256 = ComputeSha256(Path.Combine(bundleDirectory, "skills", skillName, "SKILL.md"))
                }
            ]
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
