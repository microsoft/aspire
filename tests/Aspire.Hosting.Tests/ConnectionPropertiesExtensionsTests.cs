// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ConnectionPropertiesExtensionsTests
{
    [Fact]
    public void CombineResourceMergesAdditionalValues()
    {
        var resource = new TestResource(
            "resource",
            new[]
            {
                new KeyValuePair<string, ReferenceExpression>("Host", ReferenceExpression.Create($"resourceHost")),
                new KeyValuePair<string, ReferenceExpression>("Port", ReferenceExpression.Create($"8080")),
            });

        var additional = new[]
        {
            new KeyValuePair<string, ReferenceExpression>("Port", ReferenceExpression.Create($"9090")),
            new KeyValuePair<string, ReferenceExpression>("Username", ReferenceExpression.Create($"user")),
        };

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in resource.CombineProperties(additional))
        {
            dict[property.Key] = property.Value.ValueExpression;
        }

        Assert.Equal(3, dict.Count);
        Assert.Equal("resourceHost", dict["Host"]);
        Assert.Equal("9090", dict["Port"]);
        Assert.Equal("user", dict["Username"]);
    }

    private sealed class TestResource(string name, IEnumerable<KeyValuePair<string, ReferenceExpression>> properties)
        : Resource(name), IResourceWithConnectionString
    {
    private readonly IReadOnlyList<KeyValuePair<string, ReferenceExpression>> _properties = new List<KeyValuePair<string, ReferenceExpression>>(properties);

        public ReferenceExpression ConnectionStringExpression { get; } = ReferenceExpression.Create($"{name}.connectionString");

        IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() => _properties;
    }
}