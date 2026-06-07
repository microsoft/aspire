var builder = DistributedApplication.CreateBuilder(args);

var vcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT")
    ?? throw new InvalidOperationException("Set VCPKG_ROOT to the root of your vcpkg installation before running this playground.");

builder.AddCMakeApp("cpp-api", "../cpp-api", targetName: "cpp-api")
       .WithRequiredBuildTool("vcpkg", "https://vcpkg.io/")
       .WithRequiredBuildTool("ninja", "https://ninja-build.org/")
       .WithConfigureArgs(
          "-G",
          "Ninja",
          $"-DCMAKE_TOOLCHAIN_FILE={Path.Combine(vcpkgRoot, "scripts", "buildsystems", "vcpkg.cmake")}")
       .WithHttpEndpoint(env: "PORT")
       .WithExternalHttpEndpoints();

builder.Build().Run();
