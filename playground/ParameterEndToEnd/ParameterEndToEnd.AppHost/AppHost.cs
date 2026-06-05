// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// Use launch profiles to change DOTNET_ENVIRONMENT. If you use the "UseConnectionString"
// environment you will need to add a Cosmos connection string to your user
// secrets (example):
// {
//   "ConnectionStrings": {
//     "db": "[insert connection string]"
//   }
// }
//
// The expression below is a bit more complex than the average developer app would
// probably have, but in our repo we'll probably want to experiment with seperately
// deployed resources a little bit.
var db = builder.AddSqlServer("sql")
                .PublishAsConnectionString()
                .AddDatabase("db");

var insertionrows = builder.AddParameter("insertionrows")
    .WithDescription("The number of rows to insert into the database.");

var cs = builder.AddConnectionString("cs", ReferenceExpression.Create($"sql={db.Resource.Parent.PrimaryEndpoint};rows={insertionrows}"));
var parameterFromConnectionStringConfig = builder.AddConnectionString("parameterFromConnectionStringConfig");

var throwing = builder.AddParameter("throwing", () => throw new InvalidOperationException("This is a test exception."));
var parameterFromConnectionStringConfigMissing = builder.AddConnectionString("parameterFromConnectionStringConfigMissing");

var parameterWithMarkdownDescription = builder.AddParameter("markdownDescription")
    .WithDescription("""
        This is a parameter with a **markdown** description.

        And has another paragraph.

        And a list:
        - Item 1
        - Item 2 (and a [hyperlink](https://github.com/microsoft/aspire))
        - Item 3
        """, enableMarkdown: true);

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var parameterWithCustomInput = builder.AddParameter("customInput")
    .WithDescription("This parameter only accepts a number.")
    .WithCustomInput(p => new()
    {
        Name = "ParameterInput",
        InputType = InputType.Number,
        Label = "Custom Input",
        Placeholder = "Enter a number",
        Description = p.Description,
    });
#pragma warning restore ASPIREINTERACTION001

builder.AddProject<Projects.ParameterEndToEnd_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithEnvironment("InsertionRows", insertionrows)
       .WithReference(cs)
       .WithReference(db);

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
