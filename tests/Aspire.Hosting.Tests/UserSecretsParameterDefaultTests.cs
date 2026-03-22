// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Aspire.Hosting.Publishing;
using Aspire.Shared.UserSecrets;
using Aspire.Hosting.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class UserSecretsParameterDefaultTests
{
    private static readonly ConstructorInfo s_userSecretsIdAttrCtor = typeof(UserSecretsIdAttribute).GetConstructor([typeof(string)])!;

    private static UserSecretsManagerFactory CreateFactory()
    {
        return new UserSecretsManagerFactory(
            new FileSystemService(
                new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void UserSecretsParameterDefault_GetDefaultValue_SavesValueInAppHostUserSecrets()
    {
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);
        var factory = CreateFactory();
        var manager = factory.GetOrCreate(testAssembly);
        var paramDefault = new TestParameterDefault();
        var userSecretDefault = new UserSecretsParameterDefault("TestApplication.AppHost", "param1", paramDefault, manager);
        var initialValue = userSecretDefault.GetDefaultValue();

        var userSecrets = GetUserSecrets(userSecretsId);
        Assert.Equal(initialValue, userSecrets["Parameters:param1"]);

        DeleteUserSecretsFile(userSecretsId);
    }

    [Fact]
    public void UserSecretsParameterDefault_GetDefaultValue_DoesntThrowIfValueCantBeSaved()
    {
        // Do not set a user secrets id attribute on the assembly
        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, []);
        var factory = CreateFactory();
        var manager = factory.GetOrCreate(testAssembly);
        var paramDefault = new TestParameterDefault();
        var userSecretDefault = new UserSecretsParameterDefault("TestApplication.AppHost", "param1", paramDefault, manager);

        var initialValue = userSecretDefault.GetDefaultValue();
        Assert.NotNull(initialValue);
    }

    [Fact]
    public void UserSecretsParameterDefault_GetDefaultValue_DoesntThrowIfSecretsFileContainsComments()
    {
        var userSecretsId = Guid.NewGuid().ToString("N");
        DeleteUserSecretsFile(userSecretsId);
        var userSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        if (File.Exists(userSecretsPath))
        {
            File.Delete(userSecretsPath);
        }
        var secretsFileContents = """
            {
                // This is a comment in a JSON file
                "SomeConfigKey": "some value"
            }
            """;
        EnsureUserSecretsDirectory(userSecretsPath);
        File.WriteAllText(userSecretsPath, secretsFileContents, Encoding.UTF8);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);
        var factory = CreateFactory();
        var manager = factory.GetOrCreate(testAssembly);
        var paramDefault = new TestParameterDefault();
        var userSecretDefault = new UserSecretsParameterDefault("TestApplication.AppHost", "param1", paramDefault, manager);

        var _ = userSecretDefault.GetDefaultValue();
    }

    [Fact]
    public async Task TrySetUserSecret_ConcurrentWrites_PreservesAllSecrets()
    {
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);

        // Create an isolated factory instance for this test to avoid cross-contamination
        var factory = CreateFactory();

        // Simulate concurrent writes from multiple threads (like SQL Server and RabbitMQ generating passwords)
        var tasks = new List<Task<bool>>();
        var secretsToWrite = new Dictionary<string, string>
        {
            ["Parameters:sqlserver-password"] = "SqlPassword123!",
            ["Parameters:rabbitmq-password"] = "RabbitPassword456!",
            ["Parameters:redis-password"] = "RedisPassword789!",
            ["Parameters:postgres-password"] = "PostgresPassword012!",
        };

        foreach (var kvp in secretsToWrite)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            tasks.Add(Task.Run(() =>
            {
                var manager = factory.GetOrCreate(testAssembly);
                return manager?.TrySetSecret(key, value) ?? false;
            }));
        }

        var results = await Task.WhenAll(tasks);

        // All writes should succeed
        Assert.All(results, Assert.True);

        // All secrets should be preserved
        var userSecrets = GetUserSecrets(userSecretsId);
        foreach (var kvp in secretsToWrite)
        {
            Assert.True(userSecrets.ContainsKey(kvp.Key), $"Secret '{kvp.Key}' was not found in user secrets");
            Assert.Equal(kvp.Value, userSecrets[kvp.Key]);
        }

        DeleteUserSecretsFile(userSecretsId);
    }

    [Fact]
    public async Task TrySetUserSecret_SqlServerAndRabbitMQ_BothSecretsPreserved()
    {
        // This test specifically reproduces the issue described in the bug report
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);

        // Create an isolated factory instance for this test to avoid cross-contamination
        var factory = CreateFactory();

        // Simulate SQL Server and RabbitMQ generating passwords concurrently
        var sqlTask = Task.Run(() =>
        {
            var manager = factory.GetOrCreate(testAssembly);
            return manager?.TrySetSecret("Parameters:sql-password", "SqlPassword123!") ?? false;
        });
        var rabbitTask = Task.Run(() =>
        {
            var manager = factory.GetOrCreate(testAssembly);
            return manager?.TrySetSecret("Parameters:rabbit-password", "RabbitPassword456!") ?? false;
        });

        var results = await Task.WhenAll(sqlTask, rabbitTask);

        // Both writes should succeed
        Assert.All(results, Assert.True);

        // Both secrets should be in the file
        var userSecrets = GetUserSecrets(userSecretsId);
        Assert.True(userSecrets.ContainsKey("Parameters:sql-password"), "SQL Server password was not found");
        Assert.True(userSecrets.ContainsKey("Parameters:rabbit-password"), "RabbitMQ password was not found");
        Assert.Equal("SqlPassword123!", userSecrets["Parameters:sql-password"]);
        Assert.Equal("RabbitPassword456!", userSecrets["Parameters:rabbit-password"]);

        DeleteUserSecretsFile(userSecretsId);
    }

    [Fact]
    public async Task TrySetUserSecret_ConcurrentWritesSameKey_LastWriteWins()
    {
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);

        // Create an isolated factory instance for this test to avoid cross-contamination
        var factory = CreateFactory();

        // Simulate concurrent writes to the same key
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 10; i++)
        {
            var value = $"Value{i}";
            tasks.Add(Task.Run(() =>
            {
                var manager = factory.GetOrCreate(testAssembly);
                return manager?.TrySetSecret("Parameters:test-key", value) ?? false;
            }));
        }

        var results = await Task.WhenAll(tasks);

        // All writes should succeed
        Assert.All(results, Assert.True);

        // The key should exist with one of the values
        var userSecrets = GetUserSecrets(userSecretsId);
        Assert.True(userSecrets.ContainsKey("Parameters:test-key"));
        Assert.NotNull(userSecrets["Parameters:test-key"]);

        DeleteUserSecretsFile(userSecretsId);
    }

    [Fact]
    public void UserSecretsParameterDefault_WithCustomFactory_UsesProvidedFactory()
    {
        // This test verifies that the constructor overload taking a manager parameter
        // uses the provided manager
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);

        // Create a custom factory instance for test isolation
        var customFactory = CreateFactory();
        var manager = customFactory.GetOrCreate(testAssembly);
        var paramDefault = new TestParameterDefault();
        var userSecretDefault = new UserSecretsParameterDefault("TestApplication.AppHost", "param1", paramDefault, manager);

        var initialValue = userSecretDefault.GetDefaultValue();

        var userSecrets = GetUserSecrets(userSecretsId);
        Assert.Equal(initialValue, userSecrets["Parameters:param1"]);

        DeleteUserSecretsFile(userSecretsId);
    }

    [Fact]
    public void UserSecretsParameterDefault_WithCustomFactory_IsolatesFromOtherInstances()
    {
        // This test verifies that using a custom factory provides isolation
        // between test runs
        var userSecretsId1 = Guid.NewGuid().ToString("N");
        var userSecretsId2 = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId1);
        ClearUsersSecrets(userSecretsId2);

        var testAssembly1 = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly1"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId1])]);
        var testAssembly2 = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly2"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId2])]);

        // Use custom factory for first parameter default
        var customFactory1 = CreateFactory();
        var manager1 = customFactory1.GetOrCreate(testAssembly1);
        var paramDefault1 = new TestParameterDefault();
        var userSecretDefault1 = new UserSecretsParameterDefault("TestApp1.AppHost", "param1", paramDefault1, manager1);

        // Use different factory for second parameter default
        var customFactory2 = CreateFactory();
        var manager2 = customFactory2.GetOrCreate(testAssembly2);
        var paramDefault2 = new TestParameterDefault();
        var userSecretDefault2 = new UserSecretsParameterDefault("TestApp2.AppHost", "param2", paramDefault2, manager2);

        var value1 = userSecretDefault1.GetDefaultValue();
        var value2 = userSecretDefault2.GetDefaultValue();

        // Both should save successfully to their respective user secrets files
        var userSecrets1 = GetUserSecrets(userSecretsId1);
        var userSecrets2 = GetUserSecrets(userSecretsId2);

        Assert.Equal(value1, userSecrets1["Parameters:param1"]);
        Assert.Equal(value2, userSecrets2["Parameters:param2"]);

        DeleteUserSecretsFile(userSecretsId1);
        DeleteUserSecretsFile(userSecretsId2);
    }

    [Fact]
    public async Task UserSecretsParameterDefault_WithCustomFactory_ConcurrentAccess()
    {
        // This test verifies that the custom factory properly handles concurrent access
        var userSecretsId = Guid.NewGuid().ToString("N");
        ClearUsersSecrets(userSecretsId);

        var testAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TestAssembly"), AssemblyBuilderAccess.RunAndCollect, [new CustomAttributeBuilder(s_userSecretsIdAttrCtor, [userSecretsId])]);

        var customFactory = CreateFactory();
        var manager = customFactory.GetOrCreate(testAssembly);

        // Create multiple UserSecretsParameterDefault instances with different parameter names
        var tasks = new List<Task<string>>();
        for (int i = 0; i < 5; i++)
        {
            var paramName = $"param{i}";
            tasks.Add(Task.Run(() =>
            {
                var paramDefault = new TestParameterDefault();
                var userSecretDefault = new UserSecretsParameterDefault("TestApp.AppHost", paramName, paramDefault, manager);
                return userSecretDefault.GetDefaultValue();
            }));
        }

        var values = await Task.WhenAll(tasks);

        // All parameters should be saved
        var userSecrets = GetUserSecrets(userSecretsId);
        for (int i = 0; i < 5; i++)
        {
            var paramKey = $"Parameters:param{i}";
            Assert.True(userSecrets.ContainsKey(paramKey), $"Parameter '{paramKey}' was not found in user secrets");
            Assert.Equal(values[i], userSecrets[paramKey]);
        }

        DeleteUserSecretsFile(userSecretsId);
    }

    private static void EnsureUserSecretsDirectory(string secretsFilePath)
    {
        var directoryName = Path.GetDirectoryName(secretsFilePath);
        if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }
    }

    private static Dictionary<string, string?> GetUserSecrets(string userSecretsId)
    {
        var factory = CreateFactory();
        var manager = factory.GetOrCreateFromId(userSecretsId);

        // Read the secrets file directly
        var secrets = new Dictionary<string, string?>();
        if (File.Exists(manager.FilePath))
        {
            var json = File.ReadAllText(manager.FilePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var config = new ConfigurationBuilder()
                    .AddJsonFile(manager.FilePath, optional: true)
                    .Build();

                foreach (var kvp in config.AsEnumerable())
                {
                    if (kvp.Value != null)
                    {
                        secrets[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        return secrets;
    }

    private static void ClearUsersSecrets(string userSecretsId)
    {
        var filePath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static void DeleteUserSecretsFile(string userSecretsId)
    {
        var userSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        if (File.Exists(userSecretsPath))
        {
            File.Delete(userSecretsPath);
        }
    }

    private sealed class TestParameterDefault : ParameterDefault
    {
        public override string GetDefaultValue()
        {
            return Guid.NewGuid().ToString("N");
        }

        public override void WriteToManifest(ManifestPublishingContext context)
        {
            throw new NotImplementedException();
        }
    }
}
