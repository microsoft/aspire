# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    builder.add_python_app("resource", ".", "app.py")
    builder.add_python_module("resource", ".", "app")
    builder.add_python_executable("resource", ".", "python3")
    uvicorn = builder.add_uvicorn_app("resource", ".", "app:app")
    uvicorn.with_virtual_env(".venv")
    uvicorn.with_debugging()
    uvicorn.with_entrypoint("Module", "app:app")
    uvicorn.with_pip()
    uvicorn.with_uv()
    builder.run()
