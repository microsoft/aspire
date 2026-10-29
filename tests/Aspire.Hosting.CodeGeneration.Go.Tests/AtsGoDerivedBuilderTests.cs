// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.CodeGeneration.TypeScript.Tests.TestTypes;
using Aspire.Hosting.RemoteHost;
using Aspire.TestUtilities;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

/// <summary>
/// Go has no return-type covariance, so the handle interface generated for a derived resource type does not
/// satisfy the handle interface generated for its base type. These tests cover the accepting interface the
/// generator emits for parameters typed with such a base handle type, which lets a builder of any derived
/// type be passed wherever the AppHost API takes the base type.
/// </summary>
public class AtsGoDerivedBuilderTests
{
    private readonly AtsGoCodeGenerator _generator = new();

    [Fact]
    public void GeneratedCode_AcceptingInterfaceIsEmittedOnlyForBaseTypesWithDerivedTypes()
    {
        var generated = GenerateFixtureSdk();

        // GpuSpotPoolResource, WorkloadResource, QueueResource and the builder have no derived type.
        Assert.Equal(["AnyAksPoolResource", "AnyPoolResource", "AnySpotPoolResource"], GetAcceptingInterfaceNames(generated));
        Assert.Matches(
            @"// AnyPoolResource [^\r\n]*\r?\n(// [^\r\n]*\r?\n)*type AnyPoolResource interface \{",
            generated);
    }

    [Fact]
    public void GeneratedCode_MarkerMethodsLimitAcceptingInterfacesToTheirFamily()
    {
        var generated = GenerateFixtureSdk();

        // Go converts one interface type to another only when the source declares every method of the target.
        // An accepting interface therefore declares the marker of its own type and of every ancestor that has an
        // accepting interface: a value typed AnySpotPoolResource converts to AnyAksPoolResource and AnyPoolResource,
        // but never the reverse. The ancestors are listed as markers, not embedded.
        Assert.Equal(["handleReference", "isPoolResource()"], GetInterfaceMembers(generated, "AnyPoolResource"));
        Assert.Equal(
            ["handleReference", "isAksPoolResource()", "isPoolResource()"],
            GetInterfaceMembers(generated, "AnyAksPoolResource"));
        Assert.Equal(
            ["handleReference", "isAksPoolResource()", "isPoolResource()", "isSpotPoolResource()"],
            GetInterfaceMembers(generated, "AnySpotPoolResource"));

        // Each concrete interface declares the marker of every ancestor-or-self that has an accepting interface,
        // so a value of a handle interface type is assignable to the accepting interface of its base types.
        var interfaceMarkers = new[] { "AksPoolResource", "GpuSpotPoolResource", "PoolResource", "QueueResource", "SpotPoolResource", "WorkloadResource" }
            .Select(name => $"{name}: {string.Join(", ", GetInterfaceMarkers(generated, name))}")
            .ToArray();

        Assert.Equal(
            [
                "AksPoolResource: isAksPoolResource, isPoolResource",
                "GpuSpotPoolResource: isAksPoolResource, isPoolResource, isSpotPoolResource",
                "PoolResource: isPoolResource",
                "QueueResource: ",
                "SpotPoolResource: isAksPoolResource, isPoolResource, isSpotPoolResource",
                "WorkloadResource: "
            ],
            interfaceMarkers);

        Assert.Equal(
            [
                "aksPoolResource.isAksPoolResource",
                "aksPoolResource.isPoolResource",
                "gpuSpotPoolResource.isAksPoolResource",
                "gpuSpotPoolResource.isPoolResource",
                "gpuSpotPoolResource.isSpotPoolResource",
                "poolResource.isPoolResource",
                "spotPoolResource.isAksPoolResource",
                "spotPoolResource.isPoolResource",
                "spotPoolResource.isSpotPoolResource"
            ],
            GetImplMarkers(generated));
    }

    [Fact]
    public void GeneratedCode_BaseTypeMissingFromHandleTypesStillSatisfiesItsAcceptingInterface()
    {
        // A type that capabilities reference is emitted even when HandleTypes does not list it. Its own
        // hierarchy is then unknown, but it can still be the base of a listed type, so it must declare and
        // implement its own marker or no PoolResource value could be passed to an AnyPoolResource parameter.
        var generated = _generator.GenerateDistributedApplication(DerivedBuilderFixture.CreateContextWithoutPoolHandleType())["aspire.go"];

        Assert.Equal(["handleReference", "isPoolResource()"], GetInterfaceMembers(generated, "AnyPoolResource"));
        Assert.Equal(["isPoolResource"], GetInterfaceMarkers(generated, "PoolResource"));
        Assert.Contains("poolResource.isPoolResource", GetImplMarkers(generated));
    }

    [Fact]
    public void GeneratedCode_AcceptingInterfaceTakesAFreeNameWhenAnExportedValueRootUsesItsName()
    {
        var generated = _generator.GenerateDistributedApplication(DerivedBuilderFixture.CreateContextWithExportedValueRoot("AnyPoolResource"))["aspire.go"];

        // An exported value is emitted under its catalog name, so the accepting interface takes the next
        // free name instead, and the parameter positions that use it follow.
        Assert.Single(Regex.Matches(generated, @"^var AnyPoolResource = struct \{", RegexOptions.Multiline));
        Assert.Equal(["AnyAksPoolResource", "AnyPoolResource2", "AnySpotPoolResource"], GetAcceptingInterfaceNames(generated));
        Assert.Equal(["handleReference", "isPoolResource()"], GetInterfaceMembers(generated, "AnyPoolResource2"));
        Assert.Contains("WithPool(pool AnyPoolResource2) WorkloadResource", GetMethodSignatures(generated, "workloadResource", "With"));
    }

    [Fact]
    public void GeneratedCode_AcceptingInterfaceTakesAFreeNameWhenAnEnumMemberConstantUsesItsName()
    {
        var generated = _generator.GenerateDistributedApplication(DerivedBuilderFixture.CreateContextWithEnumMember())["aspire.go"];

        // An enum member is emitted as a package-level const named after the enum and the member
        // (AnyPool + Resource), so the accepting interface takes the next free name.
        Assert.Single(Regex.Matches(generated, @"^\tAnyPoolResource AnyPool = ""Resource""", RegexOptions.Multiline));
        Assert.Equal(["AnyAksPoolResource", "AnyPoolResource2", "AnySpotPoolResource"], GetAcceptingInterfaceNames(generated));
        Assert.Equal(["handleReference", "isPoolResource()"], GetInterfaceMembers(generated, "AnyPoolResource2"));
        Assert.Contains("WithPool(pool AnyPoolResource2) WorkloadResource", GetMethodSignatures(generated, "workloadResource", "With"));
    }

    [Fact]
    public void GeneratedCode_ParameterPositionsAcceptDerivedBuilders()
    {
        var generated = GenerateFixtureSdk();

        // Required parameters, slice and read-only map elements, and union members use the accepting interface.
        // Handle types without a derived type, callback parameters and mutable List/Dict handles stay concrete.
        Assert.Equal(
            [
                "WithAksPool(pool AnyAksPoolResource) WorkloadResource",
                "WithGpuSpotPool(pool GpuSpotPoolResource) WorkloadResource",
                "WithOptionalPool(name string, options ...*WithOptionalPoolOptions) WorkloadResource",
                "WithPool(pool AnyPoolResource) WorkloadResource",
                "WithPoolCallback(configure func(pool PoolResource)) WorkloadResource",
                "WithPoolList(pools *List[PoolResource]) WorkloadResource",
                "WithPoolMap(pools map[string]AnyPoolResource) WorkloadResource",
                "WithPoolOrName(pool any) WorkloadResource",
                "WithPoolSettings(settings *PoolSettings) WorkloadResource",
                "WithPools(pools []AnyPoolResource) WorkloadResource",
                "WithQueue(queue QueueResource) WorkloadResource",
                "WithSpotPool(pool AnySpotPoolResource) WorkloadResource"
            ],
            GetMethodSignatures(generated, "workloadResource", "With"));

        // DTO fields and options struct fields are parameter-like positions too. An optional handle parameter
        // is a pointer field in its options struct.
        Assert.Matches(@"type PoolSettings struct \{\r?\n\tPool AnyPoolResource `json:""pool,omitempty""`", generated);
        Assert.Matches(@"type WithOptionalPoolOptions struct \{\r?\n\tPool \*AnyPoolResource `json:""pool,omitempty""`", generated);

        // The runtime guard for a union parameter must accept derived builders as well.
        Assert.Contains("// Allowed types for parameter pool: AnyPoolResource, string.", generated, StringComparison.Ordinal);
        Assert.Contains("case AnyPoolResource, string:", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCode_ReturnTypesStayConcreteAndFluent()
    {
        var generated = GenerateFixtureSdk();

        Assert.Equal(
            [
                "AddAksPool(name string) AksPoolResource",
                "AddGpuSpotPool(name string) GpuSpotPoolResource",
                "AddPool(name string) PoolResource",
                "AddQueue(name string) QueueResource",
                "AddSpotPool(name string) SpotPoolResource",
                "AddWorkload(name string) WorkloadResource"
            ],
            GetMethodSignatures(generated, "distributedApplicationBuilder", "Add"));

        // A method declared on the base type is carried by each derived type and keeps returning that derived type.
        Assert.Equal(
            ["WithPoolPeer(peer AnyPoolResource) AksPoolResource"],
            GetMethodSignatures(generated, "aksPoolResource", "With"));
        Assert.Equal(
            ["WithPoolPeer(peer AnyPoolResource) SpotPoolResource"],
            GetMethodSignatures(generated, "spotPoolResource", "With"));
        Assert.Equal(
            ["WithPoolPeer(peer AnyPoolResource) GpuSpotPoolResource"],
            GetMethodSignatures(generated, "gpuSpotPoolResource", "With"));
    }

    [Fact]
    public void GeneratedCode_RealHostingModel_AcceptsDerivedContainerResources()
    {
        var scanResult = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(TestRedisResource).Assembly]);

        var generated = _generator.GenerateDistributedApplication(scanResult.ToAtsContext())["aspire.go"];

        // TestRedisResource derives from ContainerResource, so a Redis builder must satisfy the accepting interface.
        Assert.Matches(
            @"type AnyContainerResource interface \{\r?\n\thandleReference\r?\n\tisContainerResource\(\)\r?\n\}",
            generated);
        Assert.Contains("isContainerResource", GetInterfaceMarkers(generated, "TestRedisResource"));
        Assert.Matches(@"(?m)^func \(\*testRedisResource\) isContainerResource\(\) \{\}", generated);
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedCode_CompilesWhenDerivedBuilderIsPassedWhereBaseIsExpected()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await WriteFixtureSdkAsync(DerivedBuilderFixture.CreateContext(), directory, timeout.Token);
            await WriteConsumerPackageAsync(directory, "consumer", PositiveConsumer, timeout.Token);

            var (exitCode, output) = await RunGoAsync(directory, ["build", "./consumer"], timeout.Token);

            Assert.True(exitCode == 0, $"go build ./consumer failed:{Environment.NewLine}{output}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedCode_RejectsBuildersOutsideTheBaseTypeFamily()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await WriteFixtureSdkAsync(DerivedBuilderFixture.CreateContext(), directory, timeout.Token);
            await WriteConsumerPackageAsync(directory, "negative", NegativeConsumer, timeout.Token);

            // The SDK itself must compile, so the failure below can only come from the consumer.
            var (sdkExitCode, sdkOutput) = await RunGoAsync(directory, ["build", "."], timeout.Token);
            Assert.True(sdkExitCode == 0, $"go build . failed:{Environment.NewLine}{sdkOutput}");

            var (exitCode, output) = await RunGoAsync(directory, ["build", "./negative"], timeout.Token);

            Assert.NotEqual(0, exitCode);

            // A resource of another kind, a base builder where a derived one is required, and an accepting-interface
            // value converted down to a derived accepting interface are all rejected.
            var errors = Regex.Matches(output, @"negative\.go:\d+:\d+: [^\r\n]+")
                .Select(match => match.Value)
                .ToArray();
            Assert.Collection(
                errors,
                error => Assert.Contains("aspire.AnyPoolResource", error, StringComparison.Ordinal),
                error => Assert.Contains("aspire.AnyAksPoolResource", error, StringComparison.Ordinal),
                error =>
                {
                    Assert.Contains("aspire.AnyPoolResource", error, StringComparison.Ordinal);
                    Assert.Contains("aspire.AnyAksPoolResource", error, StringComparison.Ordinal);
                });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedCode_CompilesWhenAnExportedValueRootIsNamedLikeAnAcceptingInterface()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await WriteFixtureSdkAsync(DerivedBuilderFixture.CreateContextWithExportedValueRoot("AnyPoolResource"), directory, timeout.Token);

            // The exported value and the accepting interface are package-level declarations in the same
            // package, so a name shared by both is a redeclaration that only the Go compiler reports.
            var (exitCode, output) = await RunGoAsync(directory, ["build", "."], timeout.Token);

            Assert.True(exitCode == 0, $"go build . failed:{Environment.NewLine}{output}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedCode_CompilesWhenAnEnumMemberConstantIsNamedLikeAnAcceptingInterface()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await WriteFixtureSdkAsync(DerivedBuilderFixture.CreateContextWithEnumMember(), directory, timeout.Token);

            // An enum member constant and the accepting interface are package-level declarations in the
            // same package, so a name shared by both is a redeclaration that only the Go compiler reports.
            var (exitCode, output) = await RunGoAsync(directory, ["build", "."], timeout.Token);

            Assert.True(exitCode == 0, $"go build . failed:{Environment.NewLine}{output}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private const string PositiveConsumer = """
        package consumer

        import "apphost/modules/aspire"

        func Compose(builder aspire.DistributedApplicationBuilder) {
            pool := builder.AddPool("pool")
            aks := builder.AddAksPool("aks")
            spot := builder.AddSpotPool("spot")
            gpu := builder.AddGpuSpotPool("gpu")
            queue := builder.AddQueue("queue")
            workload := builder.AddWorkload("api")

            // A builder of a derived type is accepted wherever the base type is expected.
            workload.WithPool(pool).WithPool(aks).WithPool(spot).WithPool(gpu)
            workload.WithAksPool(aks).WithAksPool(spot).WithAksPool(gpu)
            workload.WithSpotPool(spot).WithSpotPool(gpu)
            workload.WithGpuSpotPool(gpu)
            workload.WithQueue(queue)
            workload.WithPools([]aspire.AnyPoolResource{pool, aks, spot, gpu})
            workload.WithPoolMap(map[string]aspire.AnyPoolResource{"aks": aks})
            workload.WithPoolSettings(&aspire.PoolSettings{Pool: spot, Label: "spot"})
            workload.WithPoolOrName(spot)

            // The fluent API of every type is unchanged: calls keep returning the receiver's own type.
            var _ aspire.GpuSpotPoolResource = gpu.WithPoolPeer(spot).WithPoolPeer(pool)
            var _ aspire.SpotPoolResource = spot.WithPoolPeer(aks).WithPoolPeer(pool)
            var _ aspire.AksPoolResource = aks.WithPoolPeer(spot)
            var _ aspire.PoolResource = pool.WithPoolPeer(spot)
            workload.WithPool(spot.WithPoolPeer(pool))

            ForwardAks(workload, aks)
            ForwardAks(workload, gpu)
            ForwardSpot(workload, spot)
            ForwardSpot(workload, gpu)
        }

        // A function written over a family of builders receives an accepting-interface value. That value
        // must reach the parameters typed with the accepting interface of each ancestor.
        func ForwardAks(workload aspire.WorkloadResource, p aspire.AnyAksPoolResource) {
            workload.WithPool(p)
            workload.WithAksPool(p)
        }

        func ForwardSpot(workload aspire.WorkloadResource, p aspire.AnySpotPoolResource) {
            workload.WithPool(p)
            workload.WithAksPool(p)
            workload.WithSpotPool(p)
            ForwardAks(workload, p)
        }
        """;

    private const string NegativeConsumer = """
        package negative

        import "apphost/modules/aspire"

        func Reject(builder aspire.DistributedApplicationBuilder) {
            queue := builder.AddQueue("queue")
            pool := builder.AddPool("pool")
            workload := builder.AddWorkload("api")

            workload.WithPool(queue)
            workload.WithAksPool(pool)
        }

        // The ancestor markers only widen an accepting interface toward its base types.
        func RejectDownward(workload aspire.WorkloadResource, p aspire.AnyPoolResource) {
            workload.WithAksPool(p)
        }
        """;

    private string GenerateFixtureSdk() =>
        _generator.GenerateDistributedApplication(DerivedBuilderFixture.CreateContext())["aspire.go"];

    private async Task WriteFixtureSdkAsync(AtsContext context, DirectoryInfo directory, CancellationToken cancellationToken)
    {
        foreach (var (name, content) in _generator.GenerateDistributedApplication(context))
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, cancellationToken);
        }
    }

    private static async Task WriteConsumerPackageAsync(DirectoryInfo directory, string packageName, string source, CancellationToken cancellationToken)
    {
        var packageDirectory = directory.CreateSubdirectory(packageName);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory.FullName, $"{packageName}.go"), source, cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunGoAsync(DirectoryInfo workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("go")
        {
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        try
        {
            // Read both streams concurrently so a full pipe buffer cannot stall the compiler.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string[] GetAcceptingInterfaceNames(string generated) =>
        Regex.Matches(generated, @"^type (Any\w+) interface ", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetInterfaceMembers(string generated, string interfaceName)
    {
        var declaration = Regex.Match(generated, $@"^type {interfaceName} interface \{{\r?\n(?<body>.*?)^\}}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(declaration.Success, $"Interface not found: {interfaceName}");

        return declaration.Groups["body"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] GetInterfaceMarkers(string generated, string interfaceName) =>
        GetInterfaceMembers(generated, interfaceName)
            .Where(member => Regex.IsMatch(member, @"^is\w+\(\)$"))
            .Select(member => member[..^2])
            .ToArray();

    private static string[] GetImplMarkers(string generated) =>
        Regex.Matches(generated, @"^func \(\*(\w+)\) (is\w+)\(\) \{\}", RegexOptions.Multiline)
            .Select(match => $"{match.Groups[1].Value}.{match.Groups[2].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] GetMethodSignatures(string generated, string implName, string methodPrefix) =>
        Regex.Matches(generated, $@"^func \(s \*{implName}\) ({methodPrefix}[^\r\n]*) \{{\r?$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static class DerivedBuilderFixture
    {
        private const string PoolId = "Tests.Derived/Tests.Derived.PoolResource";
        private const string AksPoolId = "Tests.Derived/Tests.Derived.AksPoolResource";
        private const string SpotPoolId = "Tests.Derived/Tests.Derived.SpotPoolResource";
        private const string GpuSpotPoolId = "Tests.Derived/Tests.Derived.GpuSpotPoolResource";
        private const string WorkloadId = "Tests.Derived/Tests.Derived.WorkloadResource";
        private const string QueueId = "Tests.Derived/Tests.Derived.QueueResource";
        private const string PoolSettingsId = "Tests.Derived/Tests.Derived.PoolSettings";

        private static readonly AtsTypeRef s_builder = new() { TypeId = AtsConstants.BuilderTypeId, Category = AtsTypeCategory.Handle, IsInterface = true };
        private static readonly AtsTypeRef s_pool = Handle(PoolId);
        private static readonly AtsTypeRef s_aksPool = Handle(AksPoolId);
        private static readonly AtsTypeRef s_spotPool = Handle(SpotPoolId);
        private static readonly AtsTypeRef s_gpuSpotPool = Handle(GpuSpotPoolId);
        private static readonly AtsTypeRef s_workload = Handle(WorkloadId);
        private static readonly AtsTypeRef s_queue = Handle(QueueId);
        private static readonly AtsTypeRef s_string = new() { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive };
        private static readonly AtsTypeRef s_poolSettings = new() { TypeId = PoolSettingsId, Category = AtsTypeCategory.Dto };

        // Pool is the base type; AksPool derives from Pool, SpotPool from AksPool and GpuSpotPool from SpotPool,
        // so the accepting interface of a type has to cover more than one ancestor. Workload consumes pools and
        // Queue is an unrelated resource type.
        public static AtsContext CreateContext() => new()
        {
            Capabilities =
            [
                Factory("addPool", s_pool),
                Factory("addAksPool", s_aksPool),
                Factory("addSpotPool", s_spotPool),
                Factory("addGpuSpotPool", s_gpuSpotPool),
                Factory("addWorkload", s_workload),
                Factory("addQueue", s_queue),

                // Declared on the base type, so each derived type carries it and keeps returning its own type.
                Method("withPoolPeer", s_pool, [s_pool, s_aksPool, s_spotPool, s_gpuSpotPool], [Required("peer", s_pool)], s_pool),

                Consumer("withPool", Required("pool", s_pool)),
                Consumer("withAksPool", Required("pool", s_aksPool)),
                Consumer("withSpotPool", Required("pool", s_spotPool)),
                Consumer("withGpuSpotPool", Required("pool", s_gpuSpotPool)),
                Consumer("withQueue", Required("queue", s_queue)),
                Consumer("withPools", Required("pools", ArrayOf(s_pool))),
                Consumer("withPoolMap", Required("pools", ReadOnlyDictOf(s_string, s_pool))),
                Consumer("withPoolList", Required("pools", ListOf(s_pool))),
                Consumer("withPoolSettings", Required("settings", s_poolSettings)),
                Consumer("withPoolOrName", Required("pool", UnionOf(s_pool, s_string))),
                Consumer("withOptionalPool", Required("name", s_string), Optional("pool", s_pool)),
                Consumer("withPoolCallback", Callback("configure", ("pool", s_pool)))
            ],
            HandleTypes =
            [
                new AtsTypeInfo { AtsTypeId = AtsConstants.BuilderTypeId, IsInterface = true },
                new AtsTypeInfo { AtsTypeId = PoolId },
                new AtsTypeInfo { AtsTypeId = AksPoolId, BaseTypeHierarchy = [s_pool] },
                new AtsTypeInfo { AtsTypeId = SpotPoolId, BaseTypeHierarchy = [s_aksPool, s_pool] },
                new AtsTypeInfo { AtsTypeId = GpuSpotPoolId, BaseTypeHierarchy = [s_spotPool, s_aksPool, s_pool] },
                new AtsTypeInfo { AtsTypeId = WorkloadId },
                new AtsTypeInfo { AtsTypeId = QueueId }
            ],
            EnumTypes = [],
            DtoTypes =
            [
                new AtsDtoTypeInfo
                {
                    TypeId = PoolSettingsId,
                    Name = "PoolSettings",
                    Properties =
                    [
                        new AtsDtoPropertyInfo { Name = "Pool", Type = s_pool },
                        new AtsDtoPropertyInfo { Name = "Label", Type = s_string }
                    ]
                }
            ]
        };

        // Pool stays referenced by capabilities but is not listed in HandleTypes.
        public static AtsContext CreateContextWithoutPoolHandleType()
        {
            var context = CreateContext();
            return new AtsContext
            {
                Capabilities = context.Capabilities,
                HandleTypes = context.HandleTypes.Where(type => type.AtsTypeId != PoolId).ToList(),
                EnumTypes = context.EnumTypes,
                DtoTypes = context.DtoTypes
            };
        }

        // An exported value is emitted as a package-level var named after its catalog root.
        public static AtsContext CreateContextWithExportedValueRoot(string rootName)
        {
            var context = CreateContext();
            return new AtsContext
            {
                Capabilities = context.Capabilities,
                HandleTypes = context.HandleTypes,
                EnumTypes = context.EnumTypes,
                DtoTypes = context.DtoTypes,
                ExportedValues =
                [
                    new AtsExportedValueInfo
                    {
                        OwningAssemblyName = "Tests.Derived",
                        PathSegments = [rootName, "Default"],
                        Value = JsonValue.Create("default"),
                        Type = s_string
                    }
                ]
            };
        }

        // An enum member is emitted as a package-level const named after the enum and the member, so
        // AnyPool.Resource becomes AnyPoolResource.
        public static AtsContext CreateContextWithEnumMember()
        {
            var context = CreateContext();
            return new AtsContext
            {
                Capabilities = context.Capabilities,
                HandleTypes = context.HandleTypes,
                EnumTypes =
                [
                    new AtsEnumTypeInfo
                    {
                        TypeId = "enum:Tests.Derived.AnyPool",
                        Name = nameof(AnyPool),
                        ClrType = typeof(AnyPool),
                        Values = [nameof(AnyPool.Resource)]
                    }
                ],
                DtoTypes = context.DtoTypes
            };
        }

        private static AtsTypeRef Handle(string typeId) => new() { TypeId = typeId, Category = AtsTypeCategory.Handle };

        private static AtsTypeRef ArrayOf(AtsTypeRef element) => new()
        {
            TypeId = AtsConstants.ArrayTypeId(element.TypeId),
            Category = AtsTypeCategory.Array,
            ElementType = element,
            IsReadOnly = true
        };

        private static AtsTypeRef ListOf(AtsTypeRef element) => new()
        {
            TypeId = AtsConstants.ListTypeId(element.TypeId),
            Category = AtsTypeCategory.List,
            ElementType = element
        };

        private static AtsTypeRef ReadOnlyDictOf(AtsTypeRef key, AtsTypeRef value) => new()
        {
            TypeId = AtsConstants.DictTypeId(key.TypeId, value.TypeId),
            Category = AtsTypeCategory.Dict,
            KeyType = key,
            ValueType = value,
            IsReadOnly = true
        };

        private static AtsTypeRef UnionOf(params AtsTypeRef[] members) => new()
        {
            TypeId = string.Join("|", members.Select(member => member.TypeId)),
            Category = AtsTypeCategory.Union,
            UnionTypes = members
        };

        private static AtsParameterInfo Required(string name, AtsTypeRef type) => new() { Name = name, Type = type };

        private static AtsParameterInfo Optional(string name, AtsTypeRef type) => new() { Name = name, Type = type, IsOptional = true, IsNullable = true };

        private static AtsParameterInfo Callback(string name, params (string Name, AtsTypeRef Type)[] parameters) => new()
        {
            Name = name,
            Type = new AtsTypeRef { TypeId = "callback", Category = AtsTypeCategory.Callback },
            IsCallback = true,
            CallbackParameters = parameters.Select(parameter => new AtsCallbackParameterInfo { Name = parameter.Name, Type = parameter.Type }).ToArray(),
            CallbackReturnType = new AtsTypeRef { TypeId = AtsConstants.Void, Category = AtsTypeCategory.Primitive }
        };

        private static AtsCapabilityInfo Factory(string methodName, AtsTypeRef returnType) =>
            Method(methodName, s_builder, [s_builder], [Required("name", s_string)], returnType);

        private static AtsCapabilityInfo Consumer(string methodName, params AtsParameterInfo[] parameters) =>
            Method(methodName, s_workload, [s_workload], parameters, s_workload);

        private static AtsCapabilityInfo Method(
            string methodName,
            AtsTypeRef target,
            IReadOnlyList<AtsTypeRef> expandedTargets,
            IReadOnlyList<AtsParameterInfo> parameters,
            AtsTypeRef returnType) => new()
        {
            CapabilityId = $"Tests.Derived/{methodName}",
            MethodName = methodName,
            TargetTypeId = target.TypeId,
            TargetType = target,
            TargetParameterName = "builder",
            ExpandedTargetTypes = expandedTargets,
            Parameters = parameters,
            ReturnType = returnType
        };

        private enum AnyPool
        {
            Resource
        }
    }
}
