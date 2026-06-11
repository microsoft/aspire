// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var sql1 = builder.AddAzureSqlServer("sql1")
    .RunAsContainer();

var db1 = sql1.AddDatabase("db1");

var sql2 = builder.AddAzureSqlServer("sql2");
var db2 = sql2.AddDatabase("db2");

var api = builder.AddProject<Projects.SqlServerEndToEnd_ApiService>("api")
       .WithExternalHttpEndpoints();

// Add EF migrations resource for the api project
// This adds dashboard commands for managing EF migrations
var db1Migrations = api.AddEFMigrations("db1-migrations", "MyDb1Context")
    .WithMigrationsProject<Projects.SqlServerEndToEnd_Common>()
    .WithMigrationOutputDirectory("Db1Migrations")
    .RunDatabaseUpdateOnStart()                        // Note that this only works during local development. The migrations resource is not deployed.
    .PublishAsMigrationBundle(publishContainer: true)
    .WithReference(db1).WaitFor(db1);

var db2Migrations = api.AddEFMigrations("db2-migrations", "MyDb2Context")
    .WithMigrationsProject<Projects.SqlServerEndToEnd_Common>()
    .WithMigrationOutputDirectory("Db2Migrations")
    .RunDatabaseUpdateOnStart()                        // Note that this only works during local development. The migrations resource is not deployed.
    .PublishAsMigrationBundle(publishContainer: true)
    .WithReference(db2).WaitFor(db2);

api
    .WaitForCompletion(db1Migrations)
    .WaitForCompletion(db2Migrations);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
