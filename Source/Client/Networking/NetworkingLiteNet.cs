using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Networking
{
    public class ClientLiteNetConnection : LiteNetConnection, ITickableConnection
    {
        private readonly NetManager netManager;

        private ClientLiteNetConnection(NetPeer peer, NetManager netManager) : base(peer) =>
            this.netManager = netManager;

        ~ClientLiteNetConnection()
        {
            if (netManager.IsRunning)
            {
                Log.Error("[ClientLiteNetConnection] NetManager did not get stopped");
                netManager.Stop();
            }
        }

        public static ClientLiteNetConnection Connect(string address, int port, string username)
        {
            var netClient = new NetManager(new NetListener(username))
            {
                EnableStatistics = true,
                IPv6Enabled = MpUtil.SupportsIPv6(),
                ReconnectDelay = 300,
                MaxConnectAttempts = 8,
                DisconnectTimeout = LiteNetManager.DisconnectTimeoutMs
            };
            netClient.Start();
            var peer = netClient.Connect(address, port, "");
            var conn = new ClientLiteNetConnection(peer, netClient);
            peer.SetConnection(conn);
            return conn;
        }

        public void Tick()
        {
            netManager.PollEvents();
            NetListener.ReleaseDelayed();
        }

        public void OnDisconnect(SessionDisconnectInfo disconnectInfo)
        {
            if (State == ConnectionStateEnum.Disconnected) return;
            ConnectionStatusListeners.TryNotifyAll_Disconnected(disconnectInfo);
            Multiplayer.StopMultiplayer();
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            base.OnClose(goodbye);
            netManager.Stop();
        }

        private class NetListener(string username) : INetEventListener
        {
            private ClientLiteNetConnection GetConnection(NetPeer peer) =>
                peer.GetConnection() as ClientLiteNetConnection ?? throw new Exception("Can't get connection");

            public void OnPeerConnected(NetPeer peer)
            {
                var conn = GetConnection(peer);
                conn.ChangeState(new ClientJoiningState(conn, username));
                MpLog.Log("Net client connected");
            }

            public void OnNetworkError(IPEndPoint endPoint, SocketError error)
            {
                MpLog.Warn($"Net client error {error}");
            }

            public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod method)
            {
                byte[] data = reader.GetRemainingBytes();
                bool reliable = method == DeliveryMethod.ReliableOrdered;
                if (reliable && ReliableDelayMs > 0)
                {
                    if (delayedCount++ == 0)
                        Log.Message($"MP_SIM_RELIABLE_DELAY: holding reliable packets back {ReliableDelayMs} ms");
                    delayedReliable.Enqueue((ReliableDelay.ElapsedMilliseconds + ReliableDelayMs, GetConnection(peer), data));
                    return;
                }

                GetConnection(peer).HandleReceiveRaw(new ByteReader(data), reliable);
            }

            /// <summary>
            /// Testing: MP_SIM_RELIABLE_DELAY=ms holds reliable packets (commands) back that long while unreliable
            /// ones (time control) go through, so they arrive out of order as they can over a real connection; the
            /// local harness on 127.0.0.1 never reorders them.
            /// </summary>
            private static readonly int ReliableDelayMs =
                int.TryParse(Environment.GetEnvironmentVariable("MP_SIM_RELIABLE_DELAY"), out var ms) ? ms : 0;
            private static int delayedCount;
            private static readonly System.Diagnostics.Stopwatch ReliableDelay = System.Diagnostics.Stopwatch.StartNew();
            private static readonly System.Collections.Generic.Queue<(long due, ConnectionBase conn, byte[] data)> delayedReliable = new();

            public static void ReleaseDelayed()
            {
                while (delayedReliable.Count > 0 && delayedReliable.Peek().due <= ReliableDelay.ElapsedMilliseconds)
                {
                    var (_, conn, data) = delayedReliable.Dequeue();
                    conn.HandleReceiveRaw(new ByteReader(data), true);
                }
            }

            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
            {
                MpDisconnectReason reason;
                ByteReader reader;

                if (info.AdditionalData.EndOfData)
                {
                    if (info.Reason is DisconnectReason.DisconnectPeerCalled or DisconnectReason.RemoteConnectionClose)
                        reason = MpDisconnectReason.Generic;
                    else if (Multiplayer.Client == null)
                        reason = MpDisconnectReason.ConnectingFailed;
                    else
                        reason = MpDisconnectReason.NetFailed;

                    reader = new ByteReader(ByteWriter.GetBytes(info.Reason));
                }
                else
                {
                    reader = new ByteReader(info.AdditionalData.GetRemainingBytes());
                    reason = reader.ReadEnum<MpDisconnectReason>();
                }

                GetConnection(peer).OnDisconnect(SessionDisconnectInfo.From(reason, reader));
                MpLog.Log($"Net client disconnected {info.Reason}");
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
            }

            public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
            }
        }
    }

    public class LiteNetLogger : INetLogger
    {
        public static void Install() => NetDebug.Logger = new LiteNetLogger();

        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            if (level == NetLogLevel.Error)
                ServerLog.Error(string.Format(str, args));
            else
                ServerLog.Log(string.Format(str, args));
        }
    }
}
