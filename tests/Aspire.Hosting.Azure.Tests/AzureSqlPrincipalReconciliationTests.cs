// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Microsoft.Data.SqlClient;

namespace Aspire.Hosting.Azure.Tests;

/// <summary>
/// Runs the T-SQL that the Azure SQL role assignment deployment script emits against a real SQL
/// Server. The generated bicep snapshots only prove the text is emitted; these tests prove the batch
/// reconciles the database principal correctly on first run, on redeploy, and after the managed
/// identity has been recreated - and that it cannot damage anything when it fails.
/// </summary>
/// <remarks>
/// SQL Server has no Entra principals, so the batch is executed with two substitutions: the user is
/// mapped to a server login instead of being created with <c>TYPE = E</c>, because logins are the
/// only local principal that accepts an explicit sid, and the guard that limits reconciliation to
/// external users matches type <c>S</c> instead of <c>E</c> so it still matches what the harness
/// creates. Everything else - the sid comparison, the drop, the create guard, QUOTENAME, and the
/// transaction - is the emitted script unmodified.
/// </remarks>
public class AzureSqlPrincipalReconciliationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string HarnessPassword = "Pa55w0rd!Harness";

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationCreatesThePrincipalWithTheIdentityObjectIdAsItsSid()
    {
        var principalName = NewPrincipalName();
        var objectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();
        await CreateLoginAsync(principalName, objectId);

        await RunReconciliationAsync(database, principalName, objectId);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(objectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationHandlesPrincipalNamesRequiringEscaping()
    {
        // A user principal is a UPN and can contain an apostrophe, which has to survive the T-SQL
        // string literal, and QUOTENAME has to escape a closing bracket that would otherwise
        // terminate the identifier early.
        var principalName = $"o'brien]{Guid.NewGuid():N}@contoso.com";
        var objectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();
        await CreateLoginAsync(principalName, objectId);

        await RunReconciliationAsync(database, principalName, objectId);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(objectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationIsANoOpWhenTheScriptRunsAgain()
    {
        // Changing the script content changes the deploymentScripts resource definition, so ARM
        // re-executes it in every existing environment on upgrade, against a database that already
        // holds the user. An unguarded CREATE USER would fail all of those with Msg 15023.
        var principalName = NewPrincipalName();
        var objectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();
        await CreateLoginAsync(principalName, objectId);

        await RunReconciliationAsync(database, principalName, objectId);
        await RunReconciliationAsync(database, principalName, objectId);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(objectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationReplacesThePrincipalWhenTheIdentityObjectIdChanges()
    {
        var principalName = NewPrincipalName();
        var originalObjectId = Guid.NewGuid();
        var replacementObjectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();

        await CreateLoginAsync(principalName, originalObjectId);
        await RunReconciliationAsync(database, principalName, originalObjectId);

        // Deleting and recreating a managed identity keeps the name but changes the object id, which
        // leaves the existing database user carrying a sid that can no longer authenticate.
        await DropLoginAsync(principalName);
        await CreateLoginAsync(principalName, replacementObjectId);

        await RunReconciliationAsync(database, principalName, replacementObjectId);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(replacementObjectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationLeavesPrincipalsItDidNotCreateIntact()
    {
        var principalName = NewPrincipalName();
        var objectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();
        await CreateLoginAsync(principalName, objectId);

        // A principal of any other type sharing this name has a different sid, so without the type
        // guard it would look stale and be dropped along with whatever it had been granted.
        await using (var connection = await OpenAsync(database))
        {
            await ExecuteAsync(connection, $"""
                CREATE CERTIFICATE recon_cert ENCRYPTION BY PASSWORD = '{HarnessPassword}' WITH SUBJECT = 'unrelated', EXPIRY_DATE = '2099-01-01';
                CREATE USER {Quote(principalName)} FROM CERTIFICATE recon_cert;
                CREATE TABLE dbo.protected_data (id INT);
                GRANT SELECT ON dbo.protected_data TO {Quote(principalName)};
                """);
        }

        var failure = await Assert.ThrowsAsync<SqlException>(() => RunReconciliationAsync(database, principalName, objectId));
        Assert.Equal(15023, failure.Number);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal("C", principal?.Type);
        Assert.Equal(["CONNECT", "SELECT"], await GetGrantedPermissionsAsync(database, principalName));
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationRollsBackWhenTheUserCannotBeRecreated()
    {
        var principalName = NewPrincipalName();
        var originalObjectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();

        await CreateLoginAsync(principalName, originalObjectId);
        await RunReconciliationAsync(database, principalName, originalObjectId);

        // Removing the login makes the create half of the reconciliation fail after the drop half has
        // already succeeded, which is the window the transaction exists to close.
        await DropLoginAsync(principalName);

        var failure = await Assert.ThrowsAsync<SqlException>(() => RunReconciliationAsync(database, principalName, Guid.NewGuid()));
        Assert.Equal(15007, failure.Number);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(originalObjectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ReconciliationFailsWithoutDamageWhenThePrincipalOwnsASchema()
    {
        var principalName = NewPrincipalName();
        var originalObjectId = Guid.NewGuid();
        var replacementObjectId = Guid.NewGuid();
        var database = await CreateDatabaseAsync();

        await CreateLoginAsync(principalName, originalObjectId);
        await RunReconciliationAsync(database, principalName, originalObjectId);

        // db_owner lets an application create schemas, and a schema created without an AUTHORIZATION
        // clause is owned by whoever ran the statement, so an application's own migrations can leave
        // its identity owning a schema.
        await using (var connection = await OpenAsync(database))
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA appdata AUTHORIZATION {Quote(principalName)};");
            await ExecuteAsync(connection, "CREATE TABLE appdata.orders (id INT); INSERT INTO appdata.orders VALUES (7);");
        }

        await DropLoginAsync(principalName);
        await CreateLoginAsync(principalName, replacementObjectId);

        // SQL Server refuses to drop a principal that owns a securable, so reconciliation cannot
        // complete here. What matters is that it fails without leaving anything half done.
        var failure = await Assert.ThrowsAsync<SqlException>(() => RunReconciliationAsync(database, principalName, replacementObjectId));
        Assert.Equal(15138, failure.Number);

        var principal = await GetPrincipalAsync(database, principalName);
        Assert.Equal(ToSidLiteral(originalObjectId), principal?.Sid);
        Assert.True(principal?.IsDbOwner);

        await using var verification = await OpenAsync(database);
        Assert.Equal(principalName, await ScalarAsync<string>(verification, "SELECT USER_NAME(principal_id) FROM sys.schemas WHERE name = 'appdata';"));
        Assert.Equal(1, await ScalarAsync<int>(verification, "SELECT COUNT(*) FROM appdata.orders;"));
    }

    /// <summary>
    /// Pulls the T-SQL out of the PowerShell the deployment script runs, and adapts the two things
    /// SQL Server cannot express locally. Every substitution asserts that it matched, so a change to
    /// the emitted script fails these tests rather than silently reducing what they cover.
    /// </summary>
    private static string BuildReconciliationBatch(string principalName, Guid objectId)
    {
        var match = Regex.Match(
            AzureSqlServerResource.PrincipalReconciliationScript,
            "\\$sqlCmd = @\"\\r?\\n(?<sql>.*?)\\r?\\n\"@",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Could not find the T-SQL here-string in the emitted deployment script.");

        var sql = match.Groups["sql"].Value;

        // The script doubles apostrophes in PowerShell before interpolating the name into the literal.
        sql = ReplaceExactlyOnce(sql, "'$escapedPrincipalName'", $"'{principalName.Replace("'", "''")}'");
        sql = ReplaceExactlyOnce(sql, "'$id'", $"'{objectId}'");
        sql = ReplaceExactlyOnce(sql, "N' WITH SID = ' + @castId + N', TYPE = E;'", "N' FOR LOGIN ' + QUOTENAME(@name) + N';'");
        sql = ReplaceExactlyOnce(sql, "type = 'E'", "type = 'S'");

        return sql;
    }

    private static string ReplaceExactlyOnce(string input, string oldValue, string newValue)
    {
        var index = input.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The emitted script no longer contains \"{oldValue}\". Update this harness so these tests keep exercising the real script.");
        Assert.Equal(-1, input.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal));

        return string.Concat(input.AsSpan(0, index), newValue, input.AsSpan(index + oldValue.Length));
    }

    private static string NewPrincipalName() => $"webfrontend_identity-{Guid.NewGuid():N}";

    private static string ToSidLiteral(Guid objectId) => $"0x{Convert.ToHexString(objectId.ToByteArray())}";

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private async Task<SqlConnection> OpenAsync(string database)
    {
        var connectionString = new SqlConnectionStringBuilder(fixture.GetConnectionString())
        {
            InitialCatalog = database
        }.ConnectionString;

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        return connection;
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var database = $"recon_{Guid.NewGuid():N}";

        await using var connection = await OpenAsync("master");
        await ExecuteAsync(connection, $"CREATE DATABASE [{database}];");

        return database;
    }

    private async Task CreateLoginAsync(string name, Guid objectId)
    {
        // The user is mapped to this login, so the login's sid is what lands in
        // sys.database_principals and what the emitted comparison sees.
        await using var connection = await OpenAsync("master");
        await ExecuteAsync(connection, $"CREATE LOGIN {Quote(name)} WITH PASSWORD = '{HarnessPassword}', SID = {ToSidLiteral(objectId)};");
    }

    private async Task DropLoginAsync(string name)
    {
        await using var connection = await OpenAsync("master");
        await ExecuteAsync(connection, $"DROP LOGIN {Quote(name)};");
    }

    private async Task RunReconciliationAsync(string database, string principalName, Guid objectId)
    {
        await using var connection = await OpenAsync(database);
        await ExecuteAsync(connection, BuildReconciliationBatch(principalName, objectId));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }

    private async Task<PrincipalState?> GetPrincipalAsync(string database, string name)
    {
        await using var connection = await OpenAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(VARCHAR(64), sid, 1), type, ISNULL(IS_ROLEMEMBER('db_owner', name), 0)
            FROM sys.database_principals
            WHERE name = @name;
            """;
        command.Parameters.AddWithValue("@name", name);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PrincipalState(reader.GetString(0), reader.GetString(1).Trim(), reader.GetInt32(2) == 1);
    }

    private async Task<string[]> GetGrantedPermissionsAsync(string database, string name)
    {
        await using var connection = await OpenAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT permission_name
            FROM sys.database_permissions p
            JOIN sys.database_principals dp ON p.grantee_principal_id = dp.principal_id
            WHERE dp.name = @name
            ORDER BY permission_name;
            """;
        command.Parameters.AddWithValue("@name", name);

        var permissions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            permissions.Add(reader.GetString(0).Trim());
        }

        return [.. permissions];
    }

    private sealed record PrincipalState(string Sid, string Type, bool IsDbOwner);
}
