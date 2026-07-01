# C++ hello Aspire playground

This playground demonstrates hosting a C++ HTTP API built with CMake and vcpkg from an Aspire AppHost.
The AppHost uses `WithCMakeInstall()`, so deployment copies the CMake install prefix rather than only the built executable.

## Prerequisites

- CMake
- A C++ compiler toolchain
- vcpkg with `VCPKG_ROOT` set to the root of the vcpkg installation

## Run

```bash
aspire start --apphost CppHello.AppHost/CppHello.AppHost.csproj
aspire wait cpp-api --apphost CppHello.AppHost/CppHello.AppHost.csproj
```

The C++ API reads the `PORT` environment variable provided by Aspire and exposes `/` and `/health`.

## Deploy to Docker Compose

```bash
aspire deploy --apphost CppHello.AppHost/CppHello.AppHost.csproj
```

The generated Dockerfile bootstraps vcpkg in the build container and runs `cmake --install`, so Docker Compose deployment does not require `VCPKG_ROOT` on the deployment host.
