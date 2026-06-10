// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.UserSecrets;

namespace Aspire.Cli.Tests.Secrets;

public class SecretsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SecretsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string GetSecretsPath() => Path.Combine(_tempDir, "secrets.json");

    [Fact]
    public void Set_CreatesFileAndSetsValue()
    {
        var path = GetSecretsPath();
        var store = new SecretsStore(path);

        store.Set("key1", "value1");
        store.Save();

        Assert.True(File.Exists(path));

        // Reload and verify
        var store2 = new SecretsStore(path);
        Assert.Equal("value1", store2.Get("key1"));
    }

    [Fact]
    public void Get_ReturnsNull_WhenKeyMissing()
    {
        var store = new SecretsStore(GetSecretsPath());
        Assert.Null(store.Get("nonexistent"));
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        var path = GetSecretsPath();
        var store = new SecretsStore(path);

        store.Set("key1", "original");
        store.Save();

        var store2 = new SecretsStore(path);
        store2.Set("key1", "updated");
        store2.Save();

        var store3 = new SecretsStore(path);
        Assert.Equal("updated", store3.Get("key1"));
    }

    [Fact]
    public void List_ReturnsAllPairs()
    {
        var path = GetSecretsPath();
        var store = new SecretsStore(path);

        store.Set("a", "1");
        store.Set("b", "2");
        store.Set("c", "3");
        store.Save();

        var store2 = new SecretsStore(path);
        var secrets = store2.AsEnumerable().ToList();
        Assert.Equal(3, secrets.Count);
    }

    [Fact]
    public void Delete_RemovesKey()
    {
        var path = GetSecretsPath();
        var store = new SecretsStore(path);

        store.Set("key1", "value1");
        store.Set("key2", "value2");
        store.Save();

        var store2 = new SecretsStore(path);
        Assert.True(store2.Remove("key1"));
        store2.Save();

        var store3 = new SecretsStore(path);
        Assert.Null(store3.Get("key1"));
        Assert.Equal("value2", store3.Get("key2"));
    }

    [Fact]
    public void Delete_ReturnsFalse_WhenKeyMissing()
    {
        var store = new SecretsStore(GetSecretsPath());
        Assert.False(store.Remove("nonexistent"));
    }

    [Fact]
    public void Load_FlattensNestedJson()
    {
        var path = GetSecretsPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Write nested JSON manually
        File.WriteAllText(path, """
        {
            "Azure": {
                "Location": "eastus2",
                "SubscriptionId": "abc-123"
            }
        }
        """);

        var store = new SecretsStore(path);
        Assert.Equal("eastus2", store.Get("Azure:Location"));
        Assert.Equal("abc-123", store.Get("Azure:SubscriptionId"));
    }

    [Fact]
    public void Load_ConvertsPrimitiveValuesToStrings()
    {
        var path = GetSecretsPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        File.WriteAllText(path, """
        {
            "StringValue": "text",
            "NumberValue": 42,
            "BoolValue": true,
            "Nested": {
                "InnerNumber": 3.14,
                "InnerBool": false,
                "InnerNull": null
            }
        }
        """);

        var store = new SecretsStore(path);

        Assert.Equal("text", store.Get("StringValue"));
        Assert.Equal("42", store.Get("NumberValue"));
        Assert.Equal("true", store.Get("BoolValue"));
        Assert.Equal("3.14", store.Get("Nested:InnerNumber"));
        Assert.Equal("false", store.Get("Nested:InnerBool"));
        Assert.Null(store.Get("Nested:InnerNull"));
    }

    [Fact]
    public void Load_DecodesEncodedStringValues()
    {
        var path = GetSecretsPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        File.WriteAllText(path, """
        {
            "EncodedStringValue": "value with \"quotes\", \u0027apostrophes\u0027, and \u4F60\u597D"
        }
        """);

        var store = new SecretsStore(path);

        Assert.Equal("value with \"quotes\", 'apostrophes', and 你好", store.Get("EncodedStringValue"));
    }

    [Fact]
    public void Save_UsesRelaxedEscaping()
    {
        var path = GetSecretsPath();
        var store = new SecretsStore(path);

        store.Set("key", "value with 'quotes' and <angle> brackets");
        store.Save();

        var json = File.ReadAllText(path);
        // Should NOT contain \u0027 or \u003C escaping
        Assert.DoesNotContain("\\u0027", json);
        Assert.DoesNotContain("\\u003C", json);
        Assert.Contains("'quotes'", json);
        Assert.Contains("<angle>", json);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var path = Path.Combine(_tempDir, "nested", "dir", "secrets.json");
        var store = new SecretsStore(path);

        store.Set("key", "value");
        store.Save();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ContainsKey_ReturnsTrueForExistingKey()
    {
        var store = new SecretsStore(GetSecretsPath());
        store.Set("exists", "yes");

        Assert.True(store.ContainsKey("exists"));
        Assert.False(store.ContainsKey("missing"));
    }

    [Fact]
    public void Count_ReflectsNumberOfSecrets()
    {
        var store = new SecretsStore(GetSecretsPath());
        Assert.Equal(0, store.Count);

        store.Set("a", "1");
        store.Set("b", "2");
        Assert.Equal(2, store.Count);

        store.Remove("a");
        Assert.Equal(1, store.Count);
    }
}
