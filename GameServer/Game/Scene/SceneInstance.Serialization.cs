using March7thHoney.Data;
using March7thHoney.Data.Config.Scene;
using March7thHoney.Data.Excel;
using March7thHoney.Database.Avatar;
using March7thHoney.Enums.Avatar;
using March7thHoney.Enums.Mission;
using March7thHoney.Enums.Scene;
using March7thHoney.GameServer.Game.Activity.Loaders;
using March7thHoney.GameServer.Game.Battle;
using March7thHoney.GameServer.Game.Mission;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Game.Player.Components;
using March7thHoney.GameServer.Game.Scene.Component;
using March7thHoney.GameServer.Game.Scene.Entity;
using March7thHoney.GameServer.Server.Packet.Send.Scene;
using March7thHoney.Proto;
using March7thHoney.Util;

namespace March7thHoney.GameServer.Game.Scene;

public partial class SceneInstance
{
    #region Serialization

    private const int InheritWorldId = 999;

    public int ResolveWorldId()
    {
        var worldId = Excel.WorldID;
        // Trial planes use the 999 "inherit" world; the official client receives the world the trial was
        // entered from instead.
        if (worldId == InheritWorldId && Excel.PlaneType == PlaneTypeEnum.TrialActivity &&
            Player.ActivityManager?.Data.TrialActivityData.ReturnEntryId is { } returnEntryId and not 0 &&
            GameData.MapEntranceData.TryGetValue(returnEntryId, out var entrance) &&
            GameData.MazePlaneData.TryGetValue(entrance.PlaneID, out var returnPlane) &&
            returnPlane.WorldID != InheritWorldId)
            worldId = returnPlane.WorldID;

        return worldId == 100 ? GameConstants.LAST_TRAIN_WORLD_ID : worldId;
    }

    public int ResolveDimensionId()
    {
        if (EntityLoader is StoryLineEntityLoader loader && loader.DimensionId != 0)
            return loader.DimensionId;

        return ResolveDimensionId(FloorInfo, EntryId, Groups);
    }

    public static int ResolveDimensionId(FloorInfo? floorInfo, int entryId = 0, IEnumerable<int>? preferredGroupIds = null)
    {
        if (floorInfo == null)
            return 0;

        HashSet<int> candidateGroupIds = [];
        if (entryId != 0
            && GameData.MapEntranceData.TryGetValue(entryId, out var entrance)
            && entrance.FloorID == floorInfo.FloorID)
            candidateGroupIds.Add(entrance.StartGroupID);

        if (floorInfo.StartGroupID != 0)
            candidateGroupIds.Add(floorInfo.StartGroupID);

        if (preferredGroupIds != null)
            foreach (var groupId in preferredGroupIds.Where(groupId => groupId != 0))
                candidateGroupIds.Add(groupId);

        foreach (var groupId in candidateGroupIds)
        {
            var dimension = floorInfo.DimensionList.FirstOrDefault(info => info.GroupIDList.Contains(groupId));
            if (dimension != null)
                return dimension.ID;
        }

        return floorInfo.DimensionList.FirstOrDefault(info => info.ID != 0)?.ID
               ?? floorInfo.DimensionList.FirstOrDefault()?.ID
               ?? 0;
    }

    // 客户端不会自己套用资源里的楼层存档默认值, 缺省即 0, 关卡图会走错分支
    private IEnumerable<(string Name, int DefaultValue)> ResolveFloorSavedValueDefaults()
    {
        var dimension = FloorInfo?.DimensionList.Find(x => x.ID == ResolveDimensionId());
        if (dimension != null)
            return dimension.SavedValues.Select(x => (x.Name, x.DefaultValue));

        return (FloorInfo?.FloorSavedValue ?? []).Select(x => (x.Name, x.DefaultValue));
    }

    public Dictionary<string, int> BuildClientFloorSavedData()
    {
        var result = new Dictionary<string, int>();
        var persisted = Player.SceneData!.FloorSavedData.GetValueOrDefault(FloorId, []);

        foreach (var (name, defaultValue) in ResolveFloorSavedValueDefaults())
            if (defaultValue != 0)
                result[name] = defaultValue;

        // Preserve the floor-wide activity-entry compatibility defaults. Platform-only stage
        // captures are excluded at prop creation, not by disabling ordinary floor content.
        var exposeActivityEntries = Excel.PlaneType is PlaneTypeEnum.Town or PlaneTypeEnum.Maze &&
                                    GameData.ContentPackageConfigData.Values.Any(package =>
                                        package.MainMissionIDList.Count > 0 && package.TouchesFloor(FloorId));
        // With missions enabled the main-mission scene/world state belongs to the story's SetFloorSavedValue
        // actions: official 20001001 ships SceneStateByMainMission 0, and forcing 1 made the client (and the
        // server group gate) create the floor's mission-gated groups early.
        var missionEnabled = Player.MissionEnabled;
        if (exposeActivityEntries)
            foreach (var value in FloorInfo?.FloorSavedValue ?? [])
                if (value.DefaultValue == 0 && !value.Name.Contains("_IsHidden") && !persisted.ContainsKey(value.Name)
                    && !(missionEnabled && IsMainMissionStateValue(value.Name)))
                    result[value.Name] = 1;

        foreach (var (key, value) in persisted)
            result[key] = value;

        return result;
    }

    private static bool IsMainMissionStateValue(string name)
    {
        return name is "SceneStateByMainMission" or "WorldStateByMainMission"
            || name.StartsWith("FSV_FinishMainLine", StringComparison.Ordinal)
            || name.StartsWith("FSV_LockMainLine", StringComparison.Ordinal)
            || name.StartsWith("FSV_Final", StringComparison.Ordinal)
            || name is "FSV_IsInFiveDim" or "FSV_InBuildRobot5D" or "FSV_InCity_AsRobot";
    }

    // Initial snapshots and LOADED notifications must describe the same complete group.
    public SceneEntityGroupInfo BuildEntityGroupInfo(int groupId)
    {
        var group = new SceneEntityGroupInfo { GroupId = (uint)groupId, State = 1 };
        var properties = FloorInfo?.Groups.GetValueOrDefault(groupId)?.GroupPropertyMap;
        var saved = Player.SceneData!.GroupPropertyData.GetValueOrDefault(FloorId, [])
            .GetValueOrDefault(groupId, []);
        foreach (var property in properties?.Values.Where(value => value.Side != GroupPropertySideEnum.ClientOnly) ?? [])
            group.BANBECDCDHG[property.Name] = saved.GetValueOrDefault(property.Name, property.DefaultValue);
        group.EntityList.AddRange(Entities.Values.Where(entity => entity.GroupId == groupId)
            .Select(entity => entity.ToProto()));
        return group;
    }

    public SceneInfo ToProto()
    {
        SceneInfo sceneInfo = new()
        {
            WorldId = (uint)ResolveWorldId(),
            GameModeType = (uint)GameModeType,
            PlaneId = (uint)PlaneId,
            FloorId = (uint)FloorId,
            EntryId = (uint)EntryId,
            ClientPosVersion = (uint)ClientPosVersion,
            SceneMissionInfo = new MissionStatusBySceneInfo(),
            FiveDimGameData = new SceneFiveDimGameData
            {
                MaxJumpEnergy = Player.GetComponent<FiveDimComponent>().MaxJumpEnergy
            },
            DimensionId = (uint)ResolveDimensionId(),
            GameStoryLineId = (uint)(Player.StoryLineManager?.StoryLineData.CurStoryLineId ?? 0),
            // 4.2.0 client expects SceneInfo.SceneIdentifier to be present during scene sync.
            SceneIdentifier = new SceneIdentifier
            {
                FloorId = (uint)FloorId,
                GameStoryLineId = (uint)(Player.StoryLineManager?.StoryLineData.CurStoryLineId ?? 0)
            }
        };

        if (Player.TrainCakeCatchManager?.GetSceneTeleportRoomOwnerUid() is { } roomOwnerUid)
            sceneInfo.SceneIdentifier.TeleportInfo = new TeleportInfo
            {
                TeleportId = roomOwnerUid
            };

        var playerGroupInfo = new SceneEntityGroupInfo(); // avatar group
        foreach (var avatar in AvatarInfo)
            playerGroupInfo.EntityList.Add(avatar.Value.ToProto());
        if (playerGroupInfo.EntityList.Count > 0)
        {
            if (LeaderEntityId == 0) LeaderEntityId = AvatarInfo.Values.First().EntityId;

            sceneInfo.LeaderEntityId = (uint)LeaderEntityId;
        }

        // 4.3 client refuses SummonUnit entities inside Challenge-type scenes
        // (ChallengePeak / Challenge endgame) — it stops processing
        // EnterSceneByServerScNotify and the player gets stuck at the loading
        // step. ExtraMpCount (e.g. Phainon's +3 max sp) is a separate ability
        // action on LineupData, so skipping dispatch here does not affect it.
        if (Excel.PlaneType != PlaneTypeEnum.Challenge)
            foreach (var summonUnit in SummonUnit.Values) playerGroupInfo.EntityList.Add(summonUnit.ToProto());

        sceneInfo.EntityGroupList.Add(playerGroupInfo);

        foreach (var groupId in Entities.Values.Select(entity => entity.GroupId).Concat(Groups)
                     .Where(groupId => groupId != 0).Distinct())
            sceneInfo.EntityGroupList.Add(BuildEntityGroupInfo(groupId));

        // custom save data and floor saved data
        Player.SceneData!.CustomSaveData.TryGetValue(EntryId, out var data);

        if (data != null)
            foreach (var customData in data)
                sceneInfo.CustomDataList.Add(new CustomSaveData
                {
                    GroupId = (uint)customData.Key,
                    SaveData = customData.Value
                });

        foreach (var (key, value) in BuildClientFloorSavedData())
            sceneInfo.FloorSavedData[key] = value;

        // mission
        Player.MissionManager!.OnLoadScene(sceneInfo);

        // unlock section
        if (!ConfigManager.Config.ServerOption.AutoLightSection)
        {
            if (IsTransientPlane)
            {
                // A rogue/raid scene publishes only what this instance revealed, never a previous run's fog.
                sceneInfo.LightenSectionList.AddRange(RevealedSections.Order().Select(x => (uint)x));
            }
            else
            {
                Player.SceneData!.UnlockSectionIdList.TryGetValue(FloorId, out var unlockSectionList);
                if (unlockSectionList != null)
                    foreach (var sectionId in unlockSectionList)
                        sceneInfo.LightenSectionList.Add((uint)sectionId);
            }
        }
        else
        {
            GameData.GetFloorInfo(PlaneId, FloorId, out var floorInfo);
            sceneInfo.LightenSectionList.AddRange(floorInfo.MapSections.Select(x => (uint)x));
        }

        // A Divergent Universe room publishes the run's drafted blessings plus the avatar-global maze buffs.
        if (GameModeType == GameModeTypeEnum.TournRogue && Player.RogueTournManager?.RunInstance is { } tournRun)
            sceneInfo.SceneBuffInfoList.AddRange(RogueTourn.RogueTournProtoBuilder.BuildSceneBuffs(tournRun, Player));

        return sceneInfo;
    }

    #endregion
}
