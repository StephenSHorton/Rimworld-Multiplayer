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
    public void OpinionsWaitUpToACapAndOtherDiagnosticsAreKept()
    {
        var conn = new BackedUpConnection { backlog = ConnectionBase.LowPriorityBacklogLimit };

        conn.SendLowPriority(ServerTracesPacket.Transfer([1], [2]).Serialize());
        // A joiner's world download in front of them: a minute of the host's opinions at Ultrafast all wait
        for (int i = 0; i < 800; i++)
            conn.SendLowPriority(Opinion(300), droppable: true);
        Assert.That(conn.lowPriorityDropped, Is.EqualTo(0));

        // Past the cap the oldest opinions go, never the traces
        int perOpinion = Opinion(300).data.Length;
        int fit = (int)(ConnectionBase.MaxDroppableQueuedBytes / perOpinion);
        for (int i = 0; i < fit; i++)
            conn.SendLowPriority(Opinion(300), droppable: true);
        Assert.That(conn.lowPriorityDropped, Is.EqualTo(800));
        Assert.That(conn.LowPriorityQueued, Is.EqualTo(1 + fit));

        conn.backlog = 0;
        conn.FlushLowPriority();
        Assert.That(conn.SentPackets[0], Is.EqualTo(Packets.Server_Traces));
        Assert.That(conn.SentPackets.Count(p => p == Packets.Server_SyncInfo), Is.EqualTo(fit));
    }

    [Test]
    public void APacketQueuedBeforeTheConnectionChangedStateIsDropped()
    {
        // ServerSteam is unused, so the test can give it a state of its own
        MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(IdleState));
        var conn = new BackedUpConnection { backlog = ConnectionBase.LowPriorityBacklogLimit };
        conn.ChangeState(ConnectionStateEnum.ServerSteam);
        conn.SendLowPriority(Opinion());
        conn.SendLowPriority(Opinion());

        // A resync leaves Playing and comes back to it between two flushes: the same state by name, a new one all
        // the same, which may not expect what was queued for the old
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
