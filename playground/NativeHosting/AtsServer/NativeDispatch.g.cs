// GENERATED from the real ATS scanner. Do not edit.
#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace NativeHosting;
internal static class NativeDispatch
{
    public static async Task<JsonNode?> InvokeAsync(AtsSession session, string capability, JsonObject args, CancellationToken token)
    {
        switch (capability)
        {
            case "NativeHosting.Ats/createNativeBuilder":
            {
                var result = NativeApi.CreateNativeBuilder();
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeBuilder");
            }
            case "NativeHosting/addResource":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = target.AddResource(AtsSession.String(args["name"], "name"), AtsSession.String(args["kind"], "kind"), args["options"]!.Deserialize(DispatchJsonContext.Default.ResourceOptions)!);
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/command":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = await target.Command(AtsSession.String(args["command"], "command"), session.Token(args["cancellationToken"], token));
                return JsonValue.Create(result);
            }
            case "NativeHosting/computeStatus":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = await target.ComputeStatus(session.Token(args["cancellationToken"], token));
                return JsonSerializer.SerializeToNode(result, DispatchJsonContext.Default.ResourceState);
            }
            case "NativeHosting/concat":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = target.Concat(args["values"]!.AsArray().Select(item => session.Get<NativeValue>(item)).ToArray());
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeValue");
            }
            case "NativeHosting/getEndpoint":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.GetEndpoint(AtsSession.String(args["endpoint"], "endpoint"), AtsSession.String(args["property"], "property"));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeValue");
            }
            case "NativeHosting/getParameter":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.GetParameter(args["uriEscape"]!.GetValue<bool>());
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeValue");
            }
            case "NativeHosting/getProperty":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.GetProperty(AtsSession.String(args["name"], "name"));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeValue");
            }
            case "NativeHosting/literal":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = target.Literal(AtsSession.String(args["value"], "value"));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeValue");
            }
            case "NativeHosting/publish":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = target.Publish();
                return JsonValue.Create(result);
            }
            case "NativeHosting/readLogs":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = await target.ReadLogs(args["stdoutOffset"]!.GetValue<long>(), args["stderrOffset"]!.GetValue<long>(), session.Token(args["cancellationToken"], token));
                return JsonSerializer.SerializeToNode(result, DispatchJsonContext.Default.ExecutableLogs);
            }
            case "NativeHosting/resolve":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = await target.Resolve(AtsSession.String(args["property"], "property"), AtsSession.String(args["mode"], "mode"), AtsSession.String(args["network"], "network"), session.Token(args["cancellationToken"], token));
                return JsonValue.Create(result);
            }
            case "NativeHosting/run":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = await target.Run(session.Token(args["cancellationToken"], token));
                return JsonValue.Create(result);
            }
            case "NativeHosting/stats":
            {
                var target = session.Get<NativeBuilder>(args["context"]);
                var result = await target.Stats(session.Token(args["cancellationToken"], token));
                return JsonSerializer.SerializeToNode(result, DispatchJsonContext.Default.CoreStats);
            }
            case "NativeHosting/status":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = await target.Status(session.Token(args["cancellationToken"], token));
                return JsonSerializer.SerializeToNode(result, DispatchJsonContext.Default.ResourceState);
            }
            case "NativeHosting/updateCustom":
            {
                var target = session.Get<NativeResource>(args["context"]);
                session.EnsureController(target);
                var result = await target.UpdateCustom(args["update"]!.Deserialize(DispatchJsonContext.Default.CustomUpdate)!, session.Token(args["cancellationToken"], token));
                return JsonValue.Create(result);
            }
            case "NativeHosting/waitFor":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.WaitFor(session.Get<NativeResource>(args["dependency"]));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withArgument":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.WithArgument(session.Union(args["value"]));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withBeforeStart":
            {
                var target = session.Get<NativeResource>(args["context"]);
                session.TrackCallback(AtsSession.String(args["callback"], "callback"));
                var result = target.WithBeforeStart((p0, p1) => session.CallbackAsync(AtsSession.String(args["callback"], "callback"), new JsonObject { ["p0"] = session.Marshal(p0, "NativeHosting.Ats/NativeHosting.NativeResource") }, p1));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withControl":
            {
                var target = session.Get<NativeResource>(args["context"]);
                session.TrackCallback(AtsSession.String(args["callback"], "callback"));
                var result = target.WithControl((p0, p1, p2) => session.CallbackAsync(AtsSession.String(args["callback"], "callback"), new JsonObject { ["p0"] = session.Marshal(p0, "NativeHosting.Ats/NativeHosting.NativeResource"), ["p1"] = JsonSerializer.SerializeToNode(p1, DispatchJsonContext.Default.ControlRequest) }, p2));
                session.ClaimController(target);
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withEnvironment":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.WithEnvironment(AtsSession.String(args["name"], "name"), session.Union(args["value"]));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withHealth":
            {
                var target = session.Get<NativeResource>(args["context"]);
                session.TrackCallback(AtsSession.String(args["callback"], "callback"));
                var result = target.WithHealth((p0, p1) => session.CallbackAsync(AtsSession.String(args["callback"], "callback"), new JsonObject { ["p0"] = session.Marshal(p0, "NativeHosting.Ats/NativeHosting.NativeResource") }, p1));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withInitialize":
            {
                var target = session.Get<NativeResource>(args["context"]);
                session.TrackCallback(AtsSession.String(args["callback"], "callback"));
                var result = target.WithInitialize((p0, p1) => session.CallbackAsync(AtsSession.String(args["callback"], "callback"), new JsonObject { ["p0"] = session.Marshal(p0, "NativeHosting.Ats/NativeHosting.NativeResource") }, p1));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withParent":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.WithParent(session.Get<NativeResource>(args["parent"]));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            case "NativeHosting/withProperty":
            {
                var target = session.Get<NativeResource>(args["context"]);
                var result = target.WithProperty(AtsSession.String(args["name"], "name"), session.Union(args["value"]));
                return session.Marshal(result, "NativeHosting.Ats/NativeHosting.NativeResource");
            }
            default: throw new AtsFault("CAPABILITY_NOT_FOUND", $"Unknown ATS capability '{capability}'.");
        }
    }
}
internal static class ExternalDispatch
{
    public static bool Contains(string capability) => capability is "NativeHosting.Ats/addRedis" or "NativeHosting.Ats/addPostgres" or "NativeHosting.Ats/addDatabase" or "NativeHosting.Ats/addNuxt" or "NativeHosting.Ats/addDevTunnel" or "NativeHosting.Ats/redisCommand" or "NativeHosting.Ats/query" or "NativeHosting.Ats/releaseGraph";
}
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(CoreStats))]
[JsonSerializable(typeof(CustomUpdate))]
[JsonSerializable(typeof(ExecutableLogs))]
[JsonSerializable(typeof(ResourceOptions))]
[JsonSerializable(typeof(ResourceState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DispatchJsonContext : JsonSerializerContext;
