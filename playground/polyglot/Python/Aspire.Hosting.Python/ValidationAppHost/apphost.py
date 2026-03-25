# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    builder.add_python_app("resource")
    builder.add_python_module("resource")
    builder.add_python_executable("resource")
    uvicorn = builder.add_uvicorn_app("resource")
    uvicorn.with_virtual_environment()
    uvicorn.with_debugging()
    uvicorn.with_entrypoint()
    uvicorn.with_pip()
    uvicorn.with_uv()
    builder.run()
