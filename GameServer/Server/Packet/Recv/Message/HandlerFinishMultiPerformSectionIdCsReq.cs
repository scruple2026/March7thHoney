using March7thHoney.GameServer.Server.Packet.Send.Message;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Message;

[Opcode(CmdIds.FinishMultiPerformSectionIdCsReq)]
public class HandlerFinishMultiPerformSectionIdCsReq : Handler<FinishMultiPerformSectionIdCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player,
        FinishMultiPerformSectionIdCsReq req)
    {
        foreach (var payload in req.CGKBNIMCFKH)
        {
            var sectionId = (int)(payload?.MGPGDLIJKLM ?? 0);
            if (sectionId != 0 && player.MessageManager?.GetMessageSectionData(sectionId) != null)
                await player.MessageManager.FinishSection(sectionId);
        }

        await connection.SendPacket(new PacketFinishMultiPerformSectionIdScRsp(req.CGKBNIMCFKH));
    }
}
