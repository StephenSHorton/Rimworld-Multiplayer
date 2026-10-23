using System;
using Multiplayer.Client.Networking;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Client;

public abstract class ClientBaseState(ConnectionBase connection) : MpConnectionState(connection)
{
    protected MultiplayerSession Session => Multiplayer.session;

    [TypedPacketHandler]
    public void HandleKeepAlive(ServerKeepAlivePacket packet)
    {
        // Behind the limit the server last sent, not the one this game runs to: that one waits until every command
        // the server counted has arrived, so with the reliable channel stuck this game stopped and still reported
        // being 0 ticks behind, and the server never paused for it (2026-10-22)
        int ticksBehind = Math.Max(TickPatch.tickUntil, Multiplayer.session?.remoteTickUntil ?? 0) - TickPatch.Timer;

        connection.Send(new ClientKeepAlivePacket(packet.id, ticksBehind, TickPatch.Simulating, TickPatch.workTicks),
            false);
    }

    [TypedPacketHandler]
    public void HandleTimeControl(ServerTimeControlPacket packet)
    {
        if (Multiplayer.session.remoteTickUntil >= packet.tickUntil) return;

        TickPatch.serverTimePerTick = packet.serverTimePerTick;
        Multiplayer.session.remoteTickUntil = packet.tickUntil;
        Multiplayer.session.remoteSentCmds = packet.sentCmds;
        Multiplayer.session.ProcessTimeControl();
    }

    // Currently handles disconnection only for Steam connections. See comment in ConnectionBase.Close for more info.
    [TypedPacketHandler]
    public virtual void HandleDisconnected(ServerDisconnectPacket packet)
    {
        ConnectionStatusListeners.TryNotifyAll_Disconnected(SessionDisconnectInfo.From(packet));
        Multiplayer.StopMultiplayer();
    }
}
