using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Plot;

public class PacketFinishPlotScRsp : BasePacket
{
    public PacketFinishPlotScRsp(uint plotId) : base(CmdIds.FinishPlotScRsp)
    {
        SetData(new FinishPlotScRsp
        {
            CAMCBJPOMLC = plotId
        });
    }
}
