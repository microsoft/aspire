from aspire_app import create_builder


with create_builder() as builder:
    project = builder.add_dotnet_project("project", "./src/Project/Project.csproj")
    _ = project.name
    _ = project.command
    _ = project.working_dir

    builder.run()
