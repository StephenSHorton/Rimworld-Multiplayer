using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class LowPriorityTest
{
    private class BackedUpConnection : RecordingConnection
    {
        public long backlog;

        public BackedUpConnection() : base("Blake")
        {
        }

        public override long ReliableBacklogBytes => backlog;
    }

    private class IdleState(ConnectionBase connection) : MpConnectionState(connection);

    private static SerializedPacket Opinion(int size = 200) => new ServerSyncInfoPacket { rawSyncOpinion = new byte[size] }.Serialize();

    [Test]
    public void DiagnosticsWaitWhileTheReliableStreamIsBackedUp()
    {
        var conn = new BackedUpConnection { backlog = ConnectionBase.LowPriorityBacklogLimit };

        conn.SendLowPriority(Opinion());
        conn.Send(Packets.Server_KeepAlive);
        Assert.That(conn.SentPackets, Is.EqualTo(new[] { Packets.Server_KeepAlive }));
        Assert.That(conn.LowPriorityQueued, Is.EqualTo(1));

        conn.backlog = 0;
        conn.FlushLowPriority();
        Assert.That(conn.SentPackets, Is.EqualTo(new[] { Packets.Server_KeepAlive, Packets.Server_SyncInfo }));
        Assert.That(conn.LowPriorityQueued, Is.EqualTo(0));
    }

    [Test]
    public void OnlyTheNewestOpinionsWaitAndOtherDiagnosticsAreKept()
    {
        var conn = new BackedUpConnection { backlog = ConnectionBase.LowPriorityBacklogLimit };

        conn.SendLowPriority(ServerTracesPacket.Transfer([1], [2]).Serialize());
        for (int i = 0; i < ConnectionBase.MaxDroppableQueued + 3; i++)
            conn.SendLowPriority(Opinion(), droppable: true);

        Assert.That(conn.LowPriorityQueued, Is.EqualTo(1 + ConnectionBase.MaxDroppableQueued));
        Assert.That(conn.lowPriorityDropped, Is.EqualTo(3));

        conn.backlog = 0;
        conn.FlushLowPriority();
        Assert.That(conn.SentPackets[0], Is.EqualTo(Packets.Server_Traces));
        Assert.That(conn.SentPackets.Count(p => p == Packets.Server_SyncInfo), Is.EqualTo(ConnectionBase.MaxDroppableQueued));
    }

    [Test]
    public void APacketQueuedBeforeTheConnectionChangedStateIsDropped()
    {
        var conn = new BackedUpConnection { backlog = ConnectionBase.LowPriorityBacklogLimit };
        conn.SendLowPriority(Opinion());
        conn.SendLowPriority(Opinion());

        // ServerSteam is unused, so the test can give it a state of its own
        MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(IdleState));
        conn.ChangeState(ConnectionStateEnum.ServerSteam);
        conn.backlog = 0;
        conn.FlushLowPriority();
        Assert.That(conn.SentPackets, Is.Empty);
        Assert.That(conn.LowPriorityQueued, Is.EqualTo(0));
        Assert.That(conn.lowPriorityDropped, Is.EqualTo(2));
    }

    [Test]
    public void ABigDiagnosticGoesOutInFragmentsOnceThereIsRoom()
    {
        var conn = new BackedUpConnection();
        conn.SendLowPriority(Opinion(5000));
        Assert.That(conn.SentPackets.Count, Is.GreaterThan(1));
        Assert.That(conn.SentPackets.Distinct(), Is.EqualTo(new[] { Packets.Server_SyncInfo }));
    }
}
