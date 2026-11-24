using March7thHoney.Data;
using March7thHoney.Data.Config.Scene;
using March7thHoney.Enums;
using March7thHoney.Enums.Mission;
using March7thHoney.Enums.Scene;
using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.GameServer.Server.Packet.Send.Scene;
using March7thHoney.Util;

namespace March7thHoney.GameServer.Game.Scene;

public class SceneEntityLoader(SceneInstance scene)
{
    private static bool Is45Prod => GameConstants.IsVersion("4.5.0");

    // 4.5 Xianzhou compatibility exclusions. PCOnly captures are not the only problematic props:
    // narrowing the earlier Android fix to PCOnly reintroduced the Changle Tian loading failure.
    // Match exact cached instances; LoadOnInitial=false is also used by ordinary usable props.
    private static readonly HashSet<(int FloorId, int GroupId, int InstId)> IncompatibleProps =
    [
        (10201001, 280, 300001), // Chap02_CloseView_ADV_X01_StarSkiff_11_PCOnly_BlockData / TeaSet0
        (10202001, 144, 300003), // Noninitial CommandRobotPuzzle prop from the earlier Android fix
        (10202001, 208, 300001), // Chap02_CloseView_ADV_X02_Sanctum_01_PCOnly_BlockData / UmbrellaGroup0
        (10202001, 208, 300002)  // Noninitial StageItemsHandler capture from the earlier Android fix
    ];

    // Props whose authored InitLevelGraph override does not ship with the client. Creating one makes the
    // client's async MapObject load throw (NullReferenceException in LevelGraphConfig.FromBinary after
    // "SyncLoadAsset fail ... DesignerBackup/TriggerEvent.bytes") and the scene never leaves the loading page.
    // Version-independent: read from the 4.7 beta client while entering Anomaly Arbitration group 10.
    private static readonly HashSet<(int FloorId, int GroupId, int InstId)> MissingLevelGraphProps =
    [
        (30146001, 16, 300001) // RoomStateChange / Prop1 (MazeProp 100, Config/Level/DesignerBackup/TriggerEvent.json)
    ];

    // Restore only the known problematic Reading atmosphere group. Other atmosphere/Hoyo groups
    // and floor-wide activity defaults remain available; those flags alone are not exclusions.
    private bool IsIncompatibleGroup(GroupInfo group) =>
        Is45Prod && Scene.FloorId == 10202001 && group.Id == 169;

    private bool IsIncompatibleProp(GroupInfo group, PropInfo info) =>
        MissingLevelGraphProps.Contains((Scene.FloorId, group.Id, info.ID)) ||
        Is45Prod && IncompatibleProps.Contains((Scene.FloorId, group.Id, info.ID));

    // The Astral Express boarding prop stays Closed until main 1000401 finishes.
    private const int StarTrainArrivalMain = 1000401;

    // Simulated Universe lobby. Inert server groups on this plane are client scenery.
    private const int SuLobbyPlaneId = 80112;

    private bool DropInertServerGroups() =>
        Scene.PlaneId == SuLobbyPlaneId || Scene.Excel.PlaneType == PlaneTypeEnum.Raid;

    private bool RejectGroup(GroupInfo group) =>
        group.OnlyEditorBackupProps() || group.OrphanedMissionGroup() ||
        DropInertServerGroups() && group.InertServerGroup();

    private bool IsRogueLobbyConsole(GroupInfo group, PropInfo info, int propId) =>
        // The 4.6 cache identifies these two lobby consoles by their authored scene identities even when
        // MazeProp.JsonPath is absent. Keep the fallback exact so unrelated Rogue doors/chests stay closed.
        Scene.FloorId == 10341001 && group.Id == 13 &&
        (info.ID, propId) is (300001, 1025) or (300002, 1032);

    public SceneInstance Scene { get; set; } = scene;
    public List<int> LoadGroups { get; set; } = [];

    /// <summary>
    ///     False for scenes whose props are rebuilt per run: their state is neither restored from nor written to
    ///     the player's floor prop data.
    /// </summary>
    public virtual bool PersistsPropState => true;

    public virtual async ValueTask LoadEntity()
    {
        if (Scene.IsLoaded) return;

        var dimInfo = Scene.FloorInfo?.DimensionList.Find(x => x.ID == 0);
        if (dimInfo == null) return;
        LoadGroups.AddRange(dimInfo.GroupIDList);

        foreach (var group in from @group in Scene.FloorInfo?.Groups.Values!
                 where @group.LoadSide != GroupLoadSideEnum.Client
                 where !@group.GroupName.Contains("DeployPuzzle_Repeat_Area")
                 where !@group.GroupName.Contains("TrainVisitor")
                 where !@group.GroupName.Contains("TrainVisiter")
                 select @group) await LoadGroup(group);

        if (Scene.Excel.PlaneType == PlaneTypeEnum.Rogue)
            await Scene.ReapplyFinishedRoguePropUnlocks();
        await Scene.RepairFanOutFogDoors();
        if (Scene.Excel.PlaneType == PlaneTypeEnum.Raid)
            await Scene.ReopenFinishedRaidPortals();

        Scene.IsLoaded = true;
    }

    public virtual async ValueTask SyncEntity()
    {
        // Empty runtime groups (for example StageItemsHandler) are still loaded groups.
        // Deriving membership only from entities reloads them on every sync and duplicates Groups.
        var oldGroupId = new HashSet<int>(Scene.Groups);
        foreach (var entity in Scene.Entities.Values.Where(entity => !oldGroupId.Contains(entity.GroupId)))
            oldGroupId.Add(entity.GroupId);

        var unloadedGroups = new List<int>();
        var loadedGroups = new List<int>();

        foreach (var group in Scene.FloorInfo!.Groups.Values
                     .Where(group => group.LoadSide != GroupLoadSideEnum.Client)
                     .Where(group => !group.GroupName.Contains("TrainVisitor"))
                     .Where(group => !group.GroupName.Contains("DeployPuzzle_Repeat_Area"))
                     .Where(group => !group.GroupName.Contains("TrainVisiter")))

            if (oldGroupId.Contains(group.Id)) // check if it should be unloaded
            {
                // DelayToLevelReload defers an unload to the next level (re)load: the group must stay
                // in the scene the player is currently standing in even after its condition turns true.
                // Ground truth: floor 80112001 group 2 (Herta) unloads on main 1000401 Finish with
                // DelayToLevelReload set — the official server keeps her standing so the follow-on
                // 4030001 talks can fire, and only drops her on the next entry to the floor. LoadGroup
                // still evaluates the same condition at build time, so the deferred unload applies
                // naturally on the next scene build. "Force" overrides the delay by definition.
                var forceUnload = !group.ForceUnloadCondition.DelayToLevelReload &&
                                  group.ForceUnloadCondition.IsTrue(Scene.Player.MissionManager!.Data, false);
                var unload = !group.UnloadCondition.DelayToLevelReload &&
                             group.UnloadCondition.IsTrue(Scene.Player.MissionManager!.Data, false);

                if (forceUnload || unload || IsIncompatibleGroup(group) || RejectGroup(group))
                {
                    foreach (var entity in Scene.Entities.Values.Where(entity => entity.GroupId == group.Id))
                    {
                        await Scene.RemoveEntity(entity, false);
                    }

                    Scene.Groups.Remove(group.Id);
                    unloadedGroups.Add(group.Id);
                }
                else if (group.OwnerMainMissionID != 0 &&
                         Scene.Player.MissionManager!.GetMainMissionStatus(group.OwnerMainMissionID) !=
                         MissionPhaseEnum.Accept) // condition: Owner Main Mission ID
                {
                    foreach (var entity in Scene.Entities.Values.Where(entity => entity.GroupId == group.Id))
                    {
                        await Scene.RemoveEntity(entity, false);
                    }

                    Scene.Groups.Remove(group.Id);
                    unloadedGroups.Add(group.Id);
                }
                else if (!group.SavedValueCondition.IsTrue(
                             Scene.BuildClientFloorSavedData())) // same dimension defaults as the scene snapshot
                {
                    foreach (var entity in Scene.Entities.Values.Where(entity => entity.GroupId == group.Id))
                    {
                        await Scene.RemoveEntity(entity, false);
                    }

                    Scene.Groups.Remove(group.Id);
                    unloadedGroups.Add(group.Id);
                }
                else if (group.RelatedBattleId.Count > 0 && !Scene.Player.MissionManager!.GetRunningSubMissionList()
                             .Any(x =>
                                 x.FinishType == MissionFinishTypeEnum.StageWin &&
                                 group.RelatedBattleId.Contains(x.ParamInt1)))
                {
                    foreach (var entity in Scene.Entities.Values.Where(entity => entity.GroupId == group.Id))
                    {
                        await Scene.RemoveEntity(entity, false);
                    }

                    Scene.Groups.Remove(group.Id);
                    unloadedGroups.Add(group.Id);
                }
            }
            else // check if it should be loaded
            {
                var groupList = await LoadGroup(group);
                if (groupList != null) loadedGroups.Add(group.Id);
            }

        if (loadedGroups.Count > 0 || unloadedGroups.Count > 0)
            await Scene.Player.SendPacket(new PacketSceneGroupRefreshScNotify(Scene, loadedGroups, unloadedGroups));
    }

    public virtual async ValueTask<List<BaseGameEntity>?> LoadGroup(GroupInfo info, bool forceLoad = false)
    {
        if (!LoadGroups.Contains(info.Id)) return null; // check if group should be loaded in this dimension
        var missionData = Scene.Player.MissionManager!.Data; // get mission data
        if (info.LoadSide == GroupLoadSideEnum.Client) return null; // check if group should be loaded on client side
        if (RejectGroup(info) || IsIncompatibleGroup(info)) return null;
        if (info.GroupName.Contains("TrainVisitor")) return null; // certain group name
        if (info.GroupName.Contains("DeployPuzzle_Repeat_Area")) return null;
        if (info.GroupName.Contains("TrainVisiter")) return null;

        if (info.SystemUnlockCondition != null) // condition: System Unlock Condition
        {
            var result = info.SystemUnlockCondition.Operation != OperationEnum.Or; // operation
            foreach (var conditionId in info.SystemUnlockCondition.Conditions)
            {
                GameData.GroupSystemUnlockDataData.TryGetValue(conditionId, out var unlockExcel);
                if (unlockExcel == null) continue;
                var part = Scene.Player.QuestManager?.UnlockHandler.GetUnlockStatus(unlockExcel.UnlockID) ??
                           false; // check if unlock condition is met
                if (info.SystemUnlockCondition.Operation == OperationEnum.Or && part)
                {
                    result = true;
                    break;
                }

                if (info.SystemUnlockCondition.Operation == OperationEnum.And && !part)
                {
                    result = false;
                    break;
                }

                if (info.SystemUnlockCondition.Operation != OperationEnum.Not || !part) continue;
                result = false;
                break;
            }

            if (!result) return null;
        }

        if (!(info.OwnerMainMissionID == 0 || // condition: Owner Main Mission ID
              Scene.Player.MissionManager!.GetMainMissionStatus(info.OwnerMainMissionID) ==
              MissionPhaseEnum.Accept)) return null; // check if main mission is accepted

        if ((!info.LoadCondition.IsTrue(missionData) ||
             info.UnloadCondition.IsTrue(missionData,
                 false) || // condition: Load Condition, Unload Condition, Force Unload Condition
             info.ForceUnloadCondition.IsTrue(missionData, false)) &&
            !forceLoad) return null; // check if group should be loaded forcefully

        if (!info.SavedValueCondition.IsTrue(Scene.BuildClientFloorSavedData()) &&
            !forceLoad) // condition: Saved Value Condition
            return null;

        if (Scene.Groups.Contains(info.Id) || Scene.Entities.Values.Any(x => x.GroupId == info.Id))
            return null;

        if (info.RelatedBattleId.Count > 0 && !Scene.Player.MissionManager!.GetRunningSubMissionList().Any(x =>
                x.FinishType == MissionFinishTypeEnum.StageWin && info.RelatedBattleId.Contains(x.ParamInt1)))
            return null; // mission not activated

        // TODO atmosphere conditions

        // load
        Scene.Groups.Add(info.Id); // add group to loaded groups

        var entityList = new List<BaseGameEntity>();
        foreach (var npc in info.NPCList)
            try
            {
                if (await LoadNpc(npc, info) is { } entity) entityList.Add(entity);
            }
            catch
            {
                // ignored
            }

        foreach (var monster in info.MonsterList)
            try
            {
                if (await LoadMonster(monster, info) is { } entity) entityList.Add(entity);
            }
            catch
            {
                // ignored
            }

        // Keep compatibility with ordinary noninitial props; compatibility exclusions live in LoadProp
        // so direct creation and forced group activation use the same rule as initial loading.
        foreach (var prop in info.PropList)
            try
            {
                if (await LoadProp(prop, info) is { } entity) entityList.Add(entity);
            }
            catch
            {
                // ignored
            }

        return entityList;
    }

    public virtual async ValueTask<List<BaseGameEntity>?> LoadGroup(int groupId, bool sendPacket = true)
    {
        var group = Scene.FloorInfo?.Groups.TryGetValue(groupId, out var v1) == true ? v1 : null;
        if (group == null) return null;
        var entities = await LoadGroup(group, true);

        if (sendPacket && entities != null)
            await Scene.Player.SendPacket(new PacketSceneGroupRefreshScNotify(Scene, [groupId]));

        return entities;
    }

    public virtual async ValueTask UnloadGroup(int groupId)
    {
        var group = Scene.FloorInfo?.Groups.TryGetValue(groupId, out var v1) == true ? v1 : null;
        if (group == null) return;

        var refreshed = Scene.Groups.Contains(groupId);
        var removed = new List<BaseGameEntity>();

        foreach (var entity in Scene.Entities.Values)
            if (entity.GroupId == group.Id)
            {
                removed.Add(entity);
                refreshed = true;
            }

        foreach (var entity in removed)
            await Scene.RemoveEntity(entity, false);

        Scene.Groups.Remove(group.Id);

        if (refreshed)
            await Scene.Player.SendPacket(new PacketSceneGroupRefreshScNotify(Scene, [], [groupId]));

        // A client-side group is not in the scene's server group list, so a group-level UNLOAD leaves
        // its entities on screen. Delete them explicitly. Server groups keep the group-UNLOAD shape.
        if (group.LoadSide != GroupLoadSideEnum.Server && removed.Count > 0)
            await Scene.Player.SendPacket(new PacketSceneGroupRefreshScNotify(Scene.Player, null, removed));
    }

    public virtual async ValueTask<EntityNpc?> LoadNpc(NpcInfo info, GroupInfo group, bool sendPacket = false)
    {
        // Like props, NPCs have no later server-side creation path. Some ordinary server NPC exports
        // incorrectly say LoadOnInitial=false, so let the containing group's lifecycle own visibility.
        if (info.IsClientOnly || info.IsDelete || IsIncompatibleGroup(group)) return null;

        if (!GameData.NpcDataData.ContainsKey(info.NPCID)) return null;

        EntityNpc npc = new(Scene, group, info);
        await Scene.AddEntity(npc, sendPacket);

        return npc;
    }

    public virtual async ValueTask<EntityMonster?> LoadMonster(MonsterInfo info, GroupInfo group,
        bool sendPacket = false)
    {
        if (info.IsClientOnly || info.IsDelete || IsIncompatibleGroup(group)) return null;
        if (Scene.MonsterStaysDefeated(group, info)) return null;

        GameData.NpcMonsterDataData.TryGetValue(info.NPCMonsterID, out var excel);
        if (excel == null) return null;

        EntityMonster entity = new(Scene, info.ToPositionProto(), info.ToRotationProto(), group.Id, info.ID, excel,
            info);
        await Scene.AddEntity(entity, sendPacket);
        return entity;
    }

    public virtual async ValueTask<EntityProp?> LoadProp(PropInfo info, GroupInfo group, bool sendPacket = false)
    {
        // IsClientOnly/LoadOnInitial alone are not server-prop exclusions: runtime exports also set
        // them on usable server props. Keep exclusions consistent for direct and group creation.
        if (info.IsClientOnly || info.IsDelete || IsIncompatibleGroup(group) || IsIncompatibleProp(group, info)) return null;

        GameData.MazePropData.TryGetValue(info.PropID, out var excel);
        if (excel == null) return null;

        var prop = new EntityProp(Scene, excel, group, info);

        if (excel.PropType == PropTypeEnum.PROP_SPRING) Scene.HealingSprings.Add(prop);

        // load from database
        var propData = PersistsPropState ? Scene.Player.GetScenePropData(Scene.FloorId, group.Id, info.ID) : null;
        var hasSavedState = propData != null && Scene.Excel.PlaneType != PlaneTypeEnum.Raid && // raid is not saved
                            ScenePropRules.SavedStateAllowed(excel, propData.State);
        if (hasSavedState)
        {
            prop.State = propData!.State;
        }
        else
        {
            if (Scene.Excel.PlaneType == PlaneTypeEnum.Raid)
                prop.State = info.State;
            else
                // elevator
                prop.State = prop.Excel.PropType == PropTypeEnum.PROP_ELEVATOR ? PropStateEnum.Elevator1 : info.State;

            // The Express stays off the prologue platform until main 1000401 finishes. Missions off
            // keeps the docked train (the same Open the lite scene ships).
            if (group.GroupName.Contains("StarTrain", StringComparison.Ordinal))
            {
                var arrived = !Scene.Player.MissionEnabled ||
                              Scene.Player.MissionManager!.GetMainMissionStatus(StarTrainArrivalMain) ==
                              MissionPhaseEnum.Finish;
                prop.State = arrived ? PropStateEnum.Open : info.State;
            }

            // Authored Locked with a Closed branch and no Locked branch never arms an interact button.
            if (prop.State == PropStateEnum.Locked && info.GraphHasRealClosedBranch && !info.GraphHasLockedBranch)
                prop.State = PropStateEnum.Closed;
        }

        if (!hasSavedState)
        {
            // Rogue lobby exports may omit State even though the same props are Open on their other
            // floors. Their client graphs expose the Rogue/RogueTourn entry button only in Open state.
            // Keep this scoped to the lobby-console assets; Rogue doors and chests retain their state.
            if (IsRogueLobbyConsole(group, info, excel.ID) ||
                prop.State == PropStateEnum.Closed &&
                (excel.JsonPath.Contains("LobbyEntrance", StringComparison.Ordinal) ||
                 excel.JsonPath.Contains("RougueEmblem", StringComparison.Ordinal) ||
                 excel.JsonPath.Contains("RogueEmblem", StringComparison.Ordinal)))
                prop.State = PropStateEnum.Open;

            // The gallery unlocks after the final Herta dialogue, before the use-handbook objective
            // can complete the tutorial. Waiting for main 4030002 to finish creates a circular gate.
            if (prop.State == PropStateEnum.Closed &&
                excel.JsonPath.Contains("Prop_Rogue_Gallery", StringComparison.Ordinal) &&
                Scene.Player.MissionManager!.IsRogueGalleryUnlocked)
                prop.State = PropStateEnum.Open;

            // A spring's checkpoint comes from its configured State when the level export declares one
            // (CheckPointDisable keeps it hidden until a mission or the player enables it - official 20002001
            // sends 300006/8 and 300021/300022/300007/7) and defaults to CheckPointEnable otherwise. Healing stays
            // PropType-based, so a disabled spring still heals on proximity.
            if (excel.PropType == PropTypeEnum.PROP_SPRING &&
                prop.State is not (PropStateEnum.CheckPointDisable or PropStateEnum.CheckPointEnable))
                prop.State = PropStateEnum.CheckPointEnable;

            // Story off: every teleport waypoint is usable from the start (the pre-story-mode behaviour, kept
            // per scene instead of being baked into the shared config).
            if (!Scene.Player.MissionEnabled && info.MappingInfoID != 0 && prop.State == PropStateEnum.CheckPointDisable)
                prop.State = PropStateEnum.CheckPointEnable;
        }

        // Exact lobby identities also cover exports without JsonPath and old saved Closed states.
        if (Scene.FloorId == 80112001 && group.Id == 3 && info.ID is 300003 or 300008 &&
            excel.ID == 1005 && Scene.Player.MissionManager!.IsRogueGalleryUnlocked)
            prop.State = PropStateEnum.Open;

        // Missions off: open doors so they don't block the way.
        if (!Scene.Player.MissionEnabled && !hasSavedState && prop.Excel.IsDoor)
            prop.State = PropStateEnum.Open;

        var timelineData = Scene.Player.GetScenePropTimelineData(Scene.FloorId, group.Id, info.ID);
        prop.PropTimelineData = timelineData;

        if (group.GroupName.Contains("Machine"))
        {
            await prop.SetState(PropStateEnum.Open);
            await Scene.AddEntity(prop, sendPacket);
            return prop;
        }

        // Only apply default "Case -> Closed" normalization when there is no persisted state.
        if (!hasSavedState && prop.PropInfo.Name.Contains("Case") && prop.State == PropStateEnum.Open)
            await prop.SetState(PropStateEnum.Closed);

        // The tutorial console (mapping 2219) is an ordinary scene entity too. Returning it before
        // AddEntity used to leave group 14 loaded but empty, so its RogueStart(area 100) button vanished.
        if (prop.PropInfo.PropID == 1003 && prop.PropInfo.MappingInfoID == 2220 && !hasSavedState &&
            (!Scene.Player.MissionEnabled ||
             Scene.Player.MissionManager!.GetMainMissionStatus(4030002) == MissionPhaseEnum.Finish))
        {
            await prop.SetState(PropStateEnum.Open);
        }

        if (prop.PropInfo.PropID is 104006 or 104005) await prop.SetState(PropStateEnum.Open);

        // Destructibles that were already opened should not respawn on reload.
        if (prop.Excel.PropType == PropTypeEnum.PROP_DESTRUCT && prop.State == PropStateEnum.Open) return null;

        await Scene.AddEntity(prop, sendPacket);

        return prop;
    }
}
