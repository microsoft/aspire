// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;

/// <summary>
/// Test DTO to verify [AspireDto] generates TypeScript interfaces.
/// </summary>
[AspireDto]
public class TestConfigDto
{
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool Enabled { get; set; }
    public string? OptionalField { get; set; }

    [AspireValue("TestConfigs")]
    public static readonly TestConfigDto Default = new()
    {
        Name = "default",
        Port = 6379,
        Enabled = true,
        OptionalField = "cache"
    };

    [AspireValue("TestConfigs")]
    public static TestConfigDto Secure { get; } = new()
    {
        Name = "secure",
        Port = 6380,
        Enabled = true
    };

    [AspireValue("TestConfigs")]
    public static string UnicodeGreeting { get; } = "你好こんにちは";

    public static class Profiles
    {
        [AspireValue("TestConfigs")]
        public static readonly TestConfigDto Development = new()
        {
            Name = "development",
            Port = 5001,
            Enabled = false
        };
    }
}

/// <summary>
/// Test context type with exposed instance methods.
/// Verifies [AspireExport(ExposeMethods=true)] generates async methods.
/// </summary>
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
public class TestResourceContext
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }

    /// <summary>
    /// Instance method that should be exposed as async method.
    /// </summary>
    public Task<string> GetValueAsync()
    {
        return Task.FromResult($"{Name}: {Value}");
    }

    /// <summary>
    /// Instance method with parameter.
    /// </summary>
    public Task SetValueAsync(string value)
    {
        Name = value;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Instance method with return type.
    /// </summary>
    public Task<bool> ValidateAsync()
    {
        return Task.FromResult(!string.IsNullOrEmpty(Name));
    }
}

/// <summary>
/// Test environment context used in callbacks.
/// Verifies property-like object pattern (ctx.name.get(), ctx.name.set()).
/// </summary>
[AspireExport(ExposeProperties = true)]
public class TestEnvironmentContext
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Priority { get; set; }
}

/// <summary>
/// Test context with collection properties to verify consistent code generation.
/// Verifies both List and Dictionary properties generate proper getter patterns.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class TestCollectionContext
{
    /// <summary>
    /// List property - should generate AspireList getter like Dictionary properties.
    /// </summary>
    public List<string> Items { get; } = [];

    /// <summary>
    /// Dictionary property - already works with AspireDict getter.
    /// </summary>
    public Dictionary<string, string> Metadata { get; } = [];
}

[AspireExport(ExposeProperties = true)]
public class TestMutableCollectionContext
{
    public List<string> Tags { get; set; } = [];

    public Dictionary<string, int> Counts { get; set; } = [];
}

/// <summary>
/// Test DTO with complex nested types.
/// </summary>
[AspireDto]
public class TestNestedDto
{
    public string Id { get; set; } = string.Empty;
    public TestConfigDto? Config { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
}

/// <summary>
/// Test enum for type generation verification.
/// </summary>
public enum TestResourceStatus
{
    Pending,
    Running,
    Stopped,
    Failed
}

/// <summary>
/// Test DTO with deeply nested generic types.
/// </summary>
[AspireDto]
public class TestDeeplyNestedDto
{
    /// <summary>
    /// Deeply nested generic: Dictionary containing List of DTOs.
    /// </summary>
    public Dictionary<string, List<TestConfigDto>> NestedData { get; set; } = [];

    /// <summary>
    /// Array of dictionaries.
    /// </summary>
    public Dictionary<string, string>[] MetadataArray { get; set; } = [];
}
