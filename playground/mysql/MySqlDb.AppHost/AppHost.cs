// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var catalogDbName = "catalog"; // MySql database & table names are case-sensitive on non-Windows.
var mySql = builder.AddMySql("mysql")
    .WithEnvironment("MYSQL_DATABASE", catalogDbName)
    .WithBindMount("../MySql.ApiService/data", "/docker-entrypoint-initdb.d")
    .WithPhpMyAdmin();

var catalogDb = mySql.AddDatabase(catalogDbName);

var myTestDb = mySql.AddDatabase("myTestDb");

var myTestDb2 = mySql.AddDatabase("myTestDb2").WithCreationScript($"""

    CREATE DATABASE IF NOT EXISTS `myTestDb2`;

    USE myTestDb2;

    CREATE TABLE IF NOT EXISTS example_table (
        id INT AUTO_INCREMENT PRIMARY KEY,
        name VARCHAR(255) NOT NULL
    );

    INSERT INTO example_table (name) VALUES ('Example Name 1');
""");

builder.AddProject<Projects.MySql_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithReference(catalogDb).WaitFor(catalogDb)
    .WithReference(myTestDb).WaitFor(myTestDb)
    .WithReference(myTestDb2).WaitFor(myTestDb2);

builder.Build().Run();
