# SPIKE: a Python AppHost whose C# service is hot reloaded by `aspire run --watch`
# (docs/plans/project-v2-csharpprogram-watch.md, Session 7).
from aspire_app import create_builder

with create_builder() as builder:
    (
        builder.add_dotnet_project("api", "../services/WatchDemo.Api")
        .with_env("GREETING_SOURCE", "python-apphost")
        .with_external_http_endpoints()
    )

    builder.run()
