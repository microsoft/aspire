using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var project = builder.AddFoundry("proj-foundry")
    .AddProject("proj");

project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41Mini);

// Add a Foundry Toolbox with a single WebSearch tool. The toolbox is created on the Foundry data
// plane at deploy time via AgentToolboxes.CreateToolboxVersionAsync.
project.AddToolbox("field-tools", t => t.Version = "v1")
    .WithWebSearchTool();

builder.Build().Run();
