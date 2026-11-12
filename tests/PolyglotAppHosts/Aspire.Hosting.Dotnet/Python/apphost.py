from aspire_app import create_builder


with create_builder() as builder:
    tool = builder.add_dotnet_cli_tool("tool", "dotnet-ef")
    tool.with_tool_package("dotnet-ef")
    tool.with_tool_version("10.0.*")
    tool.with_tool_prerelease()
    tool.with_tool_source("https://example.invalid/feed")
    tool.with_tool_ignore_existing_feeds()
    tool.with_tool_ignore_failed_sources()

    project = builder.add_dotnet_project(
        "project",
        "./src/Project/Project.csproj",
        options={"launchProfileName": "https"},
    )
    project.with_replicas(2)
    project.disable_forwarded_headers()
    project.with_endpoints_in_env(["http"])
    _ = project.name
    _ = project.command
    _ = project.working_dir

    builder.run()
