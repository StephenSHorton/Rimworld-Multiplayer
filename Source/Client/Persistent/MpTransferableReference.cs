using Multiplayer.API;
using RimWorld;

namespace Multiplayer.Client.Persistent;

public class MpTransferableReference
{
    public ISessionWithTransferables session;
    public Transferable transferable;

    public MpTransferableReference(ISessionWithTransferables session, Transferable transferable)
    {
        this.session = session;
        this.transferable = transferable;
    }

    public int CountToTransfer
    {
        get => transferable?.CountToTransfer ?? 0;
        set { if (transferable != null) transferable.CountToTransfer = value; }
    }

    public override int GetHashCode() => transferable?.GetHashCode() ?? 0;
    public override bool Equals(object obj) => obj is MpTransferableReference tr && tr.transferable == transferable;
}
