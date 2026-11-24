using March7thHoney.GameServer.Server.Packet.Send.Mission;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Recv.Mission;

[Opcode(CmdIds.FinishTalkMissionCsReq)]
public class HandlerFinishTalkMissionCsReq : Handler<FinishTalkMissionCsReq>
{
    protected override async Task OnHandle(Connection connection, PlayerInstance player, FinishTalkMissionCsReq req)
    {
        var hasCustomValues = req.CustomValueList != null && req.CustomValueList.Count > 0;
        if (hasCustomValues)
            player.MissionManager!.StoreCustomValues([.. req.CustomValueList!], (int)req.SubMissionId);

        // A talk string that matches no gate (e.g. a plain NPC dialogue reporting "0") is ACK-only, as on the
        // official server: finishing a gate the client never reported tears down its running performance.
        var handled = await player.MissionManager!.HandleTalkStr(req.TalkStr);
        if (!handled && string.IsNullOrEmpty(req.TalkStr) && req.SubMissionId > 0)
        {
            var subInfo = player.MissionManager.GetSubMissionInfo((int)req.SubMissionId);
            if (subInfo is { FinishType: Enums.Mission.MissionFinishTypeEnum.Talk } &&
                player.MissionManager.GetSubMissionStatus((int)req.SubMissionId) == Enums.Mission.MissionPhaseEnum.Accept)
                await player.MissionManager.FinishSubMission((int)req.SubMissionId);
        }

        if (hasCustomValues)
            await player.MissionManager!.HandleCustomValue([.. req.CustomValueList!], (int)req.SubMissionId);

        await connection.SendPacket(new PacketFinishTalkMissionScRsp(req));
    }
}
