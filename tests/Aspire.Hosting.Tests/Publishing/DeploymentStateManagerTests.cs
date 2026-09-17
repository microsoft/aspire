// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Pipelines.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class DeploymentStateManagerTests : IDisposable
{
    private readonly DirectoryInfo _aspireHome = Directory.CreateTempSubdirectory("aspire-deployment-state-tests-");

    [Fact]
    public async Task AcquireSectionAsync_ReturnsEmptySection_WhenStateIsNew()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section = await stateManager.AcquireSectionAsync("Parameters");

        Assert.NotNull(section);
        Assert.Equal("Parameters", section.SectionName);
        Assert.Equal(0, section.Version);
        Assert.NotNull(section.Data);
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task SaveSectionAsync_IncrementsVersion_AfterSave()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section1 = await stateManager.AcquireSectionAsync("Parameters");
        {
            section1.Data["key1"] = "value1";
            await stateManager.SaveSectionAsync(section1);
        }

        var section2 = await stateManager.AcquireSectionAsync("Parameters");

        Assert.Equal(1, section2.Version);
        Assert.Equal("value1", section2.Data["key1"]?.GetValue<string>());
    }

    [Fact]
    public async Task SaveSectionAsync_PreservesExplicitNullProperty()
    {
        var sha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sha);
        var section = await stateManager.AcquireSectionAsync("Parameters");
        section.Data["NullValue"] = null;

        await stateManager.SaveSectionAsync(section);

        var restartedStateManager = CreateFileDeploymentStateManager(sha);
        var restartedSection = await restartedStateManager.AcquireSectionAsync("Parameters");
        Assert.True(restartedSection.Data.ContainsKey("NullValue"));
        Assert.Null(restartedSection.Data["NullValue"]);
    }

    [Fact]
    public async Task SaveSectionAsync_ThrowsException_WhenVersionConflictDetected()
    {
        var stateManager = CreateFileDeploymentStateManager();

        // Acquire and save first section
        DeploymentStateSection oldSection;
        var section1 = await stateManager.AcquireSectionAsync("Parameters");
        {
            section1.Data["key1"] = "value1";
            var oldVersion = section1.Version; // Capture version before save
            await stateManager.SaveSectionAsync(section1);
            // Create a copy of the section with the old version to simulate a stale section
            oldSection = new DeploymentStateSection(section1.SectionName, section1.Data, oldVersion);
        }

        // Acquire and save the section again, incrementing version
        var section2 = await stateManager.AcquireSectionAsync("Parameters");
        {
            section2.Data["key2"] = "value2";
            await stateManager.SaveSectionAsync(section2);
        }

        // Try to save the old section - should throw due to version conflict
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await stateManager.SaveSectionAsync(oldSection));

        Assert.Contains("Concurrency conflict detected in section 'Parameters'", exception.Message);
    }

    [Fact]
    public async Task MultipleSections_CanBeModified_Independently()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var parametersSection = await stateManager.AcquireSectionAsync("Parameters");
        var azureSection = await stateManager.AcquireSectionAsync("Azure");
        {
            parametersSection.Data["param1"] = "value1";
            azureSection.Data["resource1"] = "azure-value1";

            await stateManager.SaveSectionAsync(parametersSection);
            await stateManager.SaveSectionAsync(azureSection);
        }

        var parametersCheck = await stateManager.AcquireSectionAsync("Parameters");
        var azureCheck = await stateManager.AcquireSectionAsync("Azure");

        Assert.Equal(1, parametersCheck.Version);
        Assert.Equal(1, azureCheck.Version);
        Assert.Equal("value1", parametersCheck.Data["param1"]?.GetValue<string>());
        Assert.Equal("azure-value1", azureCheck.Data["resource1"]?.GetValue<string>());
    }
    [Fact]
    public async Task ConcurrentSaves_ToDifferentSections_AreSerializedToStorage()
    {
        var sharedSha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sharedSha);
        var tasks = new List<Task>();

        // Concurrently save to different sections
        for (int i = 0; i < 10; i++)
        {
            int sectionIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var section = await stateManager.AcquireSectionAsync($"Section{sectionIndex}");
                section.Data[$"key{sectionIndex}"] = $"value{sectionIndex}";
                await stateManager.SaveSectionAsync(section);
            }));
        }

        await Task.WhenAll(tasks);

        // Verify all sections were saved correctly by loading with a new state manager
        var verifyManager = CreateFileDeploymentStateManager(sharedSha);
        for (int i = 0; i < 10; i++)
        {
            var section = await verifyManager.AcquireSectionAsync($"Section{i}");
            Assert.Equal($"value{i}", section.Data[$"key{i}"]?.GetValue<string>());
        }
    }

    [Fact]
    public async Task AcquireSectionAsync_UsesExclusiveLock_OnFirstLoad()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section1 = await stateManager.AcquireSectionAsync("Parameters");
        {
            section1.Data["key1"] = "value1";
            await stateManager.SaveSectionAsync(section1);
        }

        var section2 = await stateManager.AcquireSectionAsync("Parameters");
        var section3 = await stateManager.AcquireSectionAsync("Azure");

        Assert.NotNull(section2.Data);
        Assert.Equal("value1", section2.Data["key1"]?.GetValue<string>());
        Assert.Equal(1, section2.Version);
        Assert.Equal(0, section3.Version);
    }

    [Fact]
    public async Task DataPersists_AcrossSessions_ButVersionsAreInstanceSpecific()
    {
        var sharedSha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sharedSha);

        var section1 = await stateManager.AcquireSectionAsync("Parameters");
        {
            section1.Data["key1"] = "value1";
            await stateManager.SaveSectionAsync(section1);
        }

        var stateManager2 = CreateFileDeploymentStateManager(sharedSha);
        var section2 = await stateManager2.AcquireSectionAsync("Parameters");
        {
            // Data persists across manager instances
            Assert.Equal("value1", section2.Data["key1"]?.GetValue<string>());

            // But version tracking is per-instance (starts at 0)
            Assert.Equal(0, section2.Version);

            section2.Data["key2"] = "value2";
            await stateManager2.SaveSectionAsync(section2);
        }

        var stateManager3 = CreateFileDeploymentStateManager(sharedSha);
        var section3 = await stateManager3.AcquireSectionAsync("Parameters");

        // Data from both sessions is present
        Assert.Equal("value1", section3.Data["key1"]?.GetValue<string>());
        Assert.Equal("value2", section3.Data["key2"]?.GetValue<string>());

        // Version starts at 0 for this new instance
        Assert.Equal(0, section3.Version);
    }

    [Fact]
    public async Task StateSection_Dispose_ReleasesLock()
    {
        var stateManager = CreateFileDeploymentStateManager();

        _ = await stateManager.AcquireSectionAsync("Parameters");

        var section2 = await stateManager.AcquireSectionAsync("Parameters");

        Assert.NotNull(section2);
    }

    [Fact]
    public async Task BackwardCompatibility_LoadsStateWithoutMetadata()
    {
        var sha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sha);

        var state = new JsonObject
        {
            ["Parameters:param1"] = "value1",
            ["Azure:resource1"] = "azure-value1"
        };

        await stateManager.SaveStateAsync(state);

        var parametersSection = await stateManager.AcquireSectionAsync("Parameters");
        var azureSection = await stateManager.AcquireSectionAsync("Azure");
        var restartedStateManager = CreateFileDeploymentStateManager(sha);
        var restartedParametersSection = await restartedStateManager.AcquireSectionAsync("Parameters");

        Assert.Equal(0, parametersSection.Version);
        Assert.Equal("value1", parametersSection.Data["param1"]?.GetValue<string>());
        Assert.Equal("azure-value1", azureSection.Data["resource1"]?.GetValue<string>());
        Assert.Equal("value1", restartedParametersSection.Data["param1"]?.GetValue<string>());
    }

    [Fact]
    public async Task AcquireSectionAsync_WithNestedPath_ReturnsCorrectSection()
    {
        var stateManager = CreateFileDeploymentStateManager();

        // First save a section at a nested path
        var section = await stateManager.AcquireSectionAsync("TestParent:TestChild:TestGrandchild");
        section.Data["key1"] = "value1";
        await stateManager.SaveSectionAsync(section);

        // Acquire the same nested section
        var retrievedSection = await stateManager.AcquireSectionAsync("TestParent:TestChild:TestGrandchild");

        Assert.Equal("TestParent:TestChild:TestGrandchild", retrievedSection.SectionName);
        Assert.Equal("value1", retrievedSection.Data["key1"]?.GetValue<string>());
    }

    [Fact]
    public async Task SaveSectionAsync_WithNestedPath_CreatesIntermediateObjects()
    {
        var sharedSha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sharedSha);

        var section = await stateManager.AcquireSectionAsync("Parent:Child:Grandchild");
        section.Data["nestedKey"] = "nestedValue";
        await stateManager.SaveSectionAsync(section);

        // Verify with a new state manager to ensure persistence
        var verifyManager = CreateFileDeploymentStateManager(sharedSha);
        var verifySection = await verifyManager.AcquireSectionAsync("Parent:Child:Grandchild");

        Assert.Equal("nestedValue", verifySection.Data["nestedKey"]?.GetValue<string>());
    }

    [Fact]
    public async Task NestedSections_CanBeModified_Independently()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section1 = await stateManager.AcquireSectionAsync("Root:Branch1:Leaf");
        var section2 = await stateManager.AcquireSectionAsync("Root:Branch2:Leaf");

        section1.Data["key1"] = "value1";
        section2.Data["key2"] = "value2";

        await stateManager.SaveSectionAsync(section1);
        await stateManager.SaveSectionAsync(section2);

        var verify1 = await stateManager.AcquireSectionAsync("Root:Branch1:Leaf");
        var verify2 = await stateManager.AcquireSectionAsync("Root:Branch2:Leaf");

        Assert.Equal("value1", verify1.Data["key1"]?.GetValue<string>());
        Assert.Equal("value2", verify2.Data["key2"]?.GetValue<string>());
    }

    [Fact]
    public async Task NestedSections_CanBeModified_UsingSetValue()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section = await stateManager.AcquireSectionAsync("Root:Branch1:Leaf");

        section.SetValue("value1");

        await stateManager.SaveSectionAsync(section);

        var verify = await stateManager.AcquireSectionAsync("Root:Branch1:Leaf");

        Assert.Equal("value1", verify.Data[""]?.GetValue<string>());
    }

    [Fact]
    public async Task NestedSection_VersionConflict_ThrowsException()
    {
        var stateManager = CreateFileDeploymentStateManager();

        // Acquire and save first section
        var section1 = await stateManager.AcquireSectionAsync("Parent:Child:Grandchild");
        section1.Data["key1"] = "value1";
        var oldVersion = section1.Version;
        await stateManager.SaveSectionAsync(section1);

        // Create a stale section reference
        var oldSection = new DeploymentStateSection(section1.SectionName, section1.Data, oldVersion);

        // Acquire and save again to increment version
        var section2 = await stateManager.AcquireSectionAsync("Parent:Child:Grandchild");
        section2.Data["key2"] = "value2";
        await stateManager.SaveSectionAsync(section2);

        // Try to save the old section - should throw due to version conflict
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await stateManager.SaveSectionAsync(oldSection));

        Assert.Contains("Concurrency conflict detected in section 'Parent:Child:Grandchild'", exception.Message);
    }

    [Fact]
    public async Task AcquireSectionAsync_WithNonexistentNestedPath_ReturnsEmptySection()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var section = await stateManager.AcquireSectionAsync("Nonexistent:Path:Here");

        Assert.NotNull(section);
        Assert.Equal("Nonexistent:Path:Here", section.SectionName);
        Assert.Equal(0, section.Version);
        Assert.NotNull(section.Data);
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task MixedTopLevelAndNestedSections_WorkCorrectly()
    {
        var stateManager = CreateFileDeploymentStateManager();

        var topLevel = await stateManager.AcquireSectionAsync("TopLevel");
        var nested = await stateManager.AcquireSectionAsync("Parent:Child");

        topLevel.Data["topKey"] = "topValue";
        nested.Data["nestedKey"] = "nestedValue";

        await stateManager.SaveSectionAsync(topLevel);
        await stateManager.SaveSectionAsync(nested);

        var verifyTop = await stateManager.AcquireSectionAsync("TopLevel");
        var verifyNested = await stateManager.AcquireSectionAsync("Parent:Child");

        Assert.Equal("topValue", verifyTop.Data["topKey"]?.GetValue<string>());
        Assert.Equal("nestedValue", verifyNested.Data["nestedKey"]?.GetValue<string>());
    }

    [Fact]
    public async Task SaveStateAsync_CreatesDirectory_WithUserOnlyPermissions()
    {
        var sharedSha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sharedSha);

        var section = await stateManager.AcquireSectionAsync("PermTest");
        section.Data["key"] = "value";
        await stateManager.SaveSectionAsync(section);

        // Get the state file path and its directory
        var stateFilePath = stateManager.StateFilePath;
        Assert.NotNull(stateFilePath);

        var stateDirectory = Path.GetDirectoryName(stateFilePath);
        Assert.NotNull(stateDirectory);
        Assert.True(Directory.Exists(stateDirectory));

        // Verify permissions on the directory (should be 0700 - user only)
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(stateDirectory);
            var expectedMode = UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead;
            Assert.Equal(expectedMode, mode);
        }
    }

    [Fact]
    public async Task LoadStateAsync_CreatesDirectory_WithUserOnlyPermissions()
    {
        var statePath = Path.Combine(
            _aspireHome.FullName,
            "deployments",
            Guid.NewGuid().ToString("N"),
            "development.json");

        await FileDeploymentStateManager.LoadEffectiveStateAsync(statePath, legacyStatePath: null);

        var stateDirectory = Path.GetDirectoryName(statePath);
        Assert.NotNull(stateDirectory);
        Assert.True(Directory.Exists(stateDirectory));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(stateDirectory);
            var expectedMode = UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead;
            Assert.Equal(expectedMode, mode);
        }
    }

    [Fact]
    public void GetStatePath_UsesConfiguredAspireHome()
    {
        var sha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sha);

        Assert.Equal(
            Path.Combine(_aspireHome.FullName, "deployments", sha, "development.json"),
            stateManager.StateFilePath);
    }

    [Fact]
    public void GetStatePath_UsesDefaultAspireHome_WhenAspireHomeIsNotConfigured()
    {
        var sha = Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:PathSha256"] = sha
            })
            .Build();
        var hostEnvironment = new TestHostEnvironment { EnvironmentName = "Development" };
        var pipelineOptions = Options.Create(new Hosting.Pipelines.PipelineOptions());
        var stateManager = new FileDeploymentStateManager(
            NullLogger<FileDeploymentStateManager>.Instance,
            configuration,
            hostEnvironment,
            pipelineOptions);

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aspire",
                "deployments",
                sha,
                "development.json"),
            stateManager.StateFilePath);
    }

    [Fact]
    public async Task SourceAppHostStateMigratesFromLegacyPathOnSave()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        try
        {
            var legacySection = await legacyStateManager.AcquireSectionAsync("Migration");
            legacySection.Data["legacy"] = true;
            await legacyStateManager.SaveSectionAsync(legacySection);
            var legacyPath = legacyStateManager.StateFilePath;

            var migratedSection = await migratingStateManager.AcquireSectionAsync("Migration");
            Assert.True(migratedSection.Data["legacy"]?.GetValue<bool>());

            migratedSection.Data["current"] = true;
            await migratingStateManager.SaveSectionAsync(migratedSection);

            var currentPath = migratingStateManager.StateFilePath;
            Assert.NotEqual(legacyPath, currentPath);
            Assert.True(File.Exists(currentPath));
            Assert.True(File.Exists(legacyPath));

            var currentStateManager = CreateFileDeploymentStateManager(currentSha);
            var currentSection = await currentStateManager.AcquireSectionAsync("Migration");
            Assert.True(currentSection.Data["legacy"]?.GetValue<bool>());
            Assert.True(currentSection.Data["current"]?.GetValue<bool>());
        }
        finally
        {
            await migratingStateManager.ClearAllStateAsync();
            await legacyStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task FullStateSaveReplacesLegacyMigrationMetadata()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        try
        {
            var legacySection = await legacyStateManager.AcquireSectionAsync("Legacy");
            legacySection.Data["Value"] = "legacy";
            await legacyStateManager.SaveSectionAsync(legacySection);

            var migratedSection = await migratingStateManager.AcquireSectionAsync("Legacy");
            migratedSection.Data["Claimed"] = true;
            await migratingStateManager.SaveSectionAsync(migratedSection);

            await migratingStateManager.SaveStateAsync(new JsonObject
            {
                ["Replacement"] = new JsonObject
                {
                    ["Value"] = "current"
                }
            });

            var sameManagerLegacySection = await migratingStateManager.AcquireSectionAsync("Legacy");
            sameManagerLegacySection.Data["Value"] = "legacy";
            await migratingStateManager.SaveSectionAsync(sameManagerLegacySection);

            var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
            var legacyAfterRestart = await restartedStateManager.AcquireSectionAsync("Legacy");
            var replacementAfterRestart = await restartedStateManager.AcquireSectionAsync("Replacement");

            Assert.Equal("legacy", legacyAfterRestart.Data["Value"]?.GetValue<string>());
            Assert.Equal("current", replacementAfterRestart.Data["Value"]?.GetValue<string>());
        }
        finally
        {
            await migratingStateManager.ClearAllStateAsync();
            await legacyStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task MigrationMetadataIsAuthoritativeWhenCanonicalWriteIsStale()
    {
        var sha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sha);
        var section = await stateManager.AcquireSectionAsync("Parameters");
        section.Data["Value"] = "committed";
        await stateManager.SaveSectionAsync(section);

        File.WriteAllText(stateManager.StateFilePath!, """{"Parameters:Value":"stale"}""");

        var restartedStateManager = CreateFileDeploymentStateManager(sha);
        var restartedSection = await restartedStateManager.AcquireSectionAsync("Parameters");
        Assert.Equal("committed", restartedSection.Data["Value"]?.GetValue<string>());
    }

    [Fact]
    public async Task MigrationTombstoneIsAuthoritativeWhenCanonicalWriteIsStale()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var legacySection = await legacyStateManager.AcquireSectionAsync("Parameters");
        legacySection.Data["Value"] = "legacy";
        await legacyStateManager.SaveSectionAsync(legacySection);

        var stateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var section = await stateManager.AcquireSectionAsync("Parameters");
        await stateManager.DeleteSectionAsync(section);

        File.WriteAllText(stateManager.StateFilePath!, """{"Parameters:Value":"stale"}""");

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        Assert.Empty((await restartedStateManager.AcquireSectionAsync("Parameters")).Data);
    }

    [Fact]
    public async Task SourceAppHostMigrationPersistsScalarValueDeletion()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);

        try
        {
            var legacyStatePath = legacyStateManager.StateFilePath!;
            Directory.CreateDirectory(Path.GetDirectoryName(legacyStatePath)!);
            await File.WriteAllTextAsync(legacyStatePath, """{"Parameters:secret":"legacy"}""");

            var parameterSection = await migratingStateManager.AcquireSectionAsync("Parameters:secret");
            Assert.Equal("legacy", parameterSection.Data[""]?.GetValue<string>());

            await migratingStateManager.DeleteSectionAsync(parameterSection);

            var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
            Assert.Empty((await restartedStateManager.AcquireSectionAsync("Parameters:secret")).Data);
        }
        finally
        {
            await migratingStateManager.ClearAllStateAsync();
            await legacyStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task FirstScalarSavePreservesCanonicalValueWithoutMigrationMetadata()
    {
        var sha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(sha);

        try
        {
            var statePath = stateManager.StateFilePath!;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, """{"Parameters:secret":"existing"}""");

            var section = await stateManager.AcquireSectionAsync("Parameters:secret");
            Assert.Equal("existing", section.Data[""]?.GetValue<string>());

            await stateManager.SaveSectionAsync(section);

            var restartedStateManager = CreateFileDeploymentStateManager(sha);
            var restartedSection = await restartedStateManager.AcquireSectionAsync("Parameters:secret");
            Assert.Equal("existing", restartedSection.Data[""]?.GetValue<string>());
        }
        finally
        {
            await stateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task UnchangedScalarSavePreservesConcurrentUpdate()
    {
        var sha = Guid.NewGuid().ToString("N");
        var initialStateManager = CreateFileDeploymentStateManager(sha);

        try
        {
            var initialSection = await initialStateManager.AcquireSectionAsync("Parameters:secret");
            initialSection.SetValue("initial");
            await initialStateManager.SaveSectionAsync(initialSection);

            var staleStateManager = CreateFileDeploymentStateManager(sha);
            var updatingStateManager = CreateFileDeploymentStateManager(sha);
            var staleSection = await staleStateManager.AcquireSectionAsync("Parameters:secret");
            var updatingSection = await updatingStateManager.AcquireSectionAsync("Parameters:secret");

            updatingSection.SetValue("updated");
            await updatingStateManager.SaveSectionAsync(updatingSection);
            await staleStateManager.SaveSectionAsync(staleSection);

            var restartedStateManager = CreateFileDeploymentStateManager(sha);
            var restartedSection = await restartedStateManager.AcquireSectionAsync("Parameters:secret");
            Assert.Equal("updated", restartedSection.Data[""]?.GetValue<string>());
        }
        finally
        {
            await initialStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task ConcurrentScalarAndObjectChangesDoNotCreateHybridState()
    {
        var sha = Guid.NewGuid().ToString("N");
        var initialStateManager = CreateFileDeploymentStateManager(sha);

        try
        {
            var initialSection = await initialStateManager.AcquireSectionAsync("Settings");
            initialSection.Data["Leaf"] = "initial";
            await initialStateManager.SaveSectionAsync(initialSection);

            var staleObjectManager = CreateFileDeploymentStateManager(sha);
            var scalarManager = CreateFileDeploymentStateManager(sha);
            var staleObjectSection = await staleObjectManager.AcquireSectionAsync("Settings");
            var scalarSection = await scalarManager.AcquireSectionAsync("Settings");
            staleObjectSection.Data["Leaf"] = "updated";
            scalarSection.SetValue("scalar");

            await scalarManager.SaveSectionAsync(scalarSection);
            await staleObjectManager.SaveSectionAsync(staleObjectSection);

            var restartedStateManager = CreateFileDeploymentStateManager(sha);
            var restartedSection = await restartedStateManager.AcquireSectionAsync("Settings");
            Assert.Equal("updated", restartedSection.Data["Leaf"]?.GetValue<string>());
            Assert.False(restartedSection.Data.ContainsKey(string.Empty));
        }
        finally
        {
            await initialStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task ConcurrentNestedAdditionsToNewObjectAreMerged()
    {
        var sha = Guid.NewGuid().ToString("N");
        var firstStateManager = CreateFileDeploymentStateManager(sha);

        try
        {
            var secondStateManager = CreateFileDeploymentStateManager(sha);
            var firstSection = await firstStateManager.AcquireSectionAsync("Settings");
            var secondSection = await secondStateManager.AcquireSectionAsync("Settings");
            firstSection.Data["Features"] = new JsonObject { ["A"] = true };
            secondSection.Data["Features"] = new JsonObject { ["B"] = true };

            await firstStateManager.SaveSectionAsync(firstSection);
            await secondStateManager.SaveSectionAsync(secondSection);

            var restartedStateManager = CreateFileDeploymentStateManager(sha);
            var restartedSection = await restartedStateManager.AcquireSectionAsync("Settings");
            var features = Assert.IsType<JsonObject>(restartedSection.Data["Features"]);
            Assert.Equal(["A", "B"], features.Select(static pair => pair.Key));
            Assert.True(features["A"]?.GetValue<bool>());
            Assert.True(features["B"]?.GetValue<bool>());
        }
        finally
        {
            await firstStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task SourceAppHostMigrationPersistsOnlyUpdatedSections()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var firstSha = Guid.NewGuid().ToString("N");
        var secondSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var firstLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        firstLegacySection.Data["SandboxId"] = "first-sandbox";
        await legacyStateManager.SaveSectionAsync(firstLegacySection);
        var secondLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        secondLegacySection.Data["SandboxId"] = "second-sandbox";
        await legacyStateManager.SaveSectionAsync(secondLegacySection);

        var firstStateManager = CreateFileDeploymentStateManager(firstSha, legacySha);
        var currentParentSection = await firstStateManager.AcquireCurrentSectionAsync("Azure:Sandboxes");
        Assert.Empty(currentParentSection.Data);

        var firstSection = await firstStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        firstSection.Data["Migrated"] = true;
        await firstStateManager.SaveSectionAsync(firstSection);

        var firstCanonicalStateManager = CreateFileDeploymentStateManager(firstSha);
        var migratedFirstSection = await firstCanonicalStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var inheritedSecondSection = await firstCanonicalStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        Assert.Equal("first-sandbox", migratedFirstSection.Data["SandboxId"]?.GetValue<string>());
        Assert.True(migratedFirstSection.Data["Migrated"]?.GetValue<bool>());
        Assert.Empty(inheritedSecondSection.Data);

        var migratedParentSection = await firstStateManager.AcquireSectionAsync("Azure:Sandboxes");
        Assert.Equal(["first", "second"], migratedParentSection.Data.Select(static pair => pair.Key));
        var migratedCurrentParentSection = await firstStateManager.AcquireCurrentSectionAsync("Azure:Sandboxes");
        Assert.Equal(["first"], migratedCurrentParentSection.Data.Select(static pair => pair.Key));

        var restartedFirstStateManager = CreateFileDeploymentStateManager(firstSha, legacySha);
        var restartedFirstSection = await restartedFirstStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var restartedSecondSection = await restartedFirstStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        var restartedCurrentParentSection = await restartedFirstStateManager.AcquireCurrentSectionAsync("Azure:Sandboxes");
        Assert.True(restartedFirstSection.Data["Migrated"]?.GetValue<bool>());
        Assert.Equal("second-sandbox", restartedSecondSection.Data["SandboxId"]?.GetValue<string>());
        Assert.Equal(["first"], restartedCurrentParentSection.Data.Select(static pair => pair.Key));

        var secondStateManager = CreateFileDeploymentStateManager(secondSha, legacySha);
        var secondSection = await secondStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        Assert.Equal("second-sandbox", secondSection.Data["SandboxId"]?.GetValue<string>());

        var firstCanonicalPath = firstStateManager.StateFilePath;
        var legacyPath = legacyStateManager.StateFilePath;
        await firstStateManager.ClearAllStateAsync();
        Assert.False(File.Exists(firstCanonicalPath));
        Assert.True(File.Exists(legacyPath));
        var legacyAfterClearManager = CreateFileDeploymentStateManager(legacySha);
        var clearedFirstLegacySection = await legacyAfterClearManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var preservedSecondLegacySection = await legacyAfterClearManager.AcquireSectionAsync("Azure:Sandboxes:second");
        Assert.Equal("first-sandbox", clearedFirstLegacySection.Data["SandboxId"]?.GetValue<string>());
        Assert.Equal("second-sandbox", preservedSecondLegacySection.Data["SandboxId"]?.GetValue<string>());
    }

    [Fact]
    public async Task SourceAppHostParentSaveDoesNotAdoptUnchangedLegacyDescendants()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var legacyAzureSection = await legacyStateManager.AcquireSectionAsync("Azure");
        legacyAzureSection.Data["SubscriptionId"] = "legacy-subscription";
        legacyAzureSection.Data["Sandboxes"] = new JsonObject
        {
            ["first"] = new JsonObject { ["SandboxId"] = "first-sandbox" },
            ["second"] = new JsonObject { ["SandboxId"] = "second-sandbox" }
        };
        await legacyStateManager.SaveSectionAsync(legacyAzureSection);

        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var azureSection = await migratingStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "current-subscription";
        await migratingStateManager.SaveSectionAsync(azureSection);

        var currentAzureSection = await migratingStateManager.AcquireCurrentSectionAsync("Azure");
        Assert.Equal("current-subscription", currentAzureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Null(currentAzureSection.Data["Sandboxes"]);

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var restartedAzureSection = await restartedStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("current-subscription", restartedAzureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("first-sandbox", restartedAzureSection.Data["Sandboxes"]?["first"]?["SandboxId"]?.GetValue<string>());
        Assert.Equal("second-sandbox", restartedAzureSection.Data["Sandboxes"]?["second"]?["SandboxId"]?.GetValue<string>());
        Assert.Null((await restartedStateManager.AcquireCurrentSectionAsync("Azure")).Data["Sandboxes"]);
    }

    [Fact]
    public async Task RecreatedClaimedParentDoesNotRestoreLegacyChildren()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);

        try
        {
            var legacyAzureSection = await legacyStateManager.AcquireSectionAsync("Azure");
            legacyAzureSection.Data["SubscriptionId"] = "legacy-subscription";
            legacyAzureSection.Data["Sandboxes"] = new JsonObject
            {
                ["legacy"] = new JsonObject { ["SandboxId"] = "legacy-sandbox" }
            };
            await legacyStateManager.SaveSectionAsync(legacyAzureSection);

            var azureSection = await migratingStateManager.AcquireSectionAsync("Azure");
            await migratingStateManager.DeleteSectionAsync(azureSection);

            var newSandboxSection = await migratingStateManager.AcquireSectionAsync("Azure:Sandboxes:new");
            newSandboxSection.Data["SandboxId"] = "new-sandbox";
            await migratingStateManager.SaveSectionAsync(newSandboxSection);

            var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
            var restartedAzureSection = await restartedStateManager.AcquireSectionAsync("Azure");

            Assert.Null(restartedAzureSection.Data["SubscriptionId"]);
            Assert.Null(restartedAzureSection.Data["Sandboxes"]?["legacy"]);
            Assert.Equal(
                "new-sandbox",
                restartedAzureSection.Data["Sandboxes"]?["new"]?["SandboxId"]?.GetValue<string>());
        }
        finally
        {
            await migratingStateManager.ClearAllStateAsync();
            await legacyStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task RecreatedClaimedDescendantDoesNotRestoreLegacyChildrenInParentRead()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);

        try
        {
            var legacySandboxesSection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes");
            legacySandboxesSection.Data["first"] = new JsonObject
            {
                ["SandboxId"] = "legacy-first",
                ["LegacyOnly"] = true
            };
            legacySandboxesSection.Data["second"] = new JsonObject
            {
                ["SandboxId"] = "legacy-second"
            };
            await legacyStateManager.SaveSectionAsync(legacySandboxesSection);

            var firstSandboxSection = await migratingStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
            await migratingStateManager.DeleteSectionAsync(firstSandboxSection);

            var newChildSection = await migratingStateManager.AcquireSectionAsync("Azure:Sandboxes:first:new");
            newChildSection.Data["SandboxId"] = "new-sandbox";
            await migratingStateManager.SaveSectionAsync(newChildSection);

            var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
            var restartedSandboxesSection = await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes");

            Assert.Null(restartedSandboxesSection.Data["first"]?["SandboxId"]);
            Assert.Null(restartedSandboxesSection.Data["first"]?["LegacyOnly"]);
            Assert.Equal(
                "new-sandbox",
                restartedSandboxesSection.Data["first"]?["new"]?["SandboxId"]?.GetValue<string>());
            Assert.Equal(
                "legacy-second",
                restartedSandboxesSection.Data["second"]?["SandboxId"]?.GetValue<string>());
        }
        finally
        {
            await migratingStateManager.ClearAllStateAsync();
            await legacyStateManager.ClearAllStateAsync();
        }
    }

    [Fact]
    public async Task ConcurrentSourceAppHostSavesPreserveCanonicalStateAndMigrationMetadata()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var firstLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        firstLegacySection.Data["SandboxId"] = "first-sandbox";
        await legacyStateManager.SaveSectionAsync(firstLegacySection);
        var secondLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        secondLegacySection.Data["SandboxId"] = "second-sandbox";
        await legacyStateManager.SaveSectionAsync(secondLegacySection);

        var firstStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var secondStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var firstSection = await firstStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var secondSection = await secondStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        firstSection.Data["Updated"] = true;
        secondSection.Data["Updated"] = true;

        await Task.WhenAll(
            firstStateManager.SaveSectionAsync(firstSection),
            secondStateManager.SaveSectionAsync(secondSection));

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var restartedFirstSection = await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var restartedSecondSection = await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        Assert.Equal("first-sandbox", restartedFirstSection.Data["SandboxId"]?.GetValue<string>());
        Assert.True(restartedFirstSection.Data["Updated"]?.GetValue<bool>());
        Assert.Equal("second-sandbox", restartedSecondSection.Data["SandboxId"]?.GetValue<string>());
        Assert.True(restartedSecondSection.Data["Updated"]?.GetValue<bool>());

        var currentParentSection = await restartedStateManager.AcquireCurrentSectionAsync("Azure:Sandboxes");
        Assert.Equal(["first", "second"], currentParentSection.Data.Select(static pair => pair.Key));
    }

    [Fact]
    public async Task ConcurrentSourceAppHostParentSavesPreserveIndependentChanges()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var legacyAzureSection = await legacyStateManager.AcquireSectionAsync("Azure");
        legacyAzureSection.Data["SubscriptionId"] = "legacy-subscription";
        legacyAzureSection.Data["Location"] = "legacy-location";
        await legacyStateManager.SaveSectionAsync(legacyAzureSection);

        var firstStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var secondStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var firstAzureSection = await firstStateManager.AcquireSectionAsync("Azure");
        var secondAzureSection = await secondStateManager.AcquireSectionAsync("Azure");
        firstAzureSection.Data["SubscriptionId"] = "current-subscription";
        secondAzureSection.Data["Location"] = "current-location";

        await Task.WhenAll(
            firstStateManager.SaveSectionAsync(firstAzureSection),
            secondStateManager.SaveSectionAsync(secondAzureSection));

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var restartedAzureSection = await restartedStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("current-subscription", restartedAzureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-location", restartedAzureSection.Data["Location"]?.GetValue<string>());

        var currentAzureSection = await restartedStateManager.AcquireCurrentSectionAsync("Azure");
        Assert.Equal("current-subscription", currentAzureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-location", currentAzureSection.Data["Location"]?.GetValue<string>());
    }

    [Fact]
    public async Task SourceAppHostClearAllStateDeletesLegacyOnlyState()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var legacySection = await legacyStateManager.AcquireSectionAsync("Azure");
        legacySection.Data["SubscriptionId"] = "sub";
        await legacyStateManager.SaveSectionAsync(legacySection);
        var legacyPath = legacyStateManager.StateFilePath;

        var currentStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        Assert.Equal(legacyPath, currentStateManager.StateFilePath);

        await currentStateManager.ClearAllStateAsync();

        Assert.True(File.Exists(legacyPath));
        Assert.False(File.Exists(Path.Combine(
            _aspireHome.FullName,
            "deployments",
            currentSha,
            "development.json")));
        var restartedCurrentStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var suppressedLegacySection = await restartedCurrentStateManager.AcquireSectionAsync("Azure");
        Assert.Empty(suppressedLegacySection.Data);
        var preservedLegacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var preservedLegacySection = await preservedLegacyStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("sub", preservedLegacySection.Data["SubscriptionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task MalformedLegacyStateDoesNotHideCanonicalState()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var currentStateManager = CreateFileDeploymentStateManager(currentSha);
        var currentSection = await currentStateManager.AcquireSectionAsync("Azure");
        currentSection.Data["SubscriptionId"] = "current-sub";
        await currentStateManager.SaveSectionAsync(currentSection);

        var legacyStatePath = Path.Combine(
            _aspireHome.FullName,
            "deployments",
            legacySha,
            "development.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyStatePath)!);
        await File.WriteAllTextAsync(legacyStatePath, "{ malformed");

        var asyncEffectiveState = await FileDeploymentStateManager.LoadEffectiveStateAsync(
            currentStateManager.StateFilePath!,
            legacyStatePath);
        var syncEffectiveState = FileDeploymentStateManager.LoadEffectiveState(
            currentStateManager.StateFilePath!,
            legacyStatePath);
        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var migratedSection = await migratingStateManager.AcquireSectionAsync("Azure");

        Assert.Equal("current-sub", asyncEffectiveState["Azure"]?["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-sub", syncEffectiveState["Azure"]?["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-sub", migratedSection.Data["SubscriptionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task MalformedCanonicalStateDoesNotHideAuthoritativeMigrationState()
    {
        var currentSha = Guid.NewGuid().ToString("N");
        var currentStateManager = CreateFileDeploymentStateManager(currentSha);
        var currentSection = await currentStateManager.AcquireSectionAsync("Azure");
        currentSection.Data["SubscriptionId"] = "current-sub";
        await currentStateManager.SaveSectionAsync(currentSection);
        await File.WriteAllTextAsync(currentStateManager.StateFilePath!, "{ malformed");

        var asyncEffectiveState = await FileDeploymentStateManager.LoadEffectiveStateAsync(
            currentStateManager.StateFilePath!,
            legacyStatePath: null);
        var syncEffectiveState = FileDeploymentStateManager.LoadEffectiveState(
            currentStateManager.StateFilePath!,
            legacyStatePath: null);
        var restartedStateManager = CreateFileDeploymentStateManager(currentSha);
        var restartedSection = await restartedStateManager.AcquireSectionAsync("Azure");

        Assert.Equal("current-sub", asyncEffectiveState["Azure"]?["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-sub", syncEffectiveState["Azure"]?["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("current-sub", restartedSection.Data["SubscriptionId"]?.GetValue<string>());

        restartedSection.Data["Location"] = "westus";
        await restartedStateManager.SaveSectionAsync(restartedSection);
        var savedSection = await CreateFileDeploymentStateManager(currentSha).AcquireSectionAsync("Azure");

        Assert.Equal("current-sub", savedSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus", savedSection.Data["Location"]?.GetValue<string>());
    }

    [Fact]
    public async Task CanceledClearPreservesCurrentState()
    {
        var stateManager = CreateFileDeploymentStateManager();
        var section = await stateManager.AcquireSectionAsync("Azure");
        section.Data["SubscriptionId"] = "sub";
        await stateManager.SaveSectionAsync(section);

        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stateManager.ClearAllStateAsync(cancellationSource.Token));

        var currentSection = await stateManager.AcquireCurrentSectionAsync("Azure");
        Assert.Equal("sub", currentSection.Data["SubscriptionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeletedMigratedSectionDoesNotReappearFromLegacyState()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var legacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:frontend");
        legacySection.Data["SandboxId"] = "sandbox";
        await legacyStateManager.SaveSectionAsync(legacySection);

        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var migratedSection = await migratingStateManager.AcquireSectionAsync("Azure:Sandboxes:frontend");
        await migratingStateManager.SaveSectionAsync(migratedSection);
        await migratingStateManager.DeleteSectionAsync(migratedSection);

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var restartedSection = await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes:frontend");

        Assert.Empty(restartedSection.Data);
    }

    [Fact]
    public async Task ParentSaveDoesNotRestoreDeletedMigratedDescendant()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var currentSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var firstLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        firstLegacySection.Data["SandboxId"] = "first-sandbox";
        await legacyStateManager.SaveSectionAsync(firstLegacySection);
        var secondLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        secondLegacySection.Data["SandboxId"] = "second-sandbox";
        await legacyStateManager.SaveSectionAsync(secondLegacySection);

        var migratingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var firstSection = await migratingStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        await migratingStateManager.DeleteSectionAsync(firstSection);

        var parentSavingStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        var azureSection = await parentSavingStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "current-subscription";
        await parentSavingStateManager.SaveSectionAsync(azureSection);

        var restartedStateManager = CreateFileDeploymentStateManager(currentSha, legacySha);
        Assert.Empty((await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes:first")).Data);
        Assert.Equal(
            "second-sandbox",
            (await restartedStateManager.AcquireSectionAsync("Azure:Sandboxes:second"))
                .Data["SandboxId"]?.GetValue<string>());
        Assert.Equal(
            "current-subscription",
            (await restartedStateManager.AcquireSectionAsync("Azure"))
                .Data["SubscriptionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentMigratedSectionDeletesDoNotMutateSharedLegacyState()
    {
        var legacySha = Guid.NewGuid().ToString("N");
        var firstSha = Guid.NewGuid().ToString("N");
        var secondSha = Guid.NewGuid().ToString("N");
        var legacyStateManager = CreateFileDeploymentStateManager(legacySha);
        var firstLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        firstLegacySection.Data["SandboxId"] = "first";
        await legacyStateManager.SaveSectionAsync(firstLegacySection);
        var secondLegacySection = await legacyStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        secondLegacySection.Data["SandboxId"] = "second";
        await legacyStateManager.SaveSectionAsync(secondLegacySection);

        var firstStateManager = CreateFileDeploymentStateManager(firstSha, legacySha);
        var secondStateManager = CreateFileDeploymentStateManager(secondSha, legacySha);
        var firstSection = await firstStateManager.AcquireSectionAsync("Azure:Sandboxes:first");
        var secondSection = await secondStateManager.AcquireSectionAsync("Azure:Sandboxes:second");
        await firstStateManager.SaveSectionAsync(firstSection);
        await secondStateManager.SaveSectionAsync(secondSection);

        await Task.WhenAll(
            firstStateManager.DeleteSectionAsync(firstSection),
            secondStateManager.DeleteSectionAsync(secondSection));

        Assert.Empty((await CreateFileDeploymentStateManager(firstSha, legacySha)
            .AcquireSectionAsync("Azure:Sandboxes:first")).Data);
        Assert.Empty((await CreateFileDeploymentStateManager(secondSha, legacySha)
            .AcquireSectionAsync("Azure:Sandboxes:second")).Data);

        var restartedLegacyStateManager = CreateFileDeploymentStateManager(legacySha);
        Assert.Equal(
            "first",
            (await restartedLegacyStateManager.AcquireSectionAsync("Azure:Sandboxes:first"))
                .Data["SandboxId"]?.GetValue<string>());
        Assert.Equal(
            "second",
            (await restartedLegacyStateManager.AcquireSectionAsync("Azure:Sandboxes:second"))
                .Data["SandboxId"]?.GetValue<string>());
    }

    [Fact]
    public async Task LoadStateDoesNotCreateLegacyDirectoryWhenLegacyFileAbsent()
    {
        var canonicalSha = Guid.NewGuid().ToString("N");
        var legacySha = Guid.NewGuid().ToString("N");
        var stateManager = CreateFileDeploymentStateManager(canonicalSha, legacySha);

        // Acquiring a section triggers the load path. When the legacy identity has no state file,
        // the manager must not manufacture the shared legacy directory or its lock file.
        await stateManager.AcquireSectionAsync("Azure");

        var legacyDirectory = Path.Combine(_aspireHome.FullName, "deployments", legacySha);
        Assert.False(Directory.Exists(legacyDirectory));
    }

    [Fact]
    public void LoadEffectiveStateDoesNotCreateLegacyDirectoryWhenLegacyFileAbsent()
    {
        var canonicalPath = Path.Combine(_aspireHome.FullName, "deployments", Guid.NewGuid().ToString("N"), "production.json");
        var legacyDirectory = Path.Combine(_aspireHome.FullName, "deployments", Guid.NewGuid().ToString("N"));
        var legacyPath = Path.Combine(legacyDirectory, "production.json");

        var effectiveState = FileDeploymentStateManager.LoadEffectiveState(canonicalPath, legacyPath);

        Assert.Empty(effectiveState);
        Assert.False(Directory.Exists(legacyDirectory));
    }

    private FileDeploymentStateManager CreateFileDeploymentStateManager(string? sha = null, string? legacySha = null)
    {
        // Use a unique SHA per test by default to avoid test interference,
        // but allow tests to share state by passing the same SHA
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:PathSha256"] = sha ?? Guid.NewGuid().ToString("N"),
                ["AppHost:LegacyDeploymentStatePathSha256"] = legacySha,
                [KnownConfigNames.AspireHome] = _aspireHome.FullName
            })
            .Build();

        var hostEnvironment = new TestHostEnvironment { EnvironmentName = "Development" };
        var pipelineOptions = Options.Create(new Hosting.Pipelines.PipelineOptions());

        return new FileDeploymentStateManager(
            NullLogger<FileDeploymentStateManager>.Instance,
            configuration,
            hostEnvironment,
            pipelineOptions);
    }

    public void Dispose()
    {
        _aspireHome.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    [InlineData("my-environment")]
    [InlineData("my_environment")]
    [InlineData("MyEnvironment123")]
    [InlineData("dev-env_01")]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("1")]
    public void IsValidEnvironmentName_WithValidNames_ReturnsTrue(string environmentName)
    {
        Assert.True(FileDeploymentStateManager.IsValidEnvironmentName(environmentName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("dev/prod")]
    [InlineData("dev\\prod")]
    [InlineData("env name")]
    [InlineData("env.name")]
    [InlineData("env@name")]
    [InlineData("env#name")]
    [InlineData("env$name")]
    [InlineData("env%name")]
    [InlineData("env&name")]
    [InlineData("env*name")]
    [InlineData("env+name")]
    [InlineData("env=name")]
    [InlineData("env!name")]
    [InlineData("env?name")]
    [InlineData("env<name")]
    [InlineData("env>name")]
    [InlineData("env|name")]
    [InlineData("env:name")]
    [InlineData("env;name")]
    [InlineData("env\"name")]
    [InlineData("env'name")]
    public void IsValidEnvironmentName_WithInvalidNames_ReturnsFalse(string environmentName)
    {
        Assert.False(FileDeploymentStateManager.IsValidEnvironmentName(environmentName));
    }

    [Fact]
    public void IsValidEnvironmentName_WithNull_ReturnsFalse()
    {
        Assert.False(FileDeploymentStateManager.IsValidEnvironmentName(null!));
    }

    [Fact]
    public void GetStatePath_WithInvalidEnvironmentName_ThrowsArgumentException()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:PathSha256"] = Guid.NewGuid().ToString("N")
            })
            .Build();

        var hostEnvironment = new TestHostEnvironment { EnvironmentName = "../etc/passwd" };
        var pipelineOptions = Options.Create(new Hosting.Pipelines.PipelineOptions());

        var stateManager = new FileDeploymentStateManager(
            NullLogger<FileDeploymentStateManager>.Instance,
            configuration,
            hostEnvironment,
            pipelineOptions);

        var exception = Assert.Throws<ArgumentException>(() => stateManager.StateFilePath);
        Assert.Contains("contains invalid characters", exception.Message);
        Assert.Contains("[a-zA-Z0-9_-]+", exception.Message);
    }

    [Theory]
    [InlineData("dev/prod")]
    [InlineData("..\\windows")]
    [InlineData("env name")]
    public void GetStatePath_WithPathTraversalAttempts_ThrowsArgumentException(string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:PathSha256"] = Guid.NewGuid().ToString("N")
            })
            .Build();

        var hostEnvironment = new TestHostEnvironment { EnvironmentName = environmentName };
        var pipelineOptions = Options.Create(new Hosting.Pipelines.PipelineOptions());

        var stateManager = new FileDeploymentStateManager(
            NullLogger<FileDeploymentStateManager>.Instance,
            configuration,
            hostEnvironment,
            pipelineOptions);

        var exception = Assert.Throws<ArgumentException>(() => stateManager.StateFilePath);
        Assert.Contains("contains invalid characters", exception.Message);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
