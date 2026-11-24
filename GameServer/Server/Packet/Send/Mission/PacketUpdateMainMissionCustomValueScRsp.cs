using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Mission;

public class PacketUpdateMainMissionCustomValueScRsp : BasePacket
{
    public PacketUpdateMainMissionCustomValueScRsp(MainMission mainMission) : base(
        CmdIds.UpdateMainMissionCustomValueScRsp)
    {
        SetData(new UpdateMainMissionCustomValueScRsp
        {
            HKDDMGGDCIP = mainMission
        });
    }
}
