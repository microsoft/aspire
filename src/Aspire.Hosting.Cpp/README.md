# C++ hosting integration

Use this integration to model, configure, and orchestrate a C++ CMake application in an Aspire solution.

## Getting started

### Prerequisites

CMake must be available on the PATH of the machine running the AppHost. The CMake project also needs a working C++ toolchain such as MSVC Build Tools, GCC, or Clang. Declare additional tools such as Ninja, vcpkg, or Conan with `WithRequiredBuildTool`.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Cpp` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Cpp
```

## Usage example

Then, in the AppHost, add a C++ CMake application resource with either C# or TypeScript:

**C#**

```csharp
var api = builder.AddCMakeApp("api", "../cpp-api", targetName: "api")
                 .WithRequiredBuildTool("ninja", "https://ninja-build.org/")
                 .WithHttpEndpoint(env: "PORT")
                 .WithExternalHttpEndpoints();
```

**TypeScript**

```typescript
const api = await builder.addCMakeApp("api", "../cpp-api", "api")
    .withRequiredBuildTool("ninja", "https://ninja-build.org/")
    .withHttpEndpoint({ env: "PORT" })
    .withExternalHttpEndpoints();
```

`AddCMakeApp` runs `cmake -S ... -B ...` and `cmake --build ... --target ...` before launching the built executable. Pass application arguments with `WithAppArgs`, CMake configure arguments with `WithConfigureArgs`, and CMake build arguments with `WithBuildArgs`.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://cmake.org/documentation/
https://learn.microsoft.com/vcpkg/

## Feedback & contributing

https://github.com/microsoft/aspire

