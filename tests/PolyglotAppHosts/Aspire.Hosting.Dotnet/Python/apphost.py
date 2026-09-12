from aspire_app import create_builder


with create_builder() as builder:
    project = builder.add_dotnet_project("project", "./src/Project/Project.csproj")
    project.with_replicas(2)
    project.disable_forwarded_headers()
    project.with_endpoints_in_env(["http"])
    _ = project.name
    _ = project.command
    _ = project.working_dir

    builder.run()
