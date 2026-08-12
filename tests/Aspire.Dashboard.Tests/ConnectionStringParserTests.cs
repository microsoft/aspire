// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class ConnectionStringParserTests
{
    [Theory]
    [InlineData("redis://[fe80::1]:6380", true, "fe80::1", 6380)]
    [InlineData("postgres://h/db", true, "h", 5432)]
    [InlineData("Endpoint=h:6379;password=pw", true, "h", 6379)]
    [InlineData("host=h;user=foo", true, "h", null)]
    [InlineData("broker1:9092,broker2:9092", true, "broker1", 9092)]
    [InlineData("/var/sqlite/file.db", false, "", null)]
    [InlineData("foo bar baz", false, "", null)]
    [InlineData("https://models.github.ai/inference", true, "models.github.ai", 443)]
    [InlineData("Server=tcp:localhost,1433;Database=test", true, "localhost", 1433)]
    [InlineData("Server=localhost;port=5432", true, "localhost", 5432)]
    // SQL Server patterns
    [InlineData("Server=myServerAddress;Database=myDataBase;User Id=myUsername;Password=myPassword;", true, "myServerAddress", null)]
    [InlineData("Server=myServerAddress,1433;Database=myDataBase;Trusted_Connection=True;", true, "myServerAddress", 1433)]
    [InlineData("Data Source=tcp:localhost,1433;Initial Catalog=TestDB;", true, "localhost", 1433)]
    [InlineData("Data Source=.\\SQLEXPRESS;AttachDbFilename=|DataDirectory|mydbfile.mdf;Integrated Security=true;User Instance=true;", true, ".\\SQLEXPRESS", null)]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=AspNetCore.StarterSite;Trusted_Connection=true;MultipleActiveResultSets=true", true, "(localdb)\\MSSQLLocalDB", null)]
    // PostgreSQL patterns  
    [InlineData("Host=localhost;Database=mydb;Username=myuser;Password=mypass", true, "localhost", null)]
    [InlineData("Host=localhost;Port=5432;Database=mydb;Username=myuser;Password=mypass", true, "localhost", 5432)]
    [InlineData("postgresql://user:password@localhost:5432/dbname", true, "localhost", 5432)]
    [InlineData("postgres://user:password@localhost/dbname", true, "localhost", 5432)]
    // MySQL patterns
    [InlineData("Server=localhost;Database=myDataBase;Uid=myUsername;Pwd=myPassword;", true, "localhost", null)]
    [InlineData("Server=localhost;Port=3306;Database=myDataBase;Uid=myUsername;Pwd=myPassword;", true, "localhost", 3306)]
    [InlineData("mysql://user:password@localhost:3306/database", true, "localhost", 3306)]
    // MongoDB patterns
    [InlineData("mongodb://localhost:27017", true, "localhost", 27017)]
    [InlineData("mongodb://user:password@localhost:27017/database", true, "localhost", 27017)]
    [InlineData("mongodb://localhost", true, "localhost", 27017)]
    [InlineData("mongodb+srv://cluster0.example.mongodb.net/database", true, "cluster0.example.mongodb.net", null)]
    // Redis patterns
    [InlineData("localhost:6379", true, "localhost", 6379)]
    [InlineData("redis://localhost:6379", true, "localhost", 6379)]
    [InlineData("rediss://localhost:6380", true, "localhost", 6380)]
    [InlineData("redis://user:password@localhost:6379/0", true, "localhost", 6379)]
    [InlineData("Endpoint=localhost:6379;Password=mypassword", true, "localhost", 6379)]
    // Oracle patterns
    [InlineData("Data Source=localhost:1521/XE;User Id=hr;Password=password;", true, "localhost", null)] // Won't parse port from path syntax
    // JDBC patterns (basic ones that should work - but many JDBC URLs are complex)
    [InlineData("jdbc:postgresql://localhost:5432/database", true, "localhost", 5432)]
    [InlineData("jdbc:mysql://localhost:3306/database", true, "localhost", 3306)]
    [InlineData("jdbc:sqlserver://localhost:1433;databaseName=TestDB", true, "localhost", 1433)]
    // Cloud provider patterns
    [InlineData("https://myaccount.blob.core.windows.net/", true, "myaccount.blob.core.windows.net", 443)]
    [InlineData("https://myvault.vault.azure.net:8080/", true, "myvault.vault.azure.net", 8080)]
    [InlineData("Server=tcp:myserver.database.windows.net,1433;Database=mydatabase;", true, "myserver.database.windows.net", 1433)]
    // Kafka patterns
    [InlineData("localhost:9092,localhost:9093,localhost:9094", true, "localhost", 9092)]
    [InlineData("broker-1:9092,broker-2:9092", true, "broker-1", 9092)]
    // RabbitMQ patterns
    [InlineData("amqp://localhost", true, "localhost", 5672)]
    [InlineData("amqp://user:pass@localhost:5672/vhost", true, "localhost", 5672)]
    [InlineData("amqps://localhost:5671", true, "localhost", 5671)]
    [InlineData("Host=localhost;Port=5672;VirtualHost=/;Username=guest;Password=guest", true, "localhost", 5672)]
    // Elasticsearch patterns
    [InlineData("http://localhost:9200", true, "localhost", 9200)]
    [InlineData("https://elastic:password@localhost:9200", true, "localhost", 9200)]
    // InfluxDB patterns  
    [InlineData("http://localhost:8086", true, "localhost", 8086)]
    [InlineData("https://localhost:8086", true, "localhost", 8086)]
    // Cassandra patterns
    [InlineData("Contact Points=localhost;Port=9042", true, "localhost", 9042)]
    [InlineData("Contact Points=node1,node2,node3;Port=9042", false, "", null)] // Multiple contact points - too complex
    // Neo4j patterns
    [InlineData("bolt://localhost:7687", true, "localhost", 7687)]
    [InlineData("neo4j://localhost:7687", true, "localhost", 7687)]
    // Docker/container patterns
    [InlineData("server.local", true, "server.local", null)]
    [InlineData("my-service:5432", true, "my-service", 5432)]
    [InlineData("my-namespace.my-service.svc.cluster.local:5432", true, "my-namespace.my-service.svc.cluster.local", 5432)]
    // IPv6 patterns
    [InlineData("Server=[::1],1433", true, "::1", 1433)]
    [InlineData("Host=[2001:db8::1];Port=5432", true, "2001:db8::1", 5432)]
    [InlineData("http://[2001:db8::1]:8080", true, "2001:db8::1", 8080)]
    // Edge cases and invalid patterns
    [InlineData("", false, "", null)]
    [InlineData("   ", false, "", null)]
    [InlineData("=", false, "", null)]
    [InlineData("key=", false, "", null)]
    [InlineData("=value", false, "", null)]
    [InlineData("C:\\path\\to\\file.db", false, "", null)]
    [InlineData("./relative/path/file.db", false, "", null)]
    [InlineData("/absolute/path/file.db", false, "", null)]
    [InlineData("just some random text", false, "", null)]
    [InlineData("host=;port=5432", false, "", null)] // Empty host
    [InlineData("server=localhost;port=abc", true, "localhost", null)] // Invalid port
    [InlineData("server=localhost;port=99999", true, "localhost", null)] // Port out of range
    public void TryDetectHostAndPort_VariousFormats_ReturnsExpectedResults(
        string connectionString, 
        bool expectedResult, 
        string expectedHost, 
        int? expectedPort)
    {
        // Act
        var result = ConnectionStringParser.TryDetectHostAndPort(connectionString, out var host, out var port);
        
        // Assert
        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedHost, host);
            Assert.Equal(expectedPort, port);
        }
        else
        {
            Assert.Null(host);
            Assert.Null(port);
        }
    }

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=catalogdb", true, "catalogdb")]
    [InlineData("Data Source=localhost;Initial Catalog=Catalog", true, "Catalog")]
    [InlineData("Server=localhost;Database Name=Catalog", true, "Catalog")]
    [InlineData("jdbc:sqlserver://localhost:1433;databaseName=Catalog", true, "Catalog")]
    [InlineData("mongodb://user:password@localhost:27017/catalogdb", true, "catalogdb")]
    [InlineData("mongodb+srv://cluster0.example.mongodb.net/catalogdb", true, "catalogdb")]
    [InlineData("mssql://localhost:1433/Catalog", true, "Catalog")]
    [InlineData("postgresql://localhost:5432/catalog%20db", true, "catalog db")]
    [InlineData("jdbc:postgresql://localhost:5432/catalogdb", true, "catalogdb")]
    [InlineData("mongodb://localhost:27017/", false, null)]
    [InlineData("https://models.github.ai/inference", false, null)]
    [InlineData("Host=localhost;Port=5432", false, null)]
    public void TryDetectDatabaseName_VariousFormats_ReturnsExpectedResults(string connectionString, bool expectedResult, string? expectedDatabaseName)
    {
        var result = ConnectionStringParser.TryDetectDatabaseName(connectionString, out var databaseName);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedDatabaseName, databaseName);
    }

    [Fact]
    public void TryDetectHostAndPort_IPv6URI_ReturnsCorrectHost()
    {
        // Test case specifically for IPv6 addresses with brackets
        var connectionString = "redis://[fe80::1]:6380";
        var result = ConnectionStringParser.TryDetectHostAndPort(connectionString, out var host, out var port);
        
        Assert.True(result);
        Assert.Equal("fe80::1", host); // Brackets should be trimmed
        Assert.Equal(6380, port);
    }

    [Fact]
    public void TryDetectHostAndPort_KeyValuePairsWithSemicolon_ParsesCorrectly()
    {
        var connectionString = "Endpoint=h:6379;password=pw;database=0";
        var result = ConnectionStringParser.TryDetectHostAndPort(connectionString, out var host, out var port);
        
        Assert.True(result);
        Assert.Equal("h", host);
        Assert.Equal(6379, port);
    }

    [Fact]
    public void TryDetectHostAndPort_DelimitedList_TakesFirstEntry()
    {
        var connectionString = "broker1:9092,broker2:9093,broker3:9094";
        var result = ConnectionStringParser.TryDetectHostAndPort(connectionString, out var host, out var port);
        
        Assert.True(result);
        Assert.Equal("broker1", host);
        Assert.Equal(9092, port);
    }
}