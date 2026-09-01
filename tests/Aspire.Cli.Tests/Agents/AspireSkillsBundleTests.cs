// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsBundleTests
{
    private const string AspireSkillDescription = "Aspire CLI commands and workflows for distributed apps";
    private const string AspireifySkillDescription = "One-time setup: wire up AppHost with discovered projects";
    private const string TestSha512 = "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    private static readonly SkillBundleProvider s_bundleProvider = new();
    private static readonly ExtensionBundleProvider s_extensionBundleProvider = new();

    [Fact]
    public void ExtensionProvider_DefinesExtensionEnvelopeRootAndRequiredFile()
    {
        var provider = s_extensionBundleProvider;

        Assert.Equal(AgentAssetKind.Extension, provider.AssetKind);
        Assert.Equal("aspire-extensions", provider.AssetPrefix);
        Assert.Equal("aspire-extensions", provider.CacheDirectoryName);
        Assert.Equal("Aspire extensions", provider.DisplayName);
        Assert.Equal("extension-manifest.json", provider.ManifestFileName);
        Assert.Equal("extensions", provider.ManifestAssetsPropertyName);
        Assert.Equal("extensions", provider.ContentRootDirectoryName);
        Assert.Equal("extension.mjs", provider.RequiredFileName);
        Assert.Equal("aspire-extensions.bundle.tgz", provider.EmbeddedArchiveResourceName);
        Assert.Equal("aspire-extensions.metadata.json", provider.EmbeddedMetadataResourceName);
        Assert.Equal(AspireSkillsInstaller.VersionOverrideKey, provider.VersionOverrideKey);
        Assert.Equal(AspireSkillsInstaller.DisablePackageValidationKey, provider.DisablePackageValidationKey);
        Assert.Equal(AspireSkillsInstaller.MaxCacheAgeKey, provider.MaxCacheAgeKey);
    }

    [Fact]
    public void SkillProvider_DefinesSkillEnvelopeRootAndRequiredFile()
    {
        var provider = s_bundleProvider;

        Assert.Equal(AgentAssetKind.Skill, provider.AssetKind);
        Assert.Equal("aspire-skills", provider.AssetPrefix);
        Assert.Equal("aspire-skills", provider.CacheDirectoryName);
        Assert.Equal("Aspire skills", provider.DisplayName);
        Assert.Equal("skill-manifest.json", provider.ManifestFileName);
        Assert.Equal("skills", provider.ManifestAssetsPropertyName);
        Assert.Equal("skills", provider.ContentRootDirectoryName);
        Assert.Equal("SKILL.md", provider.RequiredFileName);
        Assert.Equal("aspire-skills.bundle.tgz", provider.EmbeddedArchiveResourceName);
        Assert.Equal("aspire-skills.metadata.json", provider.EmbeddedMetadataResourceName);
        Assert.Equal(AspireSkillsInstaller.VersionOverrideKey, provider.VersionOverrideKey);
        Assert.Equal(AspireSkillsInstaller.DisablePackageValidationKey, provider.DisablePackageValidationKey);
        Assert.Equal(AspireSkillsInstaller.MaxCacheAgeKey, provider.MaxCacheAgeKey);
    }

    [Fact]
    public async Task LoadAsync_ExtensionProvider_UsesExtensionEnvelopeRootAndSourceKind()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            // The extension's required file (extension.mjs) is plain JS with no SKILL.md-style
            // frontmatter. Unlike SkillBundleProvider, ExtensionBundleProvider performs no
            // additional content validation of the required file.
            await WriteExtensionBundleAsync(bundleDirectory);

            var bundle = await s_extensionBundleProvider.LoadAsync(
                new DirectoryInfo(bundleDirectory),
                TestContext.Current.CancellationToken);
            var extension = Assert.Single(bundle.GetAssetDefinitions());
            var file = Assert.Single(await bundle.GetAssetFilesAsync(extension, TestContext.Current.CancellationToken));

            Assert.Equal(AgentAssetKind.Extension, bundle.AssetKind);
            Assert.Equal(AgentAssetKind.Extension, extension.AssetKind);
            Assert.Equal(AgentFileAssetSourceKind.AspireSkillsBundle, extension.SourceKind);
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

        Assert.Equal(manifestAssetsPropertyName, provider.ManifestAssetsPropertyName);

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
            var asset = Assert.Single(bundle.GetAssetDefinitions());
            var files = await bundle.GetAssetFilesAsync(asset, CancellationToken.None);
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

            var asset = Assert.Single(bundle.GetAssetDefinitions());
            var files = await bundle.GetAssetFilesAsync(asset, CancellationToken.None);

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
    public async Task GetAssetDefinitions_ReturnsManifestAssets()
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
            var skill = Assert.Single(bundle.GetAssetDefinitions());

            Assert.Equal(AgentAssetKind.Skill, skill.AssetKind);
            Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
            Assert.Equal(AspireSkillDescription, skill.Description);
            Assert.True(skill.IsDefault);
            Assert.Equal(AgentFileAssetSourceKind.AspireSkillsBundle, skill.SourceKind);
            Assert.Equal(["evals"], skill.InstallExcludedRelativePaths);
            Assert.Empty(skill.ApplicableLanguages);
            Assert.Empty(skill.Files);
            var files = await bundle.GetAssetFilesAsync(skill, CancellationToken.None);
            Assert.Equal(
                ["SKILL.md", Path.Combine("references", "app-commands.md")],
                files.Select(static file => file.RelativePath));
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
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets = [asset]
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);

            Assert.True(Assert.Single(bundle.GetAssetDefinitions()).IsDefault);
        }
        finally
        {
            Directory.Delete(bundleDirectory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_ThrowsWhenAssetKindDoesNotMatch()
    {
        var asset = AgentFileAssetDefinition.CreateAspireSkillsBundle(
            AgentAssetKind.Extension,
            CommonAgentApplicators.AspireSkillName,
            AspireSkillDescription);

        var exception = Assert.Throws<ArgumentException>(
            () => new AspireSkillsBundle(
                AspireSkillsInstaller.Version,
                AgentAssetKind.Skill,
                [new ValidatedAspireSkillsBundleAsset(asset, [new AgentAssetFile("SKILL.md", CreateSkillFileContent())])]));

        Assert.Equal("assets", exception.ParamName);
    }

    [Fact]
    public async Task GetAssetFilesAsync_ThrowsForDefinitionNotOwnedByBundle()
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            await CreateBundleAsync(bundleDirectory, new Dictionary<string, string>
            {
                ["SKILL.md"] = CreateSkillFileContent()
            });
            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var differentDefinition = AgentFileAssetDefinition.CreateAspireSkillsBundle(
                AgentAssetKind.Skill,
                CommonAgentApplicators.AspireSkillName,
                AspireSkillDescription);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => bundle.GetAssetFilesAsync(differentDefinition, CancellationToken.None));

            Assert.Contains("does not own asset definition", exception.Message, StringComparison.Ordinal);
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
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
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
            });

            var bundle = await LoadBundleAsync(s_bundleProvider, bundleDirectory);
            var skill = Assert.Single(bundle.GetAssetDefinitions());

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
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
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
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
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
            await WriteManifestAsync(bundleDirectory, new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
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

            var manifest = new SkillBundleManifest
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
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = skillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
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
    [InlineData("con")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    public async Task LoadAsync_ThrowsWhenAssetNameIsWindowsDeviceName(string assetName)
    {
        var bundleDirectory = CreateTempDirectory();

        try
        {
            var manifest = new SkillBundleManifest
            {
                Version = AspireSkillsInstaller.Version,
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = assetName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
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

            Assert.Contains("is not portable", exception.Message, StringComparison.Ordinal);
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
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
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
    [InlineData("references/CoN.txt")]
    [InlineData("references/PRN")]
    [InlineData("references/aux.json")]
    [InlineData("references/NUL.tar.gz")]
    [InlineData("references/com1.js")]
    [InlineData("references/LPT9.md")]
    [InlineData("references/file.")]
    [InlineData("references/file ")]
    public async Task LoadAsync_ThrowsWhenFilePathIsNotPortable(string relativePath)
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
                Supports = CreateSupports(),
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "SKILL.md",
                                Sha512 = ComputeSha512(skillPath)
                            },
                            new SkillBundleFile
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
    public async Task GetAssetFilesAsync_TreatsMissingOptionalPathArraysAsEmpty()
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
            var asset = Assert.Single(
                bundle.GetAssetDefinitions(),
                static asset => asset.Name == CommonAgentApplicators.AspireifySkillName);
            var files = await bundle.GetAssetFilesAsync(asset, CancellationToken.None);

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
                Assets =
                [
                    new SkillBundleAsset
                    {
                        Name = CommonAgentApplicators.AspireSkillName,
                        Description = AspireSkillDescription,
                        Files =
                        [
                            new SkillBundleFile
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
                supports: new SkillBundleSupports { AspireCli = ">=99.0.0 <100.0.0" });

            var bundleProvider = new SkillBundleProvider("13.4.0", "13.4.0");
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

            var bundleProvider = new SkillBundleProvider("13.4.0-pr.17323.gf2228d9b", "13.4.0");
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

            var bundleProvider = new SkillBundleProvider("13.5.0-pr.17553.gca8e5ace", "13.5.0");
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

            var bundleProvider = new SkillBundleProvider("13.5.0", "13.5.0");
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

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = CreateSupports(),
            Assets =
            [
                new SkillBundleAsset
                {
                    Name = extensionName,
                    Description = "Runs Aspire doctor in a canvas",
                    Files = declareRequiredFile
                        ?
                        [
                            new SkillBundleFile
                            {
                                RelativePath = "extension.mjs",
                                Sha512 = ComputeSha512ForContent(extensionContent)
                            }
                        ]
                        :
                        [
                            new SkillBundleFile
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
            var fullPath = Path.Combine(skillDirectory, s_bundleProvider.NormalizeRelativePath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = supports ?? CreateSupports(),
            Assets =
            [
                new SkillBundleAsset
                {
                    Name = CommonAgentApplicators.AspireSkillName,
                    Description = AspireSkillDescription,
                    InstallExcludedRelativePaths = installExcludedRelativePaths?.ToArray() ?? ["evals"],
                    Files = files
                        .Select(file => new SkillBundleFile
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

    private static SkillBundleAsset CreateAgentAsset(
        string bundleDirectory,
        string assetName,
        string description)
    {
        return new SkillBundleAsset
        {
            Name = assetName,
            Description = description,
            Files =
            [
                new SkillBundleFile
                {
                    RelativePath = "SKILL.md",
                    Sha512 = ComputeSha512(Path.Combine(bundleDirectory, "skills", assetName, "SKILL.md"))
                }
            ]
        };
    }

    private static Task WriteManifestAsync(string bundleDirectory, SkillBundleManifest manifest)
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
