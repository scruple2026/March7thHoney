using March7thHoney.Enums.Mission;
using March7thHoney.GameServer.Game.Mission;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Mission;

public class PacketGetMainMissionCustomValueScRsp : BasePacket
{
    public PacketGetMainMissionCustomValueScRsp(GetMainMissionCustomValueCsReq req, PlayerInstance player) : base(
        CmdIds.GetMainMissionCustomValueScRsp)
    {
        var proto = new GetMainMissionCustomValueScRsp();
        foreach (var mission in req.MainMissionIdList)
        {
            var mainProto = new MainMission
            {
                Id = mission,
                Status = player.MissionManager!.GetMainMissionStatus((int)mission).ToProto()
            };
            if (player.MissionManager.Data.MainMissionCustomValues.TryGetValue((int)mission, out var records))
                MissionSyncBuilder.FillMainCustomValues(mainProto, records);
            proto.MainMissionList.Add(mainProto);
        }

        SetData(proto);
    }
}
