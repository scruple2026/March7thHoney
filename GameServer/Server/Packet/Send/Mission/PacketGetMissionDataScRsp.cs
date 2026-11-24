using March7thHoney.Data;
using March7thHoney.Data.Excel;
using March7thHoney.Enums.Mission;
using March7thHoney.GameServer.Game.Mission;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Kcp;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Server.Packet.Send.Mission;

public class PacketGetMissionDataScRsp : BasePacket
{
    public PacketGetMissionDataScRsp(PlayerInstance player) : base(CmdIds.GetMissionDataScRsp)
    {
        var trackMissionId = player.MissionManager!.Data.TrackingMainMissionId;
        if (trackMissionId <= 0 || player.MissionManager.GetMainMissionStatus(trackMissionId) != MissionPhaseEnum.Accept)
            // Same auto-select rule as UpdateTrackMainMissionCsReq: never land on a hidden
            // Branch/Gap transition mission, and follow NextTrackMainMission after a beat closes.
            trackMissionId = player.MissionManager.ResolveTrackingMainMissionId(0);
        player.MissionManager.Data.TrackingMainMissionId = trackMissionId;

        var proto = new GetMissionDataScRsp
        {
            TrackMissionId = (uint)trackMissionId
        };

        var preparedMainIds = player.MissionManager.Data.PreparedMainMissionIds ?? [];
        // With nothing tracked, official still sends the anchor of a Prepared frontier main, without a
        // track id, so the client can point at the locked quest it selects locally.
        var anchorMainId = trackMissionId > 0 ? trackMissionId : preparedMainIds.FirstOrDefault();
        var anchorSubMissionId = player.MissionManager.GetTrackMissionAnchorSubMissionId(anchorMainId);
        if (anchorSubMissionId > 0) proto.GKFPKBGJBGN = (uint)anchorSubMissionId;

        proto.FinishedMainMissionIdList.AddRange(player.MissionManager.Data.FinishedMainMissionIds
            .Concat(player.B51RacingManager?.CompletedMainMissionIDs ?? [])
            .Where(x => x > 0)
            .Select(x => (uint)x)
            .Distinct());

        foreach (var mission in GameData.MainMissionData.Keys)
            if (player.MissionManager!.GetMainMissionStatus(mission) == MissionPhaseEnum.Accept)
            {
                var mainProto = new MainMission
                {
                    Id = (uint)mission,
                    Status = MissionStatus.MissionDoing
                };
                if (player.MissionManager.Data.MainMissionCustomValues.TryGetValue(mission, out var records))
                    MissionSyncBuilder.FillMainCustomValues(mainProto, records);
                proto.MainMissionList.Add(mainProto);
            }

        // Prepared mains ride the same list, and their opener subs ride mission_list as Prepared: the
        // client builds a quest row from its subs, so a main without one shows no locked entry.
        var preparedSubIds = new List<int>();
        foreach (var mission in preparedMainIds)
        {
            if (!GameData.MainMissionData.TryGetValue(mission, out var preparedMain)) continue;
            proto.MainMissionList.Add(new MainMission
            {
                Id = (uint)mission,
                Status = MissionStatus.MissionPrepared
            });
            preparedSubIds.AddRange(preparedMain.MissionInfo?.StartSubMissionList.Where(id => id > 0) ?? []);
        }

        foreach (var mission in GameData.SubMissionInfoData.Keys.Concat(GameData.SubMissionData.Keys)
                     .Concat(MechCraftResourceCatalog.BlockingSubMissionIDs)
                     .Append(MechCraftResourceCatalog.TutorialSubMissionID).Where(id => id > 0).Distinct())
        {
            var isCompletedActivityMission = player.MechCraftManager?.IsCompletedSubMission(mission) == true ||
                player.B51RacingManager?.IsCompletedSubMission(mission) == true;
            if (isCompletedActivityMission || player.MissionManager!.GetSubMissionStatus(mission) == MissionPhaseEnum.Accept)
                proto.MissionList.Add(new Proto.Mission
                {
                    Id = (uint)mission,
                    Status = isCompletedActivityMission ? MissionStatus.MissionFinish : MissionStatus.MissionDoing,
                    Progress = (uint)player.MissionManager!.GetMissionProgress(mission)
                });
        }

        foreach (var mission in preparedSubIds.Distinct())
            proto.MissionList.Add(new Proto.Mission
            {
                Id = (uint)mission,
                Status = MissionStatus.MissionPrepared
            });

        SetData(proto);
    }
}
