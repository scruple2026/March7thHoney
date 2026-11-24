using March7thHoney.Data;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.StoryLine;

public class PacketStoryLineInfoScNotify : BasePacket
{
    public PacketStoryLineInfoScNotify(PlayerInstance player) : base(CmdIds.StoryLineInfoScNotify)
    {
        var storyLineIdList =
            player.StoryLineManager?.StoryLineData.RunningStoryLines.Keys.Select(x => (uint)x).ToList() ?? [];
        storyLineIdList.Insert(0, 0);

        var proto = new StoryLineInfoScNotify
        {
            CurStoryLineId = (uint)(player.StoryLineManager?.StoryLineData.CurStoryLineId ?? 0),
            UnfinishedStoryLineIdList = { storyLineIdList }
        };

        if (!proto.UnfinishedStoryLineIdList.Contains(proto.CurStoryLineId))
            proto.UnfinishedStoryLineIdList.Add(proto.CurStoryLineId);

        SetData(proto);
    }
}
