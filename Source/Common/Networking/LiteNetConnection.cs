using LiteNetLib;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class LiteNetConnection(NetPeer peer) : ConnectionBase
    {
        public readonly NetPeer peer = peer;

        protected override void SendRaw(byte[] raw, bool reliable)
        {
            if (peer.ConnectionState == ConnectionState.Connected)
                peer.Send(raw, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            else
                ServerLog.Error($"SendRaw() called with invalid connection state ({peer}): {peer.ConnectionState}");
        }

        // Fragments are 1 KB and a bigger reliable packet goes in MTU-sized parts, so a count of queued parts times
        // the MTU is near enough
        public override long ReliableBacklogBytes =>
            peer.ConnectionState == ConnectionState.Connected ? (long)peer.GetPacketsCountInReliableQueue(0, true) * peer.Mtu : 0;

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            if (goodbye.HasValue)
                peer.Disconnect(goodbye.Value.Serialize().data);
            else
                peer.Disconnect();
        }

        public override void OnKeepAliveArrived(bool idMatched)
        {
            // Latency already handled by LiteNetLib. This can be as low as 0ms because LNL spawns its own thread for
            // receiving packets and immediately processes its own internal keep alive packet (called Ping-Pong).
            // This is handled only on the server-side in MpServerNetListener
        }

        public override string ToString()
        {
            return $"NetConnection ({peer}) ({username})";
        }
    }
}
