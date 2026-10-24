using System.Collections.Generic;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class RecordingConnection : ConnectionBase
{
    public List<Packets> SentPackets { get; } = new();

    /// <summary>Every packet sent, its id byte included, for tests that read what was sent.</summary>
    public List<byte[]> SentRaw { get; } = new();

    public RecordingConnection(string username)
    {
        this.username = username;
    }

    public override int Latency { get => 0; set { } }

    protected override void SendRaw(byte[] raw, bool reliable)
    {
        if (raw.Length == 0)
            return;

        SentPackets.Add((Packets)(raw[0] & 0x3F));
        SentRaw.Add(raw);
    }

    /// <summary>The packets of one type sent so far, read back.</summary>
    public List<T> Sent<T>(Packets type) where T : struct, IPacket
    {
        var result = new List<T>();
        foreach (var raw in SentRaw)
        {
            if ((Packets)(raw[0] & 0x3F) != type) continue;
            var packet = new T();
            packet.Bind(new PacketReader(new ByteReader(raw[1..])));
            result.Add(packet);
        }
        return result;
    }

    protected override void OnClose(ServerDisconnectPacket? goodbye) { }
}