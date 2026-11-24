using March7thHoney.Database.Quests;
using March7thHoney.Proto;

namespace March7thHoney.GameServer.Game.Mission;

/// <summary>
///     Shared MissionSync assembly. Packet pushes and the mission manager both go through here so a
///     level-up, a talk finish, and a login repair describe a change the same way.
/// </summary>
public static class MissionSyncBuilder
{
    public static void Merge(MissionSync target, MissionSync? source)
    {
        if (source == null) return;
        target.MissionList.AddRange(source.MissionList);
        target.FinishedMainMissionIdList.AddRange(source.FinishedMainMissionIdList);
        target.LEBFLAKCJCM.AddRange(source.LEBFLAKCJCM);
        target.DEPBONHELFD.AddRange(source.DEPBONHELFD);
        foreach (var record in source.NLPKLNDCMOC)
            ReplaceCustomValueRecord(target, record);
    }

    public static void NoteAcceptedSub(MissionSync sync, int subId)
    {
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)subId,
            Status = MissionStatus.MissionDoing
        });
    }

    public static void NotePreparedSub(MissionSync sync, int subId)
    {
        if (subId <= 0) return;
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)subId,
            Status = MissionStatus.MissionPrepared
        });
    }

    public static void NoteRemovedMain(MissionSync sync, int mainId)
    {
        if (mainId > 0) sync.DEPBONHELFD.Add((uint)mainId);
    }

    public static void AttachCustomValues(MissionSync sync, int mainId, IReadOnlyList<MissionCustomValueRecord> records)
    {
        if (mainId <= 0 || records.Count == 0) return;
        var list = new ONHKODAFEMH();
        foreach (var record in records)
            list.CustomValueList.Add(ToProto(record));
        ReplaceCustomValueRecord(sync, new MJBANFPPEFH
        {
            Id = (uint)mainId,
            CustomValueList = list
        });
    }

    public static void FillMainCustomValues(MainMission proto, IReadOnlyList<MissionCustomValueRecord>? records)
    {
        if (records == null) return;
        foreach (var record in records)
            proto.CustomValueList.Add(ToProto(record));
    }

    /// <summary>
    ///     Collapses duplicate sub records to the last one and drops new-sub ids that already finished
    ///     in this same change.
    /// </summary>
    public static void Finalize(MissionSync sync)
    {
        if (sync.MissionList.Count > 1)
        {
            var byId = new Dictionary<uint, Proto.Mission>();
            foreach (var mission in sync.MissionList)
                byId[mission.Id] = mission;
            sync.MissionList.Clear();
            foreach (var mission in byId.Values.OrderBy(mission => mission.Id))
                sync.MissionList.Add(mission);
        }

        var finished = new HashSet<uint>();
        foreach (var mission in sync.MissionList)
            if (mission.Status == MissionStatus.MissionFinish)
                finished.Add(mission.Id);

        var accepted = sync.LEBFLAKCJCM.Where(id => id != 0 && !finished.Contains(id)).Distinct().ToList();
        sync.LEBFLAKCJCM.Clear();
        sync.LEBFLAKCJCM.AddRange(accepted);

        var removed = sync.DEPBONHELFD.Where(id => id != 0).Distinct().ToList();
        sync.DEPBONHELFD.Clear();
        sync.DEPBONHELFD.AddRange(removed);
    }

    private static MissionCustomValue ToProto(MissionCustomValueRecord record)
    {
        var proto = new MissionCustomValue
        {
            Index = record.Index,
            CustomValue = record.CustomValue
        };
        if (!string.IsNullOrEmpty(record.Key)) proto.BGDFHBAANLB = record.Key;
        return proto;
    }

    private static void ReplaceCustomValueRecord(MissionSync sync, MJBANFPPEFH record)
    {
        for (var i = 0; i < sync.NLPKLNDCMOC.Count; i++)
        {
            if (sync.NLPKLNDCMOC[i].Id != record.Id) continue;
            sync.NLPKLNDCMOC[i] = record;
            return;
        }

        sync.NLPKLNDCMOC.Add(record);
    }
}
