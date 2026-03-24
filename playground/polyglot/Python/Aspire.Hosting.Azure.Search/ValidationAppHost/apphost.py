# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent / ".modules"))

from aspire_app import create_builder


with create_builder() as builder:
    search = builder.add_azure_search("resource")
    search.with_search_role_assignments()
    builder.run()
