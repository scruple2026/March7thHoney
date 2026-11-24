using March7thHoney.GameServer.Server.Packet.Send.Mission;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Mission;

[Opcode(CmdIds.SyncTaskCsReq)]
public class HandlerSyncTaskCsReq : Handler<SyncTaskCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player, SyncTaskCsReq req)
    {
        await connection.SendPacket(new PacketMissionSyncTaskScRsp(req.AFBDHEMJEPP ?? string.Empty));
    }
}
