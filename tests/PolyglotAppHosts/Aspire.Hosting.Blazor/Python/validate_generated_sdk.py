from aspire_app import DistributedApplicationBuilder, DotnetProjectResource


required_members = [
    (DistributedApplicationBuilder, "add_dotnet_project_blazor_gateway"),
    (DotnetProjectResource, "with_blazor_client_app"),
]

for owner, member_name in required_members:
    if not hasattr(owner, member_name):
        raise AttributeError(f"{owner.__name__}.{member_name} is missing from the generated SDK")
