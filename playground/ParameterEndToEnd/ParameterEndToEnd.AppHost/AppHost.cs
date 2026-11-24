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
var sql = builder.AddSqlServer("sql");
#pragma warning disable CS0618 // This playground intentionally exercises obsolete manifest-only PublishAsConnectionString behavior.
sql.PublishAsConnectionString();
#pragma warning restore CS0618
var db = sql.AddDatabase("db");

var insertionrows = builder.AddParameter("insertionrows")
    .WithDescription("The number of rows to insert into the database.");

// Lets dashboard development exercise the "Show hidden resources" path in the Parameters view.
builder.AddParameter("hiddenParameter")
    .WithDescription("A hidden parameter used to exercise dashboard visibility indicators.")
    .WithHidden();

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

// Optional parameter without a value: never prompted for and resolves to null.
var optionalUnset = builder.AddParameter("optionalUnset")
    .WithDescription("Optional parameter with no configured value. It should not be prompted for.")
    .AsOptional();
AddProgrammaticValueCommands(optionalUnset);

// Optional parameter with a value from appsettings.json: behaves like a normal parameter.
var optionalConfigured = builder.AddParameter("optionalConfigured")
    .WithDescription("Optional parameter with a value configured in appsettings.json.")
    .AsOptional();
AddProgrammaticValueCommands(optionalConfigured);

// Required parameter without configuration whose value is supplied by AppHost code,
// so it should not be prompted for at startup.
var programmaticValue = builder.AddParameter("programmaticValue")
    .WithDescription("Required parameter whose value is set by AppHost code when it has no configured value.");
if (!programmaticValue.Resource.TryGetCurrentValue(out _))
{
    await programmaticValue.Resource.SetValueAsync("set-by-apphost");
}
AddProgrammaticValueCommands(programmaticValue);

// Marking optional and then required again restores normal required behavior (prompted when missing).
var requiredAfterOptional = builder.AddParameter("requiredAfterOptional")
    .WithDescription("Parameter marked optional and then required again. It should be prompted for.")
    .AsOptional()
    .AsRequired();

builder.AddDotnetProject("api", "../ParameterEndToEnd.ApiService/ParameterEndToEnd.ApiService.csproj")
       .WithExternalHttpEndpoints()
       .WithEnvironment("InsertionRows", insertionrows)
       .WithEnvironment("OPTIONAL_UNSET", optionalUnset)
       .WithEnvironment("OPTIONAL_CONFIGURED", optionalConfigured)
       .WithEnvironment("PROGRAMMATIC_VALUE", programmaticValue)
       .WithEnvironment("REQUIRED_AFTER_OPTIONAL", requiredAfterOptional)
       .WithReference(cs)
       .WithReference(db);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddDotnetProject(KnownResourceNames.AspireDashboard, "../../../src/Aspire.Dashboard/Aspire.Dashboard.csproj");
#endif

builder.Build().Run();

// Exercises ParameterResource.SetValueAsync at runtime from AppHost code (as opposed to the
// built-in dashboard "Set parameter"/"Delete parameter" commands, which go through prompts/user secrets).
static void AddProgrammaticValueCommands(IResourceBuilder<ParameterResource> parameter)
{
    parameter.WithCommand(
        "set-from-code",
        "Set value from code",
        async context =>
        {
            var value = $"from-code-{DateTime.Now:HH:mm:ss}";
            await parameter.Resource.SetValueAsync(value, context.CancellationToken);
            return CommandResults.Success($"Set '{parameter.Resource.Name}' to '{value}'.");
        },
        new CommandOptions { Description = "Calls ParameterResource.SetValueAsync with a timestamped value.", IconName = "Code" });

    parameter.WithCommand(
        "clear-from-code",
        "Clear value from code",
        async context =>
        {
            await parameter.Resource.SetValueAsync(null, context.CancellationToken);
            return CommandResults.Success($"Cleared '{parameter.Resource.Name}'.");
        },
        new CommandOptions { Description = "Calls ParameterResource.SetValueAsync(null).", IconName = "Delete" });

    parameter.WithCommand(
        "show-current-value",
        "Show current value",
        context =>
        {
            var result = parameter.Resource.TryGetCurrentValue(out var value)
                ? $"TryGetCurrentValue returned true: '{value}'."
                : "TryGetCurrentValue returned false (no value).";
            return Task.FromResult(CommandResults.Success(result, result));
        },
        new CommandOptions { Description = "Calls ParameterResource.TryGetCurrentValue.", IconName = "Info" });
}
