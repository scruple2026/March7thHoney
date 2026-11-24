using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Scene;

public class PacketGroupStateChangeScRsp : BasePacket
{
    public PacketGroupStateChangeScRsp(GroupStateInfo? info) : base(CmdIds.GroupStateChangeScRsp)
    {
        var rsp = new GroupStateChangeScRsp();
        if (info != null) rsp.GroupStateInfo = info.Clone();
        SetData(rsp);
    }
}
