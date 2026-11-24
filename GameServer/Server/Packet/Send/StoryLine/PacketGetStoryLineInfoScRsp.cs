using March7thHoney.Data;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.StoryLine;

public class PacketGetStoryLineInfoScRsp : BasePacket
{
    public PacketGetStoryLineInfoScRsp(PlayerInstance player) : base(CmdIds.GetStoryLineInfoScRsp)
    {
        var curId = player.StoryLineManager!.StoryLineData.CurStoryLineId;
        var proto = new GetStoryLineInfoScRsp
        {
            CurStoryLineId = (uint)curId,
            UnfinishedStoryLineIdList =
                { player.StoryLineManager!.StoryLineData.RunningStoryLines.Keys.Select(x => (uint)x) }
        };

        if (curId != 0 && !proto.UnfinishedStoryLineIdList.Contains((uint)curId))
            proto.UnfinishedStoryLineIdList.Add((uint)curId);

        if (GameData.StroyLineTrialAvatarDataData.TryGetValue(curId, out var storyExcel))
            proto.TrialAvatarIdList.AddRange(storyExcel.InitTrialAvatarList.Select(x => (uint)x));

        SetData(proto);
    }
}
