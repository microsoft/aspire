// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationCommandsTests
{
    [Fact]
    public void AddEFMigrationsAddsUpdateDatabaseCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Commands are added to the migration resource
        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var updateCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-database-update"));

        Assert.NotNull(updateCommand);
        Assert.Contains("Update Database", updateCommand.DisplayName);
    }

    [Fact]
    public void AddEFMigrationsAddsDropDatabaseCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var dropCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-database-drop"));

        Assert.NotNull(dropCommand);
        Assert.Contains("Drop Database", dropCommand.DisplayName);
        Assert.NotNull(dropCommand.ConfirmationMessage);
    }

    [Fact]
    public void AddEFMigrationsAddsResetDatabaseCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var resetCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-database-reset"));

        Assert.NotNull(resetCommand);
        Assert.Contains("Reset Database", resetCommand.DisplayName);
        Assert.NotNull(resetCommand.ConfirmationMessage);
    }

    [Fact]
    public void AddEFMigrationsAddsAddMigrationCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var addCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-migrations-add"));

        Assert.NotNull(addCommand);
        Assert.Contains("Add Migration", addCommand.DisplayName);
    }

    [Fact]
    public void AddEFMigrationsAddsRemoveMigrationCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var removeCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-migrations-remove"));

        Assert.NotNull(removeCommand);
        Assert.Contains("Remove Migration", removeCommand.DisplayName);
    }

    [Fact]
    public void AddEFMigrationsAddsGetDatabaseStatusCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var statusCommand = commands.FirstOrDefault(c => c.Name.Contains("ef-database-status"));

        Assert.NotNull(statusCommand);
        Assert.Contains("Get Database Status", statusCommand.DisplayName);
    }

    [Fact]
    public void AddEFMigrationsAddsSixCommands()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        // Should have 6 EF-related commands
        var efCommands = commands.Where(c => c.Name.Contains("ef-")).ToList();
        Assert.Equal(6, efCommands.Count);
    }

    [Fact]
    public void AddEFMigrationsWithoutContextAddsCommandsWithoutContextSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations");

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var updateCommand = commands.FirstOrDefault(c => c.Name == "ef-database-update");

        Assert.NotNull(updateCommand);
        Assert.Equal("Update Database", updateCommand.DisplayName);
    }

    [Fact]
    public void AddEFMigrationsWithContextUsesContextTypeName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Verify the context type name is stored on the resource
        Assert.Equal(typeof(TestDbContext).FullName, migrations.Resource.ContextTypeName);
    }

    [Fact]
    public void CommandsHaveIcons()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        foreach (var command in commands.Where(c => c.Name.Contains("ef-")))
        {
            Assert.NotNull(command.IconName);
        }
    }

    [Fact]
    public void DestructiveCommandsHaveConfirmationMessages()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        var dropCommand = commands.First(c => c.Name.Contains("ef-database-drop"));
        var resetCommand = commands.First(c => c.Name.Contains("ef-database-reset"));

        Assert.NotNull(dropCommand.ConfirmationMessage);
        Assert.NotNull(resetCommand.ConfirmationMessage);
    }

    [Fact]
    public void NonDestructiveCommandsDoNotHaveConfirmationMessages()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        var updateCommand = commands.First(c => c.Name.Contains("ef-database-update"));
        var statusCommand = commands.First(c => c.Name.Contains("ef-database-status"));

        Assert.Null(updateCommand.ConfirmationMessage);
        Assert.Null(statusCommand.ConfirmationMessage);
    }

    [Fact]
    public void CommandsAreEnabledWhenNotExecuting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "EFMigration",
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Info)
        };

        foreach (var command in commands.Where(c => c.Name.Contains("ef-")))
        {
            var state = command.UpdateState(new UpdateCommandStateContext
            {
                ResourceSnapshot = snapshot,
                ServiceProvider = builder.Services.BuildServiceProvider()
            });
            Assert.Equal(ResourceCommandState.Enabled, state);
        }
    }

    [Fact]
    public void CommandsAreDisabledWhenExecutingCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Simulate a command currently executing
        migrations.Resource.IsExecutingCommand = true;

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "EFMigration",
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Info)
        };

        foreach (var command in commands.Where(c => c.Name.Contains("ef-")))
        {
            var state = command.UpdateState(new UpdateCommandStateContext
            {
                ResourceSnapshot = snapshot,
                ServiceProvider = builder.Services.BuildServiceProvider()
            });
            Assert.Equal(ResourceCommandState.Disabled, state);
        }
    }

    [Fact]
    public void CommandsAreReenabledAfterCommandCompletes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Simulate command executing then completing
        migrations.Resource.IsExecutingCommand = true;

        var commands = migrations.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "EFMigration",
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Info)
        };

        // Should be disabled while executing
        foreach (var command in commands.Where(c => c.Name.Contains("ef-")))
        {
            var state = command.UpdateState(new UpdateCommandStateContext
            {
                ResourceSnapshot = snapshot,
                ServiceProvider = builder.Services.BuildServiceProvider()
            });
            Assert.Equal(ResourceCommandState.Disabled, state);
        }

        // Simulate command completed
        migrations.Resource.IsExecutingCommand = false;
        snapshot = new CustomResourceSnapshot
        {
            ResourceType = "EFMigration",
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Info)
        };

        // Should be enabled again
        foreach (var command in commands.Where(c => c.Name.Contains("ef-")))
        {
            var state = command.UpdateState(new UpdateCommandStateContext
            {
                ResourceSnapshot = snapshot,
                ServiceProvider = builder.Services.BuildServiceProvider()
            });
            Assert.Equal(ResourceCommandState.Enabled, state);
        }
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }
}
