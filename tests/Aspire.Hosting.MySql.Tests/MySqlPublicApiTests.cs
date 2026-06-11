// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.MySql.Tests;

public class MySqlPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddMySqlShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "MySql";

        var action = () => builder.AddMySql(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMySqlShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddMySql(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;
        const string name = "db";

        var action = () => builder.AddDatabase(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddDatabaseShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMySql("MySql");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddDatabase(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPhpMyAdminShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;

        var action = () => builder.WithPhpMyAdmin();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithHostPortShouldThrowWhenBuilderIsNull(bool includePort)
    {
        IResourceBuilder<PhpMyAdminContainerResource> builder = null!;
        int? port = includePort ? null : 6033;

        var action = () => builder.WithHostPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;
        const string source = "/MySql/data";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMySql("MySql");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithDataBindMount(source);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithInitBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;
        const string source = "/MySql/init.sql";

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithInitBindMount(source);
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithInitBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMySql("MySql");
        var source = isNull ? null! : string.Empty;

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithInitBindMount(source);
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithInitFilesShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;
        const string source = "/MySql/init.sql";

        var action = () => builder.WithInitFiles(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithInitFilesShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMySql("MySql");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithInitFiles(source);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMySqlDatabaseResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        const string databaseName = "db";
        const string passwordName = "password";
        var password = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, passwordName, special: false);
        const string parentName = "parent";
        var parent = new MySqlServerResource(parentName, password);

        var action = () => new MySqlDatabaseResource(name, databaseName, parent);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMySqlDatabaseResourceShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        const string name = "MySql";
        var databaseName = isNull ? null! : string.Empty;
        const string passwordName = "password";
        var password = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, passwordName, special: false);
        const string parentName = "parent";
        var parent = new MySqlServerResource(parentName, password);

        var action = () => new MySqlDatabaseResource(name, databaseName, parent);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void CtorMySqlDatabaseResourceShouldThrowWhenParentIsNull()
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        const string name = "MySql";
        const string databaseName = "db";
        MySqlServerResource parent = null!;

        var action = () => new MySqlDatabaseResource(name, databaseName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMySqlServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        const string passwordName = "password";
        var password = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, passwordName, special: false);

        var action = () => new MySqlServerResource(name, password);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorMySqlServerResourceShouldThrowWhenPasswordIsNull()
    {
        const string name = "MySql";
        ParameterResource password = null!;

        var action = () => new MySqlServerResource(name, password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(password), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorPhpMyAdminContainerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new PhpMyAdminContainerResource(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPasswordShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MySqlServerResource> builder = null!;
        var password = TestDistributedApplicationBuilder.Create(testOutputHelper).AddParameter("password");

        var action = () => builder.WithPassword(password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPasswordShouldThrowWhenPasswordIsNull()
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMySql("MySql");
        IResourceBuilder<ParameterResource> password = null!;

        var action = () => builder.WithPassword(password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(password), exception.ParamName);
    }
}
