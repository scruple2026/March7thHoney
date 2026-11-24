using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Mission;

public class PacketMissionSyncTaskScRsp : BasePacket
{
    public PacketMissionSyncTaskScRsp(string key) : base(CmdIds.MissionSyncTaskScRsp)
    {
        SetData(new MissionSyncTaskScRsp
        {
            Key = key
        });
    }
}
