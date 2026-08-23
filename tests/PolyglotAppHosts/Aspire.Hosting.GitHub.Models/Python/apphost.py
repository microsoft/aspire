# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # 1) addGitHubModel — using the GitHubModelName enum
    github_model = builder.add_git_hub_model("resource", "OpenAIGpt4o")
    # 2) addGitHubModel — with organization parameter
    org_param = builder.add_parameter("parameter")
    github_model_with_org = builder.add_git_hub_model(
        "resource", "OpenAIGpt4oMini", organization=org_param
    )
    # 3) addGitHubModelById — using a model identifier string for models not in the enum
    custom_model = builder.add_git_hub_model_by_id("resource", "custom-model")
    # 3) withApiKey — configure a custom API key parameter
    api_key = builder.add_parameter("parameter")
    github_model.with_api_key(api_key)
    # 4) enableHealthCheck — integration-specific no-args health check
    github_model.enable_health_check()
    # 5) withReference — pass GitHubModelResource as a connection string source to a container
    container = builder.add_container("resource", "image")
    container.with_reference(github_model)
    # 6) withReference — pass GitHubModelResource as a source to another container with custom connection name
    container.with_reference(custom_model, connection_name="custom-model")
    app = builder.build()
    builder.run()
