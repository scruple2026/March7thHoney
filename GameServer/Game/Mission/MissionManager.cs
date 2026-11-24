using March7thHoney.Data;
using March7thHoney.Data.Config;
using March7thHoney.Data.Config.Scene;
using March7thHoney.Data.Excel;
using March7thHoney.Database;
using March7thHoney.Database.Inventory;
using MissionCustomValueRecord = March7thHoney.Database.Quests.MissionCustomValueRecord;
using March7thHoney.Enums.Item;
using March7thHoney.Enums.Mission;
using March7thHoney.Enums.Scene;
using March7thHoney.GameServer.Game.Battle;
using March7thHoney.GameServer.Game.DailyActive;
using March7thHoney.GameServer.Game.Mission.FinishAction;
using March7thHoney.GameServer.Game.Mission.FinishType;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.GameServer.Game.Task;
using March7thHoney.GameServer.Server.Packet.Send.HeartDial;
using March7thHoney.GameServer.Server.Packet.Send.Mission;
using March7thHoney.GameServer.Server.Packet.Send.PlayerSync;
using March7thHoney.GameServer.Server.Packet.Send.Scene;
using March7thHoney.Kcp;
using March7thHoney.Proto;
using March7thHoney.Util;
using MissionData = March7thHoney.Database.Quests.MissionData;

namespace March7thHoney.GameServer.Game.Mission;

public partial class MissionManager(PlayerInstance player) : BasePlayerManager<MissionData>(player)
{
    #region Initializer & Properties

    private static readonly Logger Logger = new("Mission");

    public static readonly Dictionary<FinishActionTypeEnum, MissionFinishActionHandler> ActionHandlers = [];
    public static readonly Dictionary<MissionFinishTypeEnum, MissionFinishTypeHandler> FinishTypeHandlers = [];

    public readonly List<int> SkipSubMissionList = []; // bug

    /// <summary>
    ///     Whether the mission system runs for THIS player. Normally the server config, but /mission start
    ///     flips it on per player so the story is playable on a server that ships with missions off.
    /// </summary>
    public bool Enabled => Data.MissionEnabled;

    #endregion

    #region Mission Actions

    public List<int> GetAllFinishedMissionIds()
    {
        return Data.FinishedSubMissionIds
            .Concat(Data.FinishedMainMissionIds)
            .Concat(Player.B51RacingManager?.CompletedMissionIDs ?? [])
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    public async ValueTask<List<MissionSync?>> AcceptMainMission(int missionId, bool sendPacket = true)
    {
        if (Player.B51RacingManager?.IsCompletedMainMission(missionId) == true) return [];
        if (!Enabled) return [];
        // A Prepared main is a real accept for a direct caller (GM, or the promotion pass).
        if (Data.GetMainMissionStatus(missionId) == MissionPhaseEnum.Prepared)
            Data.SetMainMissionStatus(missionId, MissionPhaseEnum.None);
        if (Data.GetMainMissionStatus(missionId) != MissionPhaseEnum.None) return []; // already accepted
        // Get entry sub mission
        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return [];

        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.Accept);

        // A "Gap" row with no MissionInfo wiring is a display-only transition entry (e.g. the
        // Trailblaze chain 1054400/1054402/1054403 and the beat missions 1054407/1054408/1054409):
        // it has no start subs and no finish gate, so FinishSubMission can never close it and every
        // successor whose TakeParam is MultiSequence on it stalls forever. Auto-finish it on accept
        // so the chain cascades the way the official server advances it — FinishMainMission re-runs
        // AcceptMainMissionByCondition, which picks up the next stage in the same pass.
        // Deliberately restricted to Gap: a Main-type mission with a missing MissionInfo is playable
        // content (or a resource-dump gap) and must never be silently skipped.
        if (mission.IsDisplayOnlyGap)
        {
            await FinishMainMission(missionId);
            return [];
        }

        var list = new List<MissionSync?>();
        foreach (var i in mission.MissionInfo?.StartSubMissionList ?? [])
            list.Add(await AcceptSubMission(i, sendPacket));
        if (missionId == 1000400)
        {
            await Player.AddAvatar(1003);
            await Player.LineupManager!.AddAvatarToCurTeam(1003);
        }

        // message
        foreach (var sectionConfigExcel in GameData.MessageSectionConfigData.Values.Where(x =>
                     x.MainMissionLink == missionId))
            await Player.MessageManager!.AddMessageSection(sectionConfigExcel.ID);

        foreach (var info in mission.MissionInfo!.SubMissionList.Where(x =>
                     x.FinishType is MissionFinishTypeEnum.MessagePerformSectionFinish
                         or MissionFinishTypeEnum.MessageSectionFinish))
            await Player.MessageManager!.AddMessageSection(info.ParamInt1);

        foreach (var sync in list)
            if (sync != null) RefreshSubMissionSync(sync);
        return list;
    }

    public async ValueTask<MissionSync> AcceptMainMissionByCondition(bool sendPacket = true)
    {
        var sync = new MissionSync();
        if (!Enabled) return sync;

        foreach (var mainId in Data.PreparedMainMissionIds?.ToList() ?? [])
        {
            if (!GameData.MainMissionData.TryGetValue(mainId, out var prepared))
            {
                Data.SetMainMissionStatus(mainId, MissionPhaseEnum.None);
                continue;
            }

            var scheduled = IsMainMissionScheduled(mainId);
            if (scheduled && prepared.IsEqual(Data) && prepared.IsBeginSatisfied(Data))
            {
                foreach (var accepted in await AcceptMainMission(mainId, sendPacket))
                    MissionSyncBuilder.Merge(sync, accepted);
                continue;
            }

            if (!scheduled || !IsPreparedEligible(prepared))
                Data.SetMainMissionStatus(mainId, MissionPhaseEnum.None);
        }

        foreach (var nextMission in GameData.MainMissionData.Values)
        {
            if (Data.GetMainMissionStatus(nextMission.MainMissionID) != MissionPhaseEnum.None)
                continue;
            if (!IsMainMissionScheduled(nextMission.MainMissionID)) continue;

            var take = nextMission.IsEqual(Data);
            var begin = nextMission.IsBeginSatisfied(Data);
            if (take && begin)
            {
                foreach (var accepted in await AcceptMainMission(nextMission.MainMissionID, sendPacket))
                    MissionSyncBuilder.Merge(sync, accepted);
                continue;
            }

            if (IsPreparedEligible(nextMission))
                AcceptPrepared(nextMission, sync);
        }

        RefreshSubMissionSync(sync);
        if (sendPacket && sync.MissionList.Any(mission => mission.Status == MissionStatus.MissionPrepared))
        {
            var preparedSync = new MissionSync();
            foreach (var mission in sync.MissionList)
                if (mission.Status == MissionStatus.MissionPrepared)
                    preparedSync.MissionList.Add(mission);
            await Player.SendPacket(new PacketPlayerSyncScNotify(preparedSync));
        }

        return sync;
    }

    public async ValueTask<List<MissionSync?>> ReAcceptMainMission(int missionId, bool sendPacket = true)
    {
        if (Player.B51RacingManager?.IsCompletedMainMission(missionId) == true) return [];
        if (!Enabled) return [];

        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return [];
        MissionSync sync = new();

        foreach (var subMission in mission.SubMissionIds.Where(id => !IsCompletedActivitySubMission(id)))
            if (Data.GetSubMissionStatus(subMission) == MissionPhaseEnum.Finish ||
                Data.GetSubMissionStatus(subMission) == MissionPhaseEnum.Accept)
                sync.MissionList.Add(new Proto.Mission
                {
                    Id = (uint)subMission,
                    Status = MissionStatus.MissionNone
                });

        foreach (var subMission in
                 mission.SubMissionIds.Where(id => !IsCompletedActivitySubMission(id))) Data.SetSubMissionStatus(subMission, MissionPhaseEnum.None); // reset

        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.None); // reset
        MissionSyncBuilder.NoteRemovedMain(sync, missionId);
        MissionSyncBuilder.Finalize(sync);
        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));

        return await AcceptMainMission(missionId, sendPacket);
    }

    /// <summary>
    ///     Re-derives the running sub-missions of every accepted main mission, so a save written while the
    ///     mission graph was incomplete becomes playable again instead of staying frozen forever.
    /// </summary>
    /// <remarks>
    ///     A main mission with no running sub is a dead end: the client renders an empty objective list,
    ///     no finish gate can ever close, and every successor gated on it stays out of the mission log.
    ///     Saves made before the 4.5.0 resource rebuild are full of them - the truncated MissionInfo
    ///     registered no sub-missions at all, so AcceptMainMission flipped the main to Accept and then
    ///     accepted nothing. The data is fixed now, but the stored state is not, and nothing in the normal
    ///     login path ever revisits an already-accepted main.
    ///     For each stalled main this replays the steps the live flow would have taken: auto-close a
    ///     display-only Gap row, close a main whose finish gate is already fully satisfied, and otherwise
    ///     re-arm the front line - first the successors of the subs the player did finish, so a half-played
    ///     chapter resumes where it stopped rather than replaying from the top, then the entry subs, which
    ///     is a no-op unless the mission never actually started.
    ///     A main that is still empty afterwards is a real data gap: it is reported, never force-skipped,
    ///     because skipping a playable Main-type row silently eats content.
    /// </remarks>
    public async ValueTask<MissionRepairReport> RepairStalledMainMissions(bool sendPacket = true)
    {
        var report = new MissionRepairReport();
        if (!Enabled) return report;

        await NormalizeFinishedMainSubs(sendPacket);
        await CleanupUnavailableScheduledMissions(sendPacket);
        RepairMissingFloorSavedValues();

        // Closing one main accepts its successors, and a successor can itself land stalled, so keep
        // sweeping. `handled` makes every main a one-shot, which is what bounds the loop.
        var handled = new HashSet<int>();
        while (true)
        {
            var pending = Data.RunningMainMissionIds
                .Where(x => x > 0 && !handled.Contains(x))
                .OrderBy(x => x)
                .ToList();
            if (pending.Count == 0) break;

            var changed = false;
            foreach (var mainId in pending)
            {
                handled.Add(mainId);
                if (!GameData.MainMissionData.TryGetValue(mainId, out var main)) continue;
                // Closing an earlier main can cascade through this one, so re-check the snapshot entry.
                if (Data.GetMainMissionStatus(mainId) != MissionPhaseEnum.Accept) continue;
                if (HasRunningSubMission(main)) continue; // healthy: the player has something to do

                if (await CloseSatisfiedMainMission(mainId, main))
                {
                    report.Closed.Add(mainId);
                    changed = true;
                    continue;
                }

                var sync = new MissionSync();
                await TriggerNextSubMissions(main, sync, true);
                if (sendPacket && sync.MissionList.Count > 0)
                    await Player.SendPacket(new PacketPlayerSyncScNotify(sync));

                // Only when the continuation found nothing: an entry sub is None either because the
                // mission never started, or because it is genuinely still ahead of the player.
                if (!HasRunningSubMission(main))
                    foreach (var subId in main.MissionInfo.StartSubMissionList)
                        await AcceptSubMission(subId, sendPacket);

                if (HasRunningSubMission(main))
                {
                    report.Resumed.Add(mainId);
                    changed = true;
                }
                else
                {
                    report.Stalled.Add(mainId);
                }
            }

            if (!changed) break;
        }

        // A track pointing at a main with nothing running is the "lost objective list" symptom, so
        // re-resolve it whenever the repair left the tracked mission without a front line.
        var tracked = Data.TrackingMainMissionId;
        if (tracked <= 0 || Data.GetMainMissionStatus(tracked) != MissionPhaseEnum.Accept ||
            !HasRunningSubMission(tracked))
        {
            var resolved = ResolveTrackingMainMissionId(0);
            if (resolved != tracked)
            {
                Data.TrackingMainMissionId = resolved;
                report.RetrackedTo = resolved;
            }
        }

        if (report.Changed) MarkDirty();

        if (report.Resumed.Count > 0 || report.Closed.Count > 0)
            Logger.Info($"Repaired stalled mission state for uid {Player.Uid}: " +
                        $"resumed [{string.Join(", ", report.Resumed)}], closed [{string.Join(", ", report.Closed)}]" +
                        (report.RetrackedTo > 0 ? $", tracking -> {report.RetrackedTo}" : ""));

        if (report.Stalled.Count > 0)
            Logger.Warn($"uid {Player.Uid} has {report.Stalled.Count} accepted main mission(s) with no " +
                        $"runnable sub-mission left: [{string.Join(", ", report.Stalled)}]. Their sub-mission " +
                        "graph is missing or their gate is unreachable, so they block every successor - " +
                        "restore the graph (Config/Custom/MissionInfoOverride.json or a resource rebuild), " +
                        "or close them by hand with /mission finishmain <id>.");

        return report;
    }

    /// <summary>
    ///     Closes a stalled main that has nothing left to run: a row that ships no gameplay at all, or one
    ///     whose finish gate is already fully satisfied and that FinishSubMission simply never got to close.
    /// </summary>
    private async ValueTask<bool> CloseSatisfiedMainMission(int mainId, MainMissionExcel main)
    {
        var gate = main.MissionInfo.FinishSubMissionList;

        // Neither sub-missions nor a finish gate: there is no objective to run and no condition that
        // could ever close it. Besides the display-only Gap rows, the 4.5.0 tree has eight Branch rows in
        // that shape (4010146, 4010241-4010246, 4011800). Closing them skips nothing, because there is
        // nothing there. Deliberately limited to Branch/Gap: on a Main-type row - and on Unknown, which
        // is what a Resource.bin predating the Type column restores to - an empty graph means a truncated
        // dump, and auto-closing that would silently eat playable content.
        var empty = main.MissionInfo.SubMissionList.Count == 0 && gate.Count == 0 &&
                    main.Type is MainMissionTypeEnum.Branch or MainMissionTypeEnum.Gap;

        var satisfied = main.IsDisplayOnlyGap || empty ||
                        (gate.Count > 0 && gate.All(x => GetSubMissionStatus(x) == MissionPhaseEnum.Finish));
        if (!satisfied) return false;

        await FinishMainMission(mainId);
        return Data.GetMainMissionStatus(mainId) == MissionPhaseEnum.Finish;
    }

    private bool HasRunningSubMission(int mainMissionId)
    {
        return GameData.MainMissionData.TryGetValue(mainMissionId, out var main) && HasRunningSubMission(main);
    }

    private bool HasRunningSubMission(MainMissionExcel main)
    {
        foreach (var sub in main.MissionInfo.SubMissionList)
            if (Data.GetSubMissionStatus(sub.ID) == MissionPhaseEnum.Accept)
                return true;

        return false;
    }

    public async ValueTask RemoveMainMission(int missionId, bool sendPacket = true)
    {
        if (Player.B51RacingManager?.IsCompletedMainMission(missionId) == true) return;
        if (!Enabled) return;
        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.None);

        GameData.MainMissionData.TryGetValue(missionId, out var mission);
        if (mission == null) return;

        MissionSync sync = new();

        foreach (var subMission in mission.SubMissionIds.Where(id => !IsCompletedActivitySubMission(id)))
        {
            Data.SetSubMissionStatus(subMission, MissionPhaseEnum.None);
            await SetMissionProgress(subMission, 0, false);
            sync.MissionList.Add(new Proto.Mission
            {
                Id = (uint)subMission,
                Status = MissionStatus.MissionNone
            });
        }

        MissionSyncBuilder.NoteRemovedMain(sync, missionId);
        MissionSyncBuilder.Finalize(sync);
        if (sendPacket) await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    public async ValueTask AcceptSubMission(int missionId)
    {
        if (!Enabled) return;
        await AcceptSubMission(missionId, true);
    }

    public async ValueTask<MissionSync?> AcceptSubMission(int missionId, bool sendPacket,
        bool doFinishTypeAction = true)
    {
        if (!Enabled || IsCompletedActivitySubMission(missionId)) return null;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var mission);
        if (mission == null) return null;
        if (Data.GetSubMissionStatus(missionId) != MissionPhaseEnum.None) return null; // already accepted

        Data.SetSubMissionStatus(missionId, MissionPhaseEnum.Accept);
        if (missionId == 403000222) await RefreshRogueGalleryProps();

        var sync = new MissionSync();
        MissionSyncBuilder.NoteAcceptedSub(sync, missionId);

        if (sendPacket)
        {
            await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
            if (Player.SceneInstance != null)
            {
                await Player.SceneInstance.SyncGroupInfo();
                await EnsureRunningKillMonsterTargetsVisible();
                await SyncStartedKillMonsterSceneSnapshot(sync);
            }
        }

        if (mission.SubMissionInfo != null)
            try
            {
                FinishTypeHandlers.TryGetValue(mission.SubMissionInfo.FinishType, out var handler);
                if (doFinishTypeAction)
                    if (handler != null)
                        await handler.HandleMissionFinishType(Player, mission.SubMissionInfo, null);
            }
            catch
            {
            }

        if (SkipSubMissionList.Contains(missionId)) await FinishSubMission(missionId);

        if (sendPacket && mission.SubMissionInfo?.LevelFloorID == Player.SceneInstance?.FloorId)
            if (mission.SubMissionInfo?.GroupIDList != null)
                foreach (var group in mission.SubMissionInfo.GroupIDList)
                    await Player.SceneInstance!.EntityLoader!.LoadGroup(group);

        // TODO: Mission Task
        Player.TaskManager?.MissionTaskTrigger.TriggerMissionTask(missionId);

        RefreshSubMissionSync(sync);
        return sync;
    }

    public async ValueTask FinishMainMission(int missionId)
    {
        if (Player.B51RacingManager?.IsCompletedMainMission(missionId) == true) return;
        if (!Enabled) return;
        if (!GameData.MainMissionData.TryGetValue(missionId, out var mainMission)) return;
        if (Data.GetMainMissionStatus(missionId) != MissionPhaseEnum.Accept) return;
        Data.SetMainMissionStatus(missionId, MissionPhaseEnum.Finish);
        var sync = new MissionSync();
        sync.FinishedMainMissionIdList.Add((uint)missionId);
        // get next main mission
        foreach (var mission in mainMission.SubMissionIds)
            if (GetSubMissionStatus(mission) != MissionPhaseEnum.Finish)
                if (Data.GetSubMissionStatus(mission) != MissionPhaseEnum.Finish)
                {
                    Data.SetSubMissionStatus(mission, MissionPhaseEnum.Finish);
                    sync.MissionList.Add(new Proto.Mission
                    {
                        Id = (uint)mission,
                        Status = MissionStatus.MissionFinish
                    });
                }

        var mainSync = await AcceptMainMissionByCondition(false);
        MissionSyncBuilder.Merge(sync, mainSync);

        RefreshSubMissionSync(sync);
        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
        if (Player.SceneInstance != null)
        {
            await Player.SceneInstance.SyncGroupInfo();
            await LoadRunningMissionGroupsForCurrentFloor();
            await EnsureRunningKillMonsterTargetsVisible();
            await SyncStartedKillMonsterSceneSnapshot(sync);
        }

        await Player.SendPacket(new PacketStartFinishMainMissionScNotify(missionId));
        await Player.SendPacket(new PacketFinishedMissionScNotify(missionId));
        await HandleMissionReward(missionId);
        await HandleFinishType(MissionFinishTypeEnum.FinishMission);

        // A finished daily main mission feeds the daily training counters (quest 2100003 "Complete 1 Daily
        // Mission" is FinishTodayDailyMissionCnt; cycle quests 1001802/1001925 are FinishDailyMissionCnt).
        if (mainMission.Type == MainMissionTypeEnum.Daily &&
            Player.GetComponentOrDefault<DailyActiveManager>()?.RecordDailyMission(missionId) == true)
        {
            await HandleFinishType(MissionFinishTypeEnum.FinishTodayDailyMissionCnt);
            await HandleFinishType(MissionFinishTypeEnum.FinishDailyMissionCnt);
        }

        await Player.RaidManager!.CheckIfLeaveRaid();
    }

    public async ValueTask FinishSubMission(int missionId)
    {
        if (!Enabled || IsCompletedActivitySubMission(missionId)) return;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;
        var mainMissionId = subMission.MainMissionId;
        if (Data.GetSubMissionStatus(missionId) != MissionPhaseEnum.Accept) return; // not accepted
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission); // get main mission
        if (mainMission == null) return;
        Data.SetSubMissionStatus(missionId, MissionPhaseEnum.Finish); // set finish

        await SetMissionProgress(missionId, subMission.SubMissionInfo?.Progress ?? 1);

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Status = MissionStatus.MissionFinish,
            Progress = (uint)(subMission.SubMissionInfo?.Progress ?? 1)
        });

        await TriggerNextSubMissions(mainMission, sync);

        // Raid domain portals: a kill opens them before the sync, any other clearing sub right after the
        // finish notify (official order).
        var killSub = subMission.SubMissionInfo?.FinishType == MissionFinishTypeEnum.KillMonster;
        if (killSub && Player.SceneInstance != null)
            await Player.SceneInstance.OpenRaidDomainPortals(missionId, true);

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
        if (Player.SceneInstance != null)
        {
            await Player.SceneInstance.SyncGroupInfo();
            await LoadRunningMissionGroupsForCurrentFloor();
            await EnsureRunningKillMonsterTargetsVisible();
            await SyncStartedKillMonsterSceneSnapshot(sync);
        }

        await Player.SendPacket(new PacketStartFinishSubMissionScNotify(missionId));
        if (!killSub && Player.SceneInstance != null)
            await Player.SceneInstance.OpenRaidDomainPortals(missionId, true);

        if (mainMission.MissionInfo != null)
            await HandleFinishAction(mainMission.MissionInfo, missionId);

        // Get if it should finish main mission
        // get current main mission
        var shouldFinish = true;
        mainMission.MissionInfo?.FinishSubMissionList.ForEach(id =>
        {
            if (GetSubMissionStatus(id) != MissionPhaseEnum.Finish) shouldFinish = false;
        });

        foreach (var nextMission in GetRunningSubMissionList())
        {
            FinishTypeHandlers.TryGetValue(nextMission.FinishType, out var handler);
            if (handler != null) await handler.HandleMissionFinishType(Player, nextMission, null);
        }

        if (shouldFinish) await FinishMainMission(mainMissionId);

        await HandleSubMissionNarrativeHooks(missionId);

        // handle reward
        await HandleSubMissionReward(missionId);
        //Player.StoryLineManager!.CheckIfEnterStoryLine();
        //Player.StoryLineManager!.CheckIfFinishStoryLine();

    }

    // Accept the sub-missions unlocked by finishing one, honoring AnySequence (OR) / MultiSequence (AND) gates.
    private async ValueTask TriggerNextSubMissions(MainMissionExcel mainMission, MissionSync sync,
        bool doFinishTypeAction = false)
    {
        foreach (var nextMission in mainMission.MissionInfo?.SubMissionList ?? [])
        {
            if (nextMission.TakeType != SubMissionTakeTypeEnum.AnySequence &&
                nextMission.TakeType != SubMissionTakeTypeEnum.MultiSequence) continue;
            var canAccept = nextMission.TakeType == SubMissionTakeTypeEnum.MultiSequence; // mean and operation
            foreach (var id in nextMission.TakeParamIntList ?? [])
                if (GetSubMissionStatus(id) != MissionPhaseEnum.Finish &&
                    nextMission.TakeType == SubMissionTakeTypeEnum.MultiSequence)
                {
                    canAccept = false;
                    break;
                }
                else if (GetSubMissionStatus(id) == MissionPhaseEnum.Finish &&
                         nextMission.TakeType == SubMissionTakeTypeEnum.AnySequence) // or operation
                {
                    canAccept = true;
                    break;
                }

            if (canAccept)
            {
                var s = await AcceptSubMission(nextMission.ID, false, doFinishTypeAction);
                MissionSyncBuilder.Merge(sync, s);
            }
        }

        RefreshSubMissionSync(sync);
    }

    private void RefreshSubMissionSync(MissionSync sync)
    {
        // Accepting a successor can enter a scene and finish it (or an earlier successor)
        // recursively. Publish the current state, not the Doing snapshot created before that work.
        // Prepared openers are not stored as running subs; refreshing them would publish MissionNone.
        foreach (var mission in sync.MissionList)
        {
            if (mission.Status == MissionStatus.MissionPrepared) continue;
            mission.Status = GetSubMissionStatus((int)mission.Id).ToProto();
            mission.Progress = (uint)GetMissionProgress((int)mission.Id);
        }

        MissionSyncBuilder.Finalize(sync);
    }

    // Hardcoded story-mission side effects (Heliobus lineup swap / HeartDial script unlocks).
    private async ValueTask HandleSubMissionNarrativeHooks(int missionId)
    {
        if (missionId == 101140201)
        {
            // Player.ChangeAvatarPathType(8001, Enums.Avatar.MultiPathAvatarTypeEnum.Knight);
            var list = Player.LineupManager!.GetCurLineup()!.BaseAvatars!
                .Select(x => x.SpecialAvatarId > 0 ? x.SpecialAvatarId : x.BaseAvatarId).ToList();
            list[list.IndexOf(8001)] = Player.Data.CurrentGender == Gender.Man ? 1008003 : 1008004;
            Player.LineupManager!.SetExtraLineup(ExtraLineupType.LineupHeliobus, list);
        }

        if (missionId == 103040103)
            await Player.SendPacket(new PacketHeartDialScriptChangeScNotify(HeartDialUnlockStatus.UnlockSingle));

        if (missionId == 103040104)
            await Player.SendPacket(new PacketHeartDialScriptChangeScNotify(HeartDialUnlockStatus.UnlockAll));
    }

    public async ValueTask HandleFinishAction(MissionInfo info, int subMissionId)
    {
        var subMission = info.SubMissionList.Find(x => x.ID == subMissionId);
        if (subMission == null) return;

        foreach (var action in subMission.FinishActionList ?? []) await HandleFinishAction(action);
    }

    public async ValueTask HandleFinishAction(FinishActionInfo actionInfo)
    {
        ActionHandlers.TryGetValue(actionInfo.FinishActionType, out var handler);
        if (handler != null)
            await handler.OnHandle(actionInfo.FinishActionPara, actionInfo.FinishActionParaString, Player);
    }

    public async ValueTask HandleMissionReward(int mainMissionId)
    {
        if (Data.ReplayedRogueTutorialRewardIds.Contains(mainMissionId)) return;
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission);
        if (mainMission == null) return;
        GameData.RewardDataData.TryGetValue(mainMission.RewardID, out var reward);
        var itemList = new List<ItemData>();

        foreach (var item in reward?.GetItems() ?? [])
        {
            GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
            var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
            if (res != null) itemList.Add(res);
        }

        var hCoin = await Player.InventoryManager!.AddItem(1, reward?.Hcoin ?? 0, false);
        if (hCoin != null) itemList.Add(hCoin);

        foreach (var i in mainMission.SubRewardList)
        {
            GameData.RewardDataData.TryGetValue(i, out var rewardDataExcel);
            var hCoin2 = await Player.InventoryManager!.AddItem(1, rewardDataExcel?.Hcoin ?? 0, false); // hcoin
            if (hCoin2 != null) itemList.Add(hCoin2);
            foreach (var item in rewardDataExcel?.GetItems() ?? []) // items
            {
                GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
                var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                    itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
                if (res != null) itemList.Add(res);
            }
        }


        if (itemList.Count > 0)
            await Player.SendPacket(new PacketMissionRewardScNotify(mainMissionId, 0, itemList));
    }

    public async ValueTask HandleSubMissionReward(int subMissionId)
    {
        GameData.SubMissionInfoData.TryGetValue(subMissionId, out var subMission);
        if (subMission == null) return;
        if (Data.ReplayedRogueTutorialRewardIds.Contains(subMission.MainMissionId)) return;
        GameData.RewardDataData.TryGetValue(subMission.SubMissionInfo?.SubRewardID ?? 0, out var reward);
        var itemList = new List<ItemData>();

        foreach (var item in reward?.GetItems() ?? [])
        {
            GameData.ItemConfigData.TryGetValue(item.Item1, out var itemExcel);
            var res = await Player.InventoryManager!.AddItem(item.Item1, item.Item2,
                itemExcel?.ItemMainType == ItemMainTypeEnum.AvatarCard); // notify if avatar card
            if (res != null) itemList.Add(res);
        }

        await Player.SendPacket(new PacketSubMissionRewardScNotify(subMissionId, itemList));
    }

    public async ValueTask HandleFinishType(MissionFinishTypeEnum finishType, object? arg = null, bool pushQuest = true)
    {
        FinishTypeHandlers.TryGetValue(finishType, out var handler);
        foreach (var mission in GetRunningSubMissionList())
            if (mission.FinishType == finishType)
                if (handler != null)
                    await handler.HandleMissionFinishType(Player, mission, arg);

        foreach (var quest in Player.QuestManager?.GetRunningQuest() ?? [])
        {
            var excel = GameData.QuestDataData.GetValueOrDefault(quest.QuestId);
            if (excel == null) continue;
            var finishWay = GameData.FinishWayData.GetValueOrDefault(excel.FinishWayID);
            if (finishWay == null) continue;
            if (finishWay.FinishType == finishType)
                if (handler != null)
                    await handler.HandleQuestFinishType(Player, excel, finishWay, arg);
        }

        if (pushQuest)
            await Player.QuestManager!.SyncQuest();
    }

    /// <summary>Raises one occurrence of a "+N per event" finish type (see <see cref="MissionCountEvent" />).</summary>
    public ValueTask TriggerCountEvent(MissionFinishTypeEnum finishType, int count = 1, int param = 0)
    {
        return HandleFinishType(finishType, new MissionCountEvent(finishType, count, param));
    }

    public async ValueTask HandleAllFinishType(object? arg = null)
    {
        foreach (var handler in FinishTypeHandlers) await HandleFinishType(handler.Key, arg, false);
        await Player.QuestManager!.SyncQuest();
    }

    /// <summary>Returns whether the talk string closed any mission or quest gate.</summary>
    public async ValueTask<bool> HandleTalkStr(string talkString)
    {
        if (!Enabled) return false;

        // Tutorial level graphs report their intermediate Auto talks from inside the run. They are
        // not entry subs and have no predecessor, so arm only the matching reported talk on its floor.
        if (Player.RogueManager?.RogueInstance is { Status: RogueStatus.Doing } run)
            foreach (var tutorialId in new[] { 4030001, 4030002 })
            {
                if (GetMainMissionStatus(tutorialId) != MissionPhaseEnum.Accept ||
                    run.AreaId != 100 || run.MapId != (tutorialId == 4030001 ? 1 : 2)) continue;
                foreach (var sub in GameData.MainMissionData[tutorialId].MissionInfo.SubMissionList)
                    if (sub.TakeType == SubMissionTakeTypeEnum.Auto && sub.FinishType == MissionFinishTypeEnum.Talk
                        && sub.ParamStr1 == talkString && sub.LevelFloorID == Player.Data.FloorId)
                        await AcceptSubMission(sub.ID, false);
            }

        var handled = false;

        foreach (var mission in GetRunningSubMissionList())
            if (mission.FinishType == MissionFinishTypeEnum.Talk)
                if (mission.ParamStr1 == talkString)
                {
                    await FinishSubMission(mission.ID);
                    handled = true;
                }

        foreach (var quest in Player.QuestManager?.GetRunningQuest() ?? [])
        {
            var excel = GameData.QuestDataData.GetValueOrDefault(quest.QuestId);
            if (excel == null) continue;
            var finishWay = GameData.FinishWayData.GetValueOrDefault(excel.FinishWayID);
            if (finishWay == null) continue;
            if (finishWay.FinishType == MissionFinishTypeEnum.Talk)
                if (finishWay.ParamStr1 == talkString)
                {
                    await Player.QuestManager!.FinishQuest(quest.QuestId);
                    handled = true;
                }
        }

        return handled;
    }

    public async ValueTask HandleCustomValue(List<MissionCustomValue> values, int missionId)
    {
        if (!Enabled) return;

        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;
        var mainMissionId = subMission.MainMissionId;
        GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission);
        if (mainMission == null) return;

        var sync = new MissionSync();
        foreach (var mission in mainMission.MissionInfo?.SubMissionList ?? [])
            if (mission.TakeType == SubMissionTakeTypeEnum.CustomValue &&
                GetSubMissionStatus(mission.ID) == MissionPhaseEnum.None)
            {
                var accept = CustomValueStrGateSatisfied(mission, values);
                if (!accept)
                {
                    List<List<int>> list = [mission.TakeParamIntList ?? []];
                    if (mission.TakeParamIntList?.Count > 5)
                    {
                        // every 3 as group
                        var group = mission.TakeParamIntList.Count / 3;
                        list = [];
                        for (var i = 0; i < group; i++)
                        {
                            var customValue = mission.TakeParamIntList.GetRange(i * 3, 3);
                            list.Add(customValue);
                        }
                    }

                    foreach (var customValues in list)
                    {
                        var thisAccept = true;
                        var matched = 0;

                        // The groups are alternatives, so each one restarts at request index 0, and the
                        // index advances per config slot — not only on a match. Sharing one cursor across
                        // groups made the second group of a >5-entry list read request indices 3-5.
                        for (var index = 0; index < customValues.Count; index++)
                        {
                            var customValue = customValues[index];
                            if (customValue == 0 && index == 0) continue; // skip 0
                            var valueInst = values.Find(x => x.Index == index);
                            if (valueInst == null) continue; // a missing request entry does not fail the group
                            if (valueInst.CustomValue != customValue)
                            {
                                thisAccept = false;
                                break;
                            }

                            matched++;
                        }

                        // A group that matched nothing must not accept: `values` is raw client input, so a
                        // request carrying a single unrelated index would otherwise leave thisAccept true
                        // and open every CustomValue-gated sub of the client-chosen main mission.
                        if (thisAccept && matched > 0) accept = true; // accept if any group is satisfied
                    }
                }

                if (!accept) continue;
                MissionSyncBuilder.Merge(sync, await AcceptSubMission(mission.ID, false));
            }

        if (values.Count > 0)
        {
            UpsertCustomValues(mainMissionId, values);
            MissionSyncBuilder.AttachCustomValues(sync, mainMissionId, Data.MainMissionCustomValues[mainMissionId]);
        }

        RefreshSubMissionSync(sync);
        if (sync.MissionList.Count > 0 || sync.NLPKLNDCMOC.Count > 0)
            await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    /// <summary>
    ///     TakeParamStrList triples [name, value, prereqSub] are alternatives. A triple matches when the
    ///     request carries that name at that value and the prerequisite sub is finished.
    /// </summary>
    private bool CustomValueStrGateSatisfied(SubMissionInfo mission, List<MissionCustomValue> values)
    {
        var gates = mission.TakeParamStrList;
        for (var i = 0; i + 2 < gates.Count; i += 3)
        {
            if (!uint.TryParse(gates[i + 1], out var expected)) continue;
            var reported = values.Find(value => value.BGDFHBAANLB == gates[i]);
            if (reported == null || reported.CustomValue != expected) continue;
            if (uint.TryParse(gates[i + 2], out var prerequisite) && prerequisite != 0 &&
                GetSubMissionStatus((int)prerequisite) != MissionPhaseEnum.Finish)
                continue;
            return true;
        }

        return false;
    }

    public void StoreCustomValues(IReadOnlyList<MissionCustomValue> values, int missionId)
    {
        if (!Enabled || values.Count == 0) return;
        if (!GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission)) return;
        UpsertCustomValues(subMission.MainMissionId, values);
        DatabaseHelper.MarkDirty(Player.Uid);
    }

    public async ValueTask<MainMission> UpdateMainMissionCustomValue(int mainMissionId, string key, uint value)
    {
        var incoming = new MissionCustomValue
        {
            BGDFHBAANLB = key ?? string.Empty,
            CustomValue = value
        };
        if (Enabled && mainMissionId > 0)
        {
            UpsertCustomValues(mainMissionId, [incoming]);
            DatabaseHelper.MarkDirty(Player.Uid);
            var sync = new MissionSync();
            if (Data.MainMissionCustomValues.TryGetValue(mainMissionId, out var records))
                MissionSyncBuilder.AttachCustomValues(sync, mainMissionId, records);
            if (sync.NLPKLNDCMOC.Count > 0)
                await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
        }

        var mainProto = new MainMission
        {
            Id = (uint)mainMissionId,
            Status = GetMainMissionStatus(mainMissionId).ToProto()
        };
        if (Data.MainMissionCustomValues.TryGetValue(mainMissionId, out var stored))
            MissionSyncBuilder.FillMainCustomValues(mainProto, stored);
        return mainProto;
    }

    private void UpsertCustomValues(int mainMissionId, IReadOnlyList<MissionCustomValue> values)
    {
        if (!Data.MainMissionCustomValues.TryGetValue(mainMissionId, out var existing))
        {
            existing = [];
            Data.MainMissionCustomValues[mainMissionId] = existing;
        }

        foreach (var incoming in values)
        {
            var name = incoming.BGDFHBAANLB ?? "";
            var index = existing.FindIndex(record =>
                (name.Length > 0 && record.Key == name) || (name.Length == 0 && record.Index == incoming.Index));
            var record = new MissionCustomValueRecord
            {
                Index = incoming.Index,
                CustomValue = incoming.CustomValue,
                Key = name
            };
            if (index >= 0) existing[index] = record;
            else existing.Add(record);
        }
    }

    public async ValueTask AddMissionProgress(int missionId, int progress)
    {
        if (!Enabled || IsCompletedActivitySubMission(missionId)) return;

        Data.SubMissionProgressDict.TryGetValue(missionId, out var currentProgress);
        Data.SubMissionProgressDict[missionId] = currentProgress + progress;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;

        if (currentProgress + progress >= (subMission.SubMissionInfo?.Progress ?? 1)) return;

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Progress = (uint)(currentProgress + progress)
        });

        await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    public async ValueTask SetMissionProgress(int missionId, int progress, bool sendPacket = true)
    {
        if (!Enabled || IsCompletedActivitySubMission(missionId)) return;

        Data.SubMissionProgressDict[missionId] = progress;
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return;

        if (progress >= (subMission.SubMissionInfo?.Progress ?? 1)) return;

        var sync = new MissionSync();
        sync.MissionList.Add(new Proto.Mission
        {
            Id = (uint)missionId,
            Progress = (uint)progress
        });

        if (sendPacket)
            await Player.SendPacket(new PacketPlayerSyncScNotify(sync));
    }

    #endregion

    #region Mission Status

    private bool IsCompletedActivitySubMission(int missionId) => Player.MechCraftManager?.IsCompletedSubMission(missionId) == true ||
        Player.B51RacingManager?.IsCompletedSubMission(missionId) == true;

    public MissionPhaseEnum GetMainMissionStatus(int missionId)
    {
        if (Player.B51RacingManager?.IsCompletedMainMission(missionId) == true) return MissionPhaseEnum.Finish;
        if (!Enabled) return MissionPhaseEnum.Finish;

        return Data.GetMainMissionStatus(missionId);
    }

    public MissionPhaseEnum GetSubMissionStatus(int missionId)
    {
        if (IsCompletedActivitySubMission(missionId)) return MissionPhaseEnum.Finish;
        if (!Enabled) return MissionPhaseEnum.Finish;

        return Data.GetSubMissionStatus(missionId);
    }

    public SubMissionInfo? GetSubMissionInfo(int missionId)
    {
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (subMission == null) return null;
        return subMission.SubMissionInfo;
    }

    public List<int> GetRunningSubMissionIdList()
    {
        if (!Enabled) return [];

        var list = new List<int>();
        list.AddRange(Data.RunningSubMissionIds.Where(id => !IsCompletedActivitySubMission(id)));
        return list;
    }

    public List<SubMissionInfo> GetRunningSubMissionList()
    {
        if (!Enabled) return [];

        var list = new List<SubMissionInfo>();
        var ids = new List<int>();
        ids.AddRange(Data.RunningSubMissionIds.Where(id => !IsCompletedActivitySubMission(id)));
        foreach (var id in ids)
        {
            GameData.SubMissionInfoData.TryGetValue(id, out var mission);
            if (mission != null && mission.SubMissionInfo != null) list.Add(mission.SubMissionInfo);
        }

        return list;
    }

    public int GetMissionProgress(int missionId)
    {
        GameData.SubMissionInfoData.TryGetValue(missionId, out var subMission);
        if (IsCompletedActivitySubMission(missionId)) return subMission?.SubMissionInfo?.Progress ?? 1;
        if (!Enabled) return subMission?.SubMissionInfo?.Progress ?? 0;

        Data.SubMissionProgressDict.TryGetValue(missionId, out var progress);
        return progress;
    }

    /// <summary>
    ///     Resolves which main mission the navigator should track.
    ///     <paramref name="requested" /> is the client's explicit target; anything &lt;= 1 is the
    ///     "unset / auto-select" sentinel the client sends on login and right after a main completes.
    /// </summary>
    /// <remarks>
    ///     Order: explicit request → keep the current track while it is still running → follow the
    ///     just-finished track's NextTrackMainMission → lowest running Main-type mission.
    ///     The last two steps matter after a story beat closes: without them the auto-track lands on
    ///     a hidden Branch/Gap transition (e.g. 1000111, whose only sub has no finishable graph), the
    ///     client renders an empty objective list and the player has nothing to follow.
    ///     The whole order runs twice: once accepting only missions that actually have a running
    ///     sub-mission, then again without that filter. Tracking a running-but-empty main is the other
    ///     way to end up with a blank objective list, and it survives relogs because the stored track is
    ///     kept as long as its mission is merely "accepted".
    /// </remarks>
    public int ResolveTrackingMainMissionId(int requested)
    {
        if (requested > 1 && Player.B51RacingManager?.IsCompletedMainMission(requested) != true) return requested;

        var data = Data;
        var prev = data.TrackingMainMissionId;
        var running = data.RunningMainMissionIds.Where(x => x > 0 && Player.B51RacingManager?.IsCompletedMainMission(x) != true).OrderBy(x => x).ToList();

        for (var pass = 0; pass < 2; pass++)
        {
            var mustHaveWork = pass == 0;

            if (prev > 0 && data.RunningMainMissionIds.Contains(prev) &&
                Player.B51RacingManager?.IsCompletedMainMission(prev) != true &&
                (!mustHaveWork || HasRunningSubMission(prev)))
                return prev;

            if (prev > 0 &&
                GameData.MainMissionData.TryGetValue(prev, out var prevExcel) &&
                prevExcel.NextTrackMainMission > 0 &&
                data.RunningMainMissionIds.Contains(prevExcel.NextTrackMainMission) &&
                Player.B51RacingManager?.IsCompletedMainMission(prevExcel.NextTrackMainMission) != true &&
                (!mustHaveWork || HasRunningSubMission(prevExcel.NextTrackMainMission)))
                return prevExcel.NextTrackMainMission;

            foreach (var id in running)
            {
                // Type is Unknown when the resource cache predates the column — treat that as "no
                // information" and keep the old first-running-mission behaviour instead of skipping.
                if (!GameData.MainMissionData.TryGetValue(id, out var excel)) continue;
                if (excel.Type is not (MainMissionTypeEnum.Unknown or MainMissionTypeEnum.Main)) continue;
                if (mustHaveWork && !HasRunningSubMission(excel)) continue;
                return id;
            }
        }

        return running.FirstOrDefault();
    }

    public int GetTrackMissionAnchorSubMissionId(int mainMissionId)
    {
        if (!GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission)) return 0;

        var runningSubMissions = GetRunningSubMissionIdList().ToHashSet();
        foreach (var subMission in mainMission.MissionInfo.SubMissionList)
        {
            if (!runningSubMissions.Contains(subMission.ID)) continue;
            for (var i = (subMission.TakeParamIntList?.Count ?? 0) - 1; i >= 0; i--)
            {
                var prerequisite = subMission.TakeParamIntList![i];
                if (GetSubMissionStatus(prerequisite) == MissionPhaseEnum.Finish)
                    return prerequisite;
            }
        }

        var finished = mainMission.MissionInfo.SubMissionList
            .Where(x => GetSubMissionStatus(x.ID) == MissionPhaseEnum.Finish)
            .Select(x => x.ID)
            .LastOrDefault();
        if (finished != 0) return finished;
        if (GetMainMissionStatus(mainMissionId) == MissionPhaseEnum.Prepared)
            return PreparedAnchorSubMissionId(mainMissionId);
        return 0;
    }

    /// <summary>
    ///     Floor a main mission wants the player on: the first running sub that names one, otherwise the
    ///     tracked anchor sub. When the mission's own subs carry no floor (a transition row), the search
    ///     walks back along NextTrackMainMission to the mission that leads into it. 0 = unknown.
    /// </summary>
    public int GetMissionFloorId(int mainMissionId)
    {
        var visited = new HashSet<int>();
        while (mainMissionId > 0 && visited.Add(mainMissionId))
        {
            var floor = GetOwnFloorId(mainMissionId);
            if (floor != 0) return floor;

            mainMissionId = GameData.MainMissionData.Values
                .FirstOrDefault(x => x.NextTrackMainMission == mainMissionId)?.MainMissionID ?? 0;
        }

        return 0;
    }

    private int GetOwnFloorId(int mainMissionId)
    {
        if (!GameData.MainMissionData.TryGetValue(mainMissionId, out var mainMission)) return 0;

        foreach (var subId in GetRunningSubMissionIdList())
        {
            if (!GameData.SubMissionInfoData.TryGetValue(subId, out var sub)) continue;
            if (sub.MainMissionId != mainMissionId) continue;
            if (sub.SubMissionInfo is { LevelFloorID: > 0 } running) return running.LevelFloorID;
        }

        var anchorId = GetTrackMissionAnchorSubMissionId(mainMissionId);
        if (anchorId > 0 && GetSubMissionInfo(anchorId) is { LevelFloorID: > 0 } anchor) return anchor.LevelFloorID;

        // Nothing running yet (the mission was only just accepted): fall back to the first sub that
        // declares a floor at all, so /mission goto still lands somewhere sensible.
        return mainMission.MissionInfo.SubMissionList
            .Select(x => x.LevelFloorID)
            .FirstOrDefault(x => x > 0);
    }

    /// <summary>Map entrance to use when teleporting to a floor, preferring one with a start anchor.</summary>
    public static MapEntranceExcel? FindEntranceForFloor(int floorId)
    {
        if (floorId == 0) return null;

        var candidates = GameData.MapEntranceData.Values
            .Where(x => x.FloorID == floorId)
            .OrderBy(x => x.ID)
            .ToList();

        return candidates.FirstOrDefault(x => x.StartAnchorID > 0) ?? candidates.FirstOrDefault();
    }

    public async ValueTask EnsureRunningKillMonsterTargetsVisible()
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        foreach (var mission in GetRunningSubMissionList())
        {
            await EnsureKillMonsterTargetVisible(mission, true);
        }
    }

    private async ValueTask SyncStartedKillMonsterSceneSnapshot(MissionSync sync)
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        var shouldSyncScene = false;
        foreach (var missionSync in sync.MissionList)
        {
            if (missionSync.Status != MissionStatus.MissionDoing) continue;
            if (!GameData.SubMissionInfoData.TryGetValue((int)missionSync.Id, out var subMission)) continue;
            if (subMission.SubMissionInfo is not { } mission) continue;
            if (scene.HasClientSceneSnapshotGroup(mission.ParamInt1)) continue;

            shouldSyncScene = await EnsureKillMonsterTargetVisible(mission, false) || shouldSyncScene;
        }

        if (!shouldSyncScene) return;

        await Player.SendPacket(new PacketEnterSceneByServerScNotify(scene));
    }

    private async ValueTask<bool> EnsureKillMonsterTargetVisible(SubMissionInfo mission, bool sendRefresh)
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return false;
        if (mission.FinishType != MissionFinishTypeEnum.KillMonster) return false;
        if (mission.LevelFloorID != scene.FloorId) return false;

        var targetGroupId = mission.ParamInt1;
        var targetInstId = mission.ParamInt2;
        if (targetGroupId <= 0 || targetInstId <= 0) return false;

        var groupEntities = scene.Entities.Values
            .Where(x => x.GroupId == targetGroupId)
            .ToList();

        if (groupEntities.Count == 0)
        {
            groupEntities = await scene.EntityLoader.LoadGroup(targetGroupId, false) ?? [];
        }

        if (groupEntities.Count == 0) return false;

        var target = groupEntities
            .OfType<EntityMonster>()
            .FirstOrDefault(x => x.InstId == targetInstId);
        if (target is not { IsAlive: true }) return false;

        if (sendRefresh)
            await Player.SendPacket(scene.HasClientSceneSnapshotGroup(targetGroupId)
                ? new PacketSceneGroupRefreshScNotify(Player, groupEntities)
                : new PacketSceneGroupRefreshScNotify(scene, [targetGroupId]));

        return true;
    }

    private async ValueTask LoadRunningMissionGroupsForCurrentFloor()
    {
        var scene = Player.SceneInstance;
        if (scene?.EntityLoader == null) return;

        foreach (var mission in GetRunningSubMissionList())
        {
            if (mission.LevelFloorID != scene.FloorId) continue;
            foreach (var group in mission.GroupIDList ?? [])
                await scene.EntityLoader.LoadGroup(group);
        }
    }

    #endregion

    #region Handlers

    public async ValueTask OnBattleFinish(BattleInstance instance, PVEBattleResultCsReq req)
    {
        foreach (var mission in GetRunningSubMissionIdList())
        {
            var subMission = GetSubMissionInfo(mission);
            if (subMission != null && subMission.FinishType == MissionFinishTypeEnum.StageWin &&
                req.EndStatus == BattleEndStatus.BattleEndWin) // TODO: Move to handler
                if (req.StageId.ToString().StartsWith(subMission.ParamInt1.ToString()))
                {
                    await FinishSubMission(mission);
                    instance.EventId = 0;
                }
        }

        await HandleAllFinishType(instance);
    }

    public async ValueTask OnPlayerInteractWithProp()
    {
        foreach (var id in GetRunningSubMissionIdList())
            if (GetSubMissionInfo(id)?.FinishType == MissionFinishTypeEnum.PropState)
            {
                FinishTypeHandlers.TryGetValue(MissionFinishTypeEnum.PropState, out var handler);
                if (handler != null) await handler.HandleMissionFinishType(Player, GetSubMissionInfo(id)!, null);
            }
    }

    public async ValueTask OnPlayerChangeScene()
    {
        foreach (var id in GetRunningSubMissionIdList())
        {
            var info = GetSubMissionInfo(id);
            if (info == null) continue;

            if (info.LevelFloorID == Player.SceneInstance?.FloorId)
            {
                if (info.GroupIDList == null) continue;
                foreach (var group in info.GroupIDList)
                    await Player.SceneInstance.EntityLoader!.LoadGroup(group, false);
            }
        }

        await ApplyRunningMissionPropStates();
    }

    /// <summary>
    ///     Re-applies the ChangePropState level task of every RUNNING sub on the current floor whose graph
    ///     carries the task - PropState subs (target in ParamInt1/ParamInt2) and story Talk subs whose graph
    ///     names the target itself (LevelTask.ResolveMissionPropTarget).
    /// </summary>
    /// <remarks>
    ///     The task normally runs once, from AcceptSubMission, against whatever scene happened to be
    ///     loaded at that moment. That leaves two ways for a mission to become impossible: the sub was
    ///     accepted while the player stood on another floor, so the loop matched no entity; or the player
    ///     relogged / teleported away and back, and the rebuilt scene restored the prop's configured
    ///     Locked state. Either way the console the objective depends on never becomes interactable, the
    ///     PropState gate never fires, the main mission never finishes, and its successor never enters the
    ///     player's mission list. Reconciling on every scene build closes both, and StepToClosedOrNext
    ///     makes the re-application a no-op once the prop is already where the mission wants it.
    /// </remarks>
    public async ValueTask ApplyRunningMissionPropStates()
    {
        if (!Enabled) return;

        var scene = Player.SceneInstance;
        if (scene == null) return;

        var levelTask = Player.TaskManager?.LevelTask;
        if (levelTask == null) return;

        foreach (var id in GetRunningSubMissionIdList())
        {
            if (!GameData.SubMissionInfoData.TryGetValue(id, out var subMission)) continue;
            var info = subMission.SubMissionInfo;
            if (info == null) continue;
            if (info.LevelFloorID != 0 && info.LevelFloorID != scene.FloorId) continue;
            if (!GraphRunsChangePropState(subMission)) continue;

            await levelTask.ApplyMissionPropState(subMission);
        }
    }

    // Only re-apply for subs whose own level graph actually carries the task, so a PropState sub that
    // never asked for a prop change is left alone.
    private static bool GraphRunsChangePropState(SubMissionData subMission)
    {
        var graph = subMission.SubMissionTaskInfo;
        if (graph == null) return false;

        return graph.OnInitSequece.SelectMany(x => x.TaskList)
            .Concat(graph.OnStartSequece.SelectMany(x => x.TaskList))
            .Any(x => x.Type.EndsWith("ChangePropState", StringComparison.Ordinal));
    }

    public void OnLoadScene(SceneInfo info)
    {
        info.SceneMissionInfo = BuildSceneMissionInfo(Player.SceneInstance?.FloorInfo,
            Player.SceneInstance?.FloorId ?? 0);
    }

    public MissionStatusBySceneInfo BuildSceneMissionInfo(FloorInfo? floorInfo, int floorId)
    {
        var missionInfo = new MissionStatusBySceneInfo();
        HashSet<int> targetSubIds = [];
        HashSet<int> mainIds = [];
        HashSet<int> subIds = [];

        // Client groups have no server entities. Their load/unload conditions are the mission IDs
        // the client needs for spawning and triggering them, even when those missions run on other floors.
        foreach (var group in floorInfo?.Groups.Values.Where(x => x.LoadSide == GroupLoadSideEnum.Client) ?? [])
        {
            targetSubIds.UnionWith(group.RelatedMissionId);
            foreach (var condition in group.LoadCondition.Conditions.Concat(group.UnloadCondition.Conditions)
                         .Concat(group.ForceUnloadCondition.Conditions))
                if (condition.Type == LevelGroupMissionTypeEnum.SubMission)
                    targetSubIds.Add(condition.ID);
                else
                    mainIds.Add(condition.ID);
        }

        void AddSubMissionStatus(SubMissionInfo subMission, int mainMissionId)
        {
            if (!subIds.Add(subMission.ID)) return;

            missionInfo.SubMissionStatusList.Add(new Proto.Mission
            {
                Id = (uint)subMission.ID,
                Status = GetSubMissionStatus(subMission.ID).ToProto(),
                Progress = (uint)GetMissionProgress(subMission.ID)
            });

            mainIds.Add(mainMissionId);
        }

        foreach (var mainMission in GameData.MainMissionData.Values)
        foreach (var subMission in mainMission.MissionInfo.SubMissionList)
            if (targetSubIds.Contains(subMission.ID))
                AddSubMissionStatus(subMission, mainMission.MainMissionID);

        // Official 4.6 floor 10001001 ships exactly this client-group-derived set (32 subs / 50 mains):
        // it does NOT enumerate every mission authored on the floor. The running story talk 100030103 is
        // absent from the entry snapshot and only reaches the client through the mission sync flow.
        foreach (var mainId in mainIds)
            if (GetMainMissionStatus(mainId) == MissionPhaseEnum.Finish)
                missionInfo.FinishedMainMissionIdList.Add((uint)mainId);
            else
                missionInfo.UnfinishedMainMissionIdList.Add((uint)mainId);

        // Activity-bound main missions: for each ContentPackage whose entrance lives on the
        // floor the player is loading, surface its main missions as "unfinished" so the client
        // treats the activity entry (map icons, in-scene activity NPCs) as live. We keep the
        // regular Mission system untouched - this only affects scene-mission serialization.
        if (floorId != 0)
            foreach (var pkg in GameData.ContentPackageConfigData.Values)
            {
                if (pkg.MainMissionIDList.Count == 0) continue;
                if (!pkg.TouchesFloor(floorId)) continue;

                foreach (var missionId in pkg.MainMissionIDList)
                {
                    var mid = (uint)missionId;
                    if (missionInfo.FinishedMainMissionIdList.Contains(mid)) continue;
                    if (missionInfo.UnfinishedMainMissionIdList.Contains(mid)) continue;
                    missionInfo.UnfinishedMainMissionIdList.Add(mid);
                }
            }

        return missionInfo;
    }

    #endregion
}

/// <summary>Outcome of <see cref="MissionManager.RepairStalledMainMissions" />.</summary>
public class MissionRepairReport
{
    /// <summary>Stalled mains that got a runnable sub-mission back.</summary>
    public List<int> Resumed { get; } = [];

    /// <summary>Stalled mains whose finish gate was already satisfied, so they were closed.</summary>
    public List<int> Closed { get; } = [];

    /// <summary>Accepted mains that are still empty: their graph is missing or their gate is unreachable.</summary>
    public List<int> Stalled { get; } = [];

    /// <summary>New tracked main mission, or 0 when tracking was left alone.</summary>
    public int RetrackedTo { get; set; }

    public bool Changed => Resumed.Count > 0 || Closed.Count > 0 || RetrackedTo > 0;
}
