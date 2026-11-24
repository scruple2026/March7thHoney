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
        var storyLineIdList =
            player.StoryLineManager!.StoryLineData.RunningStoryLines.Keys.Select(x => (uint)x).ToList();
        storyLineIdList.Insert(0, 0);
        if (!storyLineIdList.Contains((uint)curId))
            storyLineIdList.Add((uint)curId);

        var trialIds = new List<uint>();
        if (GameData.StroyLineTrialAvatarDataData.TryGetValue(curId, out var storyExcel))
            trialIds.AddRange(storyExcel.InitTrialAvatarList.Select(x => (uint)x));

        // 4.7 proto mapping note: field 13 (DDOHIGLHCNO -> TrialAvatarIdList) is unfinished_story_line_id_list,
        // while field 2 (BFEDFBEKOBI -> UnfinishedStoryLineIdList) is trial_avatar_id_list.
        var proto = new GetStoryLineInfoScRsp
        {
            CurStoryLineId = (uint)curId,
            TrialAvatarIdList = { storyLineIdList },
            UnfinishedStoryLineIdList = { trialIds }
        };

        SetData(proto);
    }
}
