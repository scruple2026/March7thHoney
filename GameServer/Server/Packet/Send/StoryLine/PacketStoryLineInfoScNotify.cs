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

        var curId = (uint)(player.StoryLineManager?.StoryLineData.CurStoryLineId ?? 0);
        if (!storyLineIdList.Contains(curId))
            storyLineIdList.Add(curId);

        var trialIds = new List<uint>();
        if (GameData.StroyLineTrialAvatarDataData.TryGetValue((int)curId, out var storyExcel))
            trialIds.AddRange(storyExcel.InitTrialAvatarList.Select(x => (uint)x));

        // 4.7 proto mapping note: field 7 (DDOHIGLHCNO -> TrialAvatarIdList) is unfinished_story_line_id_list,
        // while field 15 (BFEDFBEKOBI -> UnfinishedStoryLineIdList) is trial_avatar_id_list.
        var proto = new StoryLineInfoScNotify
        {
            CurStoryLineId = curId,
            TrialAvatarIdList = { storyLineIdList },
            UnfinishedStoryLineIdList = { trialIds }
        };

        SetData(proto);
    }
}
