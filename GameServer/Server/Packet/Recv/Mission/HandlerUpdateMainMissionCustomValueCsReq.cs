using March7thHoney.GameServer.Server.Packet.Send.Mission;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Mission;

[Opcode(CmdIds.UpdateMainMissionCustomValueCsReq)]
public class HandlerUpdateMainMissionCustomValueCsReq : Handler<UpdateMainMissionCustomValueCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player,
        UpdateMainMissionCustomValueCsReq req)
    {
        var mainProto = await player.MissionManager!.UpdateMainMissionCustomValue(
            (int)req.MainMissionId, req.BGDFHBAANLB ?? string.Empty, req.Value);
        await connection.SendPacket(new PacketUpdateMainMissionCustomValueScRsp(mainProto));
    }
}
