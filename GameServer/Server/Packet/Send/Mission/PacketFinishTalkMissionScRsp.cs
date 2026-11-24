using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Mission;

public class PacketFinishTalkMissionScRsp : BasePacket
{
    public PacketFinishTalkMissionScRsp(string talkStr) : base(CmdIds.FinishTalkMissionScRsp)
    {
        var proto = new FinishTalkMissionScRsp
        {
            TalkStr = talkStr
        };

        SetData(proto);
    }

    public PacketFinishTalkMissionScRsp(FinishTalkMissionCsReq req) : base(CmdIds.FinishTalkMissionScRsp)
    {
        var proto = new FinishTalkMissionScRsp
        {
            TalkStr = req.TalkStr ?? string.Empty,
            SubMissionId = req.SubMissionId
        };

        if (req.CustomValueList != null && req.CustomValueList.Count > 0)
            proto.CustomValueList.AddRange(req.CustomValueList);

        SetData(proto);
    }
}
