using March7thHoney.GameServer.Server.Packet.Send.Scene;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Scene;

[Opcode(CmdIds.GroupStateChangeCsReq)]
public class HandlerGroupStateChangeCsReq : Handler<GroupStateChangeCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player, GroupStateChangeCsReq req)
    {
        await connection.SendPacket(new PacketGroupStateChangeScRsp(req.GroupStateInfo));
    }
}
