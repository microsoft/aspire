// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

#pragma warning disable ASPIREMONGODB001

namespace Aspire.Hosting.MongoDB.Tests;

public class MongoDBReplicaSetInitiationTests
{
    private static readonly BsonArray s_members =
    [
        new BsonDocument { ["_id"] = 0, ["host"] = "mongo1:27017" },
        new BsonDocument { ["_id"] = 1, ["host"] = "mongo2:27017" },
        new BsonDocument { ["_id"] = 2, ["host"] = "mongo3:27017" },
    ];

    [Fact]
    public async Task InitiationWaitsForTheInitialMemberToBecomeWritablePrimaryBeforeReconfiguring()
    {
        // The initiated member keeps reporting that it is not yet a writable primary for a few probes, and interrupts a
        // reconfiguration sent before then with `InterruptedDueToReplStateChange`, like a real `mongod` stepping up does.
        var admin = SteppingUpMember.Create(probesBeforePrimary: 3);

        await MongoDBReplicaSetBuilderExtensions.InitiateReplicaSetAsync(admin, "rs0", s_members, CancellationToken.None);

        var member = (SteppingUpMember)(object)admin;
        Assert.Equal(["replSetInitiate", "hello", "hello", "hello", "replSetReconfig"], member.Commands.Select(c => c.GetElement(0).Name));

        var initiate = member.Commands[0]["replSetInitiate"].AsBsonDocument;
        Assert.Equal("rs0", initiate["_id"].AsString);
        Assert.Equal(new BsonArray([s_members[0]]), initiate["members"].AsBsonArray);

        var reconfig = member.Commands[^1];
        Assert.True(reconfig["force"].AsBoolean);
        Assert.Equal(2, reconfig["replSetReconfig"]["version"].AsInt32);
        Assert.Equal(s_members, reconfig["replSetReconfig"]["members"].AsBsonArray);
    }

    [Fact]
    public async Task InitiationStopsWaitingForPrimaryWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var admin = SteppingUpMember.Create(probesBeforePrimary: int.MaxValue, onProbe: probe =>
        {
            if (probe == 2)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MongoDBReplicaSetBuilderExtensions.InitiateReplicaSetAsync(admin, "rs0", s_members, cancellation.Token));

        var member = (SteppingUpMember)(object)admin;
        Assert.DoesNotContain(member.Commands, c => c.GetElement(0).Name == "replSetReconfig");
    }

    public class SteppingUpMember : DispatchProxy
    {
        private int _probesBeforePrimary;
        private int _probes;
        private bool _initiated;
        private Action<int>? _onProbe;

        public List<BsonDocument> Commands { get; } = [];

        public static IMongoDatabase Create(int probesBeforePrimary, Action<int>? onProbe = null)
        {
            var admin = Create<IMongoDatabase, SteppingUpMember>();
            var member = (SteppingUpMember)(object)admin;
            member._probesBeforePrimary = probesBeforePrimary;
            member._onProbe = onProbe;
            return admin;
        }

        private bool IsWritablePrimary => _initiated && _probes >= _probesBeforePrimary;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not { Name: nameof(IMongoDatabase.RunCommandAsync) } || args is not [BsonDocumentCommand<BsonDocument> command, _, CancellationToken cancellationToken])
            {
                throw new NotSupportedException($"Unexpected call to '{targetMethod?.Name}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command.Document);

            switch (command.Document.GetElement(0).Name)
            {
                case "replSetInitiate":
                    _initiated = true;
                    return Task.FromResult(new BsonDocument("ok", 1));

                case "hello":
                    _probes++;
                    _onProbe?.Invoke(_probes);
                    return Task.FromResult(new BsonDocument { ["isWritablePrimary"] = IsWritablePrimary, ["ok"] = 1 });

                case "replSetReconfig" when !IsWritablePrimary:
                    var result = new BsonDocument { ["ok"] = 0, ["code"] = 11602, ["codeName"] = "InterruptedDueToReplStateChange" };
                    var connectionId = new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("mongo1", 27017)));
                    return Task.FromException<BsonDocument>(new MongoNodeIsRecoveringException(connectionId, command.Document, result));

                case "replSetReconfig":
                    return Task.FromResult(new BsonDocument("ok", 1));

                default:
                    throw new NotSupportedException($"Unexpected command '{command.Document}'.");
            }
        }
    }
}
