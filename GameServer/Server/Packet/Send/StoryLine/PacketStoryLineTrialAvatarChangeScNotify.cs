using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.StoryLine;

public class PacketStoryLineTrialAvatarChangeScNotify : BasePacket
{
    public PacketStoryLineTrialAvatarChangeScNotify(IEnumerable<int> added, IEnumerable<int> removed,
        bool isEvent = true) : base(CmdIds.StoryLineTrialAvatarChangeScNotify)
    {
        var proto = new StoryLineTrialAvatarChangeScNotify
        {
            OLPNMKAPDLP = isEvent
        };
        foreach (var id in added)
            if (id > 0)
                proto.EDBMIFNEHHH.Add((uint)id);
        foreach (var id in removed)
            if (id > 0)
                proto.ILIDIIGALKD.Add((uint)id);

        SetData(proto);
    }
}
