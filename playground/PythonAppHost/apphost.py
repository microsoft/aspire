# Aspire Python AppHost playground
# Run with: aspire run

from aspire_app import create_builder

with create_builder() as builder:
    cache = builder.add_redis("cache")

    app = (
        builder.add_uvicorn_app("app", "./app", "main:app")
        .with_uv()
        .with_external_http_endpoints()
        .with_reference(cache)
        .wait_for(cache)
        .with_http_health_check(path="/health")
    )

    frontend = (
        builder.add_vite_app("frontend", "./frontend")
        .with_reference(app)
        .wait_for(app)
    )

    app.publish_with_container_files(frontend, "./static")

    builder.run()
