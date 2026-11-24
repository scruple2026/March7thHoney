using March7thHoney.GameServer.Server.Packet.Send.Plot;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Plot;

[Opcode(CmdIds.FinishPlotCsReq)]
public class HandlerFinishPlotCsReq : Handler<FinishPlotCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player, FinishPlotCsReq req)
    {
        await connection.SendPacket(new PacketFinishPlotScRsp(req.CAMCBJPOMLC));
    }
}
