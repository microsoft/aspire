// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsBundleTests
{
    private const string AspireSkillDescription = "Aspire CLI commands and workflows for distributed apps";
    private const string AspireifySkillDescription = "One-time setup: wire up AppHost with discovered projects";
    private const string TestSha512 = "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    private static readonly AspireSkillsBundleProvider s_bundleProvider = TestBundleProviderFactory.CreateSkills();
    private static readonly AspireSkillsBundleProvider s_extensionBundleProvider = TestBundleProviderFactory.CreateExtensions();

    [Fact]
    public void ExtensionProvider_DefinesExtensionEnvelopeRootAndRequiredFile()
    {
        var descriptor = s_extensionBundleProvider.Descriptor;

        Assert.Equal(AgentAssetKind.Extension, descriptor.AssetKind);
        Assert.Equal("extensions", descriptor.AssetKindName);
        Assert.Equal("aspire-extensions", descriptor.AssetPrefix);
        Assert.Equal("aspire-extensions", descriptor.CacheDirectoryName);
        Assert.Equal("Aspire extensions", descriptor.DisplayName);
        Assert.Equal("extension-manifest.json", descriptor.ManifestFileName);
        Assert.Equal("extensions", descriptor.ManifestAssetsPropertyName);
        Assert.Equal("extensions", descriptor.ContentRootDirectoryName);
        Assert.Equal("extension.mjs", descriptor.RequiredFileName);
        Assert.Equal("aspire-extensions.bundle.tgz", descriptor.EmbeddedArchiveResourceName);
        Assert.Equal("aspire-extensions.metadata.json", descriptor.EmbeddedMetadataResourceName);
    }

    [Fact]
    public void SkillProvider_DefinesSkillEnvelopeRootAndRequiredFile()
    {
        var descriptor = s_bundleProvider.Descriptor;

        Assert.Equal(AgentAssetKind.Skill, descriptor.AssetKind);
        Assert.Equal("skills", descriptor.AssetKindName);
        Assert.Equal("aspire-skills", descriptor.AssetPrefix);
        Assert.Equal("aspire-skills", descriptor.CacheDirectoryName);
        Assert.Equal("Aspire skills", descriptor.DisplayName);
        Assert.Equal("skill-manifest.json", descriptor.ManifestFileName);
        Assert.Equal("skills", descriptor.ManifestAssetsPropertyName);
        Assert.Equal("skills", descriptor.ContentRootDirectoryName);
        Assert.Equal("SKILL.md", descriptor.RequiredFileName);
        Assert.Equal("aspire-skills.bundle.tgz", descriptor.EmbeddedArchiveResourceName);
        Assert.Equal("aspire-skills.metadata.json", descriptor.EmbeddedMetadataResourceName);
    }

    [Fact]
    public async Task LoadAsync_ExtensionProvider_UsesExtensionEnvelopeRootAndSourceKind()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            // The extension's required file (extension.mjs) is plain JS with no SKILL.md-style
            // frontmatter. The extension catalog configures no additional content validation.
            await WriteExtensionBundleAsync(bundleDirectory);

            var bundle = await s_extensionBundleProvider.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                TestContext.Current.CancellationToken);
            var extension = Assert.Single(bundle.Assets);
            var file = Assert.Single(extension.Files);

            Assert.Equal(AgentAssetKind.Extension, bundle.AssetKind);
            Assert.True(extension.IsDefault);
            Assert.Equal("aspire-doctor", extension.Name);
            Assert.Equal("extension.mjs", file.RelativePath);
            Assert.Equal(AgentAssetFileComparison.NormalizedUtf8Text, file.Comparison);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ExtensionProvider_RejectsWrongEnvelope()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, "extension-manifest.json"),
                """
                {
                  "version": "0.0.1",
                  "supports": {
                    "aspireCli": ">=0.0.0 <999.0.0",
                    "aspireSdk": ">=0.0.0 <999.0.0"
                  },
                  "skills": []
                }
                """,
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => s_extensionBundleProvider.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                TestContext.Current.CancellationToken));

            Assert.Contains("must contain at least one", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoadAsync_ExtensionProvider_RejectsMissingContentRootOrRequiredFile(
        bool createContentRoot,
        bool declareRequiredFile)
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteExtensionBundleAsync(bundleDirectory, createContentRoot, declareRequiredFile);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => s_extensionBundleProvider.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                TestContext.Current.CancellationToken));

            Assert.Contains(
                createContentRoot ? "must contain extension.mjs" : "was not found",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    public static IEnumerable<object[]> BundleProviderKinds()
    {
        yield return [false, "skills"];
        yield return [true, "extensions"];
    }

    [Theory]
    [MemberData(nameof(BundleProviderKinds))]
    public void Manifest_MapsProviderPropertyToAssets(bool isExtension, string manifestAssetsPropertyName)
    {
        AspireSkillsBundleProvider provider = isExtension ? s_extensionBundleProvider : s_bundleProvider;
        var json =
            $$"""
            {
              "version": "0.0.1",
              "supports": {
                "aspireCli": ">=0.0.0",
                "aspireSdk": ">=0.0.0"
              },
              "{{manifestAssetsPropertyName}}": [
                {
                  "name": "aspire",
                  "description": "Aspire"
                }
              ]
            }
            """;
        var manifestTypeInfo = provider.CreateManifestTypeInfo();

        Assert.Equal(manifestAssetsPropertyName, provider.Descriptor.ManifestAssetsPropertyName);

        var manifest = JsonSerializer.Deserialize(
            json,
            manifestTypeInfo);

        Assert.NotNull(manifest);
        var asset = Assert.Single(manifest.Assets);
        Assert.NotNull(asset);
        Assert.Equal("aspire", asset.Name);

        var serializedManifest = JsonSerializer.Serialize(manifest, manifestTypeInfo);
        using var document = JsonDocument.Parse(serializedManifest);
        Assert.True(document.RootElement.TryGetProperty(manifestAssetsPropertyName, out var serializedAssets));
        Assert.Equal(JsonValueKind.Array, serializedAssets.ValueKind);
    }

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
            var files = Assert.Single(bundle.Assets).Files;
            Assert.Equal(AspireSkillsInstaller.Version, bundle.Version);
            Assert.Equal(
                ["SKILL.md", Path.Combine("references", "app-commands.md")],
                files.Select(file => file.RelativePath));
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task LoadAsync_SkillsPreserveBomAwareTextDecodingForAllFiles(int codePage)
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var encoding = Encoding.GetEncoding(codePage);
            var skillContent = CreateSkillFileContent(body: "# Caf\u00e9");
            const string scriptContent = "print('caf\u00e9')\n";
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, byte[]>
            {
                ["SKILL.md"] = [.. encoding.GetPreamble(), .. encoding.GetBytes(skillContent)],
                ["scripts/helper.py"] = [.. encoding.GetPreamble(), .. encoding.GetBytes(scriptContent)]
            });

            var skill = Assert.Single((await LoadBundleAsync(s_bundleProvider, bundleDirectory)).Assets);

            Assert.Collection(skill.Files,
                file =>
                {
                    Assert.Equal("SKILL.md", file.RelativePath);
                    Assert.Equal(AgentAssetFileComparison.NormalizedText, file.Comparison);
                    Assert.Equal(Encoding.UTF8.GetBytes(skillContent), file.Bytes.ToArray());
                    Assert.True(file.ContentEquals(Encoding.UTF8.GetBytes(skillContent.ReplaceLineEndings("\r\n"))));
                },
                file =>
                {
                    Assert.Equal(Path.Combine("scripts", "helper.py"), file.RelativePath);
                    Assert.Equal(AgentAssetFileComparison.NormalizedText, file.Comparison);
                    Assert.Equal(Encoding.UTF8.GetBytes(scriptContent), file.Bytes.ToArray());
                    Assert.True(file.ContentEquals(Encoding.UTF8.GetBytes(scriptContent.ReplaceLineEndings("\r\n"))));
                });
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_SkillsPreserveReplacementDecoding()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var skillContent = CreateSkillFileContent();
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, byte[]>
            {
                ["SKILL.md"] = [.. Encoding.UTF8.GetBytes(skillContent), 0xff],
                ["references/data.bin"] = [0x41, 0xff]
            });

            var skill = Assert.Single((await LoadBundleAsync(s_bundleProvider, bundleDirectory)).Assets);

            Assert.Collection(skill.Files,
                file => Assert.Equal(Encoding.UTF8.GetBytes(skillContent + "\uFFFD"), file.Bytes.ToArray()),
                file => Assert.Equal(Encoding.UTF8.GetBytes("A\uFFFD"), file.Bytes.ToArray()));
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
            var skill = Assert.Single(bundle.Assets);
            Directory.Delete(bundleDirectory, recursive: true);

            Assert.Collection(
                skill.Files,
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
    public async Task Assets_ReturnsManifestAssets()
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
            var skill = Assert.Single(bundle.Assets);

            Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
            Assert.Equal(AspireSkillDescription, skill.Description);
            Assert.True(skill.IsDefault);
            Assert.Empty(skill.ApplicableLanguages);
            Assert.Equal(
                ["SKILL.md", Path.Combine("references", "app-commands.md")],
                skill.Files.Select(static file => file.RelativePath));
        }

        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SkillProvider_TreatsBundleAssetAsDefaultCandidate()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteSkillAsync(bundleDirectory, CommonAgentApplicators.AspireSkillName, CreateSkillFileContent());
            var asset = CreateAgentAsset(
                bundleDirectory,
                CommonAgentApplicators.AspireSkillName,
                AspireSkillDescription);
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets = [asset]
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);

            Assert.True(Assert.Single(bundle.Assets).IsDefault);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_PreservesManifestLanguageRestrictions()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteSkillAsync(bundleDirectory, CommonAgentApplicators.AspireSkillName, CreateSkillFileContent());
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        ApplicableLanguages = [KnownLanguageId.CSharp],
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha512 = ComputeSha512(Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName, "SKILL.md"))
                            }
                        ]
                    }
                ]
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var skill = Assert.Single(bundle.Assets);

            Assert.True(skill.IsDefault);
            Assert.Equal([KnownLanguageId.CSharp], skill.ApplicableLanguages);
            Assert.True(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
            Assert.False(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
            Assert.False(skill.IsApplicableToLanguage(null));
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public void Assets_KeepPayloadsSeparateForDifferentKindsWithTheSameName()
    {
        var skillFile = new AgentAssetFile("SKILL.md", CreateSkillFileContent());
        var extensionFile = new AgentAssetFile("extension.mjs", "export default {};");
        var skill = new AgentAssetDefinition(
            "aspire", AspireSkillDescription, [skillFile], installExcludedRelativePaths: [], isDefault: true);
        var extension = new AgentAssetDefinition(
            "aspire", "Aspire extension", [extensionFile], installExcludedRelativePaths: [], isDefault: true);
        var skillBundle = new AspireSkillsBundle(AspireSkillsInstaller.Version, AgentAssetKind.Skill, [skill]);
        var extensionBundle = new AspireSkillsBundle(AspireSkillsInstaller.Version, AgentAssetKind.Extension, [extension]);

        Assert.Same(skillFile, Assert.Single(Assert.Single(skillBundle.Assets).Files));
        Assert.Same(extensionFile, Assert.Single(Assert.Single(extensionBundle.Assets).Files));
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
            }, hashOverride: TestSha512);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("failed SHA-512 verification", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ValidatesLegacySha256PerFileHashes()
    {
        var bundleDirectory = CreateTempDirectory();
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, CreateSkillFileContent());

        try
        {
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha256 = ComputeSha256(skillPath)
                            }
                        ]
                    }
                ]
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var skill = Assert.Single(bundle.Assets);

            Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenNoPerFileHashSpecified()
    {
        var bundleDirectory = CreateTempDirectory();
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), CreateSkillFileContent());

        try
        {
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md"
                            }
                        ]
                    }
                ]
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("SHA-512 or SHA-256", exception.Message, StringComparison.Ordinal);
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
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets = [null]
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Equal("Aspire skills bundle manifest contains an empty asset entry.", exception.Message);
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
            await WriteManifestAsync(bundleDirectory, new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files = [null]
                    }
                ]
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Equal("Aspire skills bundle asset 'aspire' contains an empty file entry.", exception.Message);
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
    public async Task LoadAsync_ThrowsWhenSkillNamesAreDuplicated()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await WriteSkillAsync(bundleDirectory, CommonAgentApplicators.AspireSkillName, CreateSkillFileContent());

            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    CreateAgentAsset(bundleDirectory, CommonAgentApplicators.AspireSkillName, AspireSkillDescription),
                    CreateAgentAsset(bundleDirectory, CommonAgentApplicators.AspireSkillName, AspireSkillDescription)
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("duplicate asset", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillFileDoesNotDeclareFrontmatterName()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = """
                    ---
                    description: "Aspire CLI commands and workflows for distributed apps"
                    ---

                    # Aspire
                    """
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("must define a frontmatter name", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ThrowsWhenSkillFileFrontmatterNameDoesNotMatchManifest()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent(name: CommonAgentApplicators.AspireifySkillName)
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("must match its manifest and directory name", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Aspire")]
    [InlineData("aspire_skill")]
    [InlineData("-aspire")]
    [InlineData("aspire-")]
    [InlineData("aspire--skill")]
    [InlineData("..")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task LoadAsync_ThrowsWhenSkillNameViolatesAgentSkillsSpecification(string skillName)
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = skillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha512 = TestSha512
                            }
                        ]
                    }
                ]
            };
            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("must be 1-64 characters", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("con", false)]
    [InlineData("con", true)]
    [InlineData("aux", false)]
    [InlineData("aux", true)]
    [InlineData("nul", false)]
    [InlineData("nul", true)]
    [InlineData("com1", false)]
    [InlineData("com1", true)]
    [InlineData("lpt9", false)]
    [InlineData("lpt9", true)]
    public async Task LoadAsync_OnlyExtensionNamesRejectWindowsDeviceNames(string assetName, bool isExtension)
    {
        var bundleDirectory = CreateTempDirectory();
        var provider = isExtension ? s_extensionBundleProvider : s_bundleProvider;

        try
        {
            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = assetName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = provider.Descriptor.RequiredFileName,
                                Sha512 = TestSha512
                            }
                        ]
                    }
                ]
            };
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, provider.Descriptor.ManifestFileName),
                JsonSerializer.Serialize(manifest, provider.CreateManifestTypeInfo()),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(provider, bundleDirectory));

            Assert.Equal(
                isExtension
                    ? $"Aspire extensions bundle asset name '{assetName}' is not portable."
                    : $"Aspire skills bundle file 'SKILL.md' in asset '{assetName}' was not found.",
                exception.Message);
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
            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "../SKILL.md",
                                Sha512 = TestSha512
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

    [Theory]
    [InlineData("references/file:stream.md")]
    [InlineData("references/file?.md")]
    [InlineData("references/file\u0001.md")]
    public async Task LoadAsync_ThrowsWhenFilePathIsNotPortable(string relativePath)
    {
        var bundleDirectory = CreateTempDirectory();
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, CreateSkillFileContent());

        try
        {
            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha512 = ComputeSha512(skillPath)
                            },
                            new AspireSkillsBundleFile
                            {
                                RelativePath = relativePath,
                                Sha512 = TestSha512
                            }
                        ]
                    }
                ]
            };

            await WriteManifestAsync(bundleDirectory, manifest);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => LoadBundleAsync(s_bundleProvider, bundleDirectory));

            Assert.Contains("is not safe", exception.Message);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("references/CoN.txt")]
    [InlineData("references/PRN")]
    [InlineData("references/aux.json")]
    [InlineData("references/NUL.tar.gz")]
    [InlineData("references/com1.js")]
    [InlineData("references/LPT9.md")]
    [InlineData("references/file.")]
    [InlineData("references/file ")]
    public async Task SkillPathsRetainPriorAcceptanceWhileExtensionPathsRejectAliases(string relativePath)
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var expectedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(expectedPath, s_bundleProvider.NormalizeRelativePath(relativePath));
            var exception = Assert.Throws<InvalidOperationException>(() => s_extensionBundleProvider.NormalizeRelativePath(relativePath));
            Assert.Equal($"Aspire extensions bundle path '{relativePath}' is not safe.", exception.Message);

            // Exclusions need not exist on disk, including paths only supported on Unix.
            await CreateBundleAsync(
                bundleDirectory,
                new Dictionary<string, string> { ["SKILL.md"] = CreateSkillFileContent() },
                installExcludedRelativePaths: [relativePath]);

            var skill = Assert.Single((await LoadBundleAsync(s_bundleProvider, bundleDirectory)).Assets);
            Assert.Equal("SKILL.md", Assert.Single(skill.Files).RelativePath);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenArchiveEntryPathIsNotPortable()
    {
        var rootDirectory = CreateTempDirectory();

        try
        {
            var archivePath = Path.Combine(rootDirectory, "bundle.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("bundle/references/file:stream.md");
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("# Reference");
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => s_bundleProvider.CreateAsync(
                new FileInfo(archivePath),
                new DirectoryInfo(Path.Combine(rootDirectory, "staged")),
                ComputeSha512(archivePath),
                CancellationToken.None));

            Assert.Contains("is not safe", exception.Message);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_TreatsMissingOptionalPathArraysAsEmpty()
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
                        { "relativePath": "SKILL.md", "sha512": "{{ComputeSha512(skillPath)}}" }
                      ]
                    }
                  ]
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var files = Assert.Single(bundle.Assets).Files;

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
            var manifest = new AspireSkillsBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Assets =
                [
                    new AspireSkillsBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha512 = ComputeSha512(skillPath)
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
                supports: new AspireSkillsBundleSupports { AspireCli = ">=99.0.0 <100.0.0" });

            var bundleProvider = TestBundleProviderFactory.CreateSkills("13.4.0", "13.4.0");
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
                supports: new AspireSkillsBundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundleProvider = TestBundleProviderFactory.CreateSkills("13.4.0-pr.17323.gf2228d9b", "13.4.0");
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
                supports: new AspireSkillsBundleSupports { AspireCli = ">=13.4.0 <13.5.0" });

            var bundleProvider = TestBundleProviderFactory.CreateSkills("13.5.0-pr.17553.gca8e5ace", "13.5.0");
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

            // Truncate the bundled SKILL.md so the SHA-512 in the manifest no longer matches.
            // The compatibility skip must not bypass content verification.
            var skillPath = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, "tampered");

            var bundleProvider = TestBundleProviderFactory.CreateSkills("13.5.0", "13.5.0");
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
            CancellationToken.None,
            skipCompatibilityCheck);
    }

    private static async Task WriteExtensionBundleAsync(
        string bundleDirectory,
        bool createContentRoot = true,
        bool declareRequiredFile = true)
    {
        const string extensionName = "aspire-doctor";
        const string extensionContent = "export default {};";
        const string readmeContent = "# Aspire doctor";
        var extensionDirectory = Path.Combine(bundleDirectory, "extensions", extensionName);
        if (createContentRoot)
        {
            Directory.CreateDirectory(extensionDirectory);
            var relativePath = declareRequiredFile ? "extension.mjs" : "README.md";
            var content = declareRequiredFile ? extensionContent : readmeContent;
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, relativePath),
                content,
                TestContext.Current.CancellationToken);
        }

        var manifest = new AspireSkillsBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = CreateSupports(),
            Assets =
            [
                new AspireSkillsBundleAsset
                {
                    Name = extensionName,
                    Description = "Runs Aspire doctor in a canvas",
                    Files = declareRequiredFile
                        ?
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "extension.mjs",
                                Sha512 = ComputeSha512ForContent(extensionContent)
                            }
                        ]
                        :
                        [
                            new AspireSkillsBundleFile
                            {
                                RelativePath = "README.md",
                                Sha512 = ComputeSha512ForContent(readmeContent)
                            }
                        ]
                }
            ]
        };
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            s_extensionBundleProvider.CreateManifestTypeInfo());
        await File.WriteAllTextAsync(
            Path.Combine(bundleDirectory, "extension-manifest.json"),
            manifestJson,
            TestContext.Current.CancellationToken);
    }

    private static Task CreateBundleAsync(
        string bundleDirectory,
        Dictionary<string, string> files,
        string? hashOverride = null,
        AspireSkillsBundleSupports? supports = null,
        IReadOnlyList<string>? installExcludedRelativePaths = null)
        => CreateBundleAsync(
            bundleDirectory,
            files.ToDictionary(file => file.Key, file => Encoding.UTF8.GetBytes(file.Value)),
            hashOverride,
            supports,
            installExcludedRelativePaths);

    private static async Task CreateBundleAsync(
        string bundleDirectory,
        Dictionary<string, byte[]> files,
        string? hashOverride = null,
        AspireSkillsBundleSupports? supports = null,
        IReadOnlyList<string>? installExcludedRelativePaths = null)
    {
        var skillDirectory = Path.Combine(bundleDirectory, "skills", CommonAgentApplicators.AspireSkillName);
        Directory.CreateDirectory(skillDirectory);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(skillDirectory, s_bundleProvider.NormalizeRelativePath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, content);
        }

        var manifest = new AspireSkillsBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = supports ?? CreateSupports(),
            Assets =
            [
                new AspireSkillsBundleAsset
                {
                    Name = CommonAgentApplicators.AspireSkillName,
                    Description = AspireSkillDescription,
                    InstallExcludedRelativePaths = installExcludedRelativePaths?.ToArray() ?? ["evals"],
                    Files = files
                        .Select(file => new AspireSkillsBundleFile
                        {
                            RelativePath = file.Key,
                            Sha512 = hashOverride ?? ComputeSha512(Path.Combine(skillDirectory, s_bundleProvider.NormalizeRelativePath(file.Key)))
                        })
                        .ToArray()
                }
            ]
        };

        await WriteManifestAsync(bundleDirectory, manifest);
    }

    private static AspireSkillsBundleSupports CreateSupports()
    {
        return new AspireSkillsBundleSupports
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

    private static AspireSkillsBundleAsset CreateAgentAsset(
        string bundleDirectory,
        string assetName,
        string description)
    {
        return new AspireSkillsBundleAsset
        {
            Name = assetName,
            Description = description,
            Files =
            [
                new AspireSkillsBundleFile
                {
                    RelativePath = "SKILL.md",
                    Sha512 = ComputeSha512(Path.Combine(bundleDirectory, "skills", assetName, "SKILL.md"))
                }
            ]
        };
    }

    private static Task WriteManifestAsync(string bundleDirectory, AspireSkillsBundleManifest manifest)
    {
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            s_bundleProvider.CreateManifestTypeInfo());
        return File.WriteAllTextAsync(Path.Combine(bundleDirectory, "skill-manifest.json"), manifestJson);
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha512ForContent(string content)
    {
        return Convert.ToHexString(SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
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
