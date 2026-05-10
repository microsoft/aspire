# Aspire Python validation AppHost

from aspire_app import create_builder


with create_builder() as builder:
    api = builder.add_project("api", "./src/Api")
    api_migrations = builder.add_project("api-migrations-project", "./src/Api.Migrations")

    api.add_e_f_migrations("api-migrations").with_migrations_project(migrations_project="./src/Api.Migrations")
    api.add_e_f_migrations(
        "api-dbcontext-migrations",
        db_context_type_name="Api.Data.AppDbContext").with_migrations_project(migrations_project=api_migrations).with_migration_namespace("Api.Data.Migrations")
    api.add_e_f_migrations("api-script-migrations").publish_as_migration_script()
    api.add_e_f_migrations("api-script-options-migrations").publish_as_migration_script(
        idempotent=False,
        no_transactions=True)
    api.add_e_f_migrations("api-bundle-migrations").publish_as_migration_bundle()
    api.add_e_f_migrations("api-bundle-options-migrations").publish_as_migration_bundle(
        target_runtime="linux-x64",
        self_contained=True,
        publish_container=True,
        base_image="mcr.microsoft.com/dotnet/runtime:10.0")

    builder.run()
