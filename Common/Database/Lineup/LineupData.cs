using MemoryPack;
using System.ComponentModel;
using March7thHoney.Database.Avatar;
using March7thHoney.Database.Quests;
using March7thHoney.Proto;
using Newtonsoft.Json;

namespace March7thHoney.Database.Lineup;

[DbTable("Lineup")]
public class LineupData : BaseDatabaseDataHelper
{
    public int CurLineup { get; set; } // index of current lineup
    public int CurExtraLineup { get; set; } = -1; // index of current extra lineup
    [DbIgnore] public int ExtraMpCount { get; set; }
    public Dictionary<int, LineupInfo> Lineups { get; set; } = []; // 9 * 4

    public int GetCurLineupIndex()
    {
        return CurExtraLineup == -1 ? CurLineup : CurExtraLineup;
    }
}

[MemoryPackable]
public partial class LineupInfo
{
    public string? Name { get; set; }
    public int LineupType { get; set; }
    public int LeaderAvatarId { get; set; }
    public List<LineupAvatarInfo>? BaseAvatars { get; set; }
    [DefaultValue(5)] public int Mp { get; set; } = 5;

    // MarkPresetLineupCsReq 标记的常用队伍。追加在已有成员之后，旧存档反序列化为 false。
    public bool IsFavourite { get; set; }

    // Story/raid trials advertise a virtual lineup. Appended so older lineup blobs keep the defaults.
    public bool IsVirtual { get; set; }
    public int PlaneId { get; set; }

    [JsonIgnore] [MemoryPackIgnore] public LineupData? LineupData { get; set; }

    [JsonIgnore] [MemoryPackIgnore] public AvatarData? AvatarData { get; set; }

    public int GetSlot(int avatarId)
    {
        return BaseAvatars?.FindIndex(item => item.BaseAvatarId == avatarId) ?? -1;
    }

    public bool Heal(int count, bool allowRevive)
    {
        var result = false;
        if (BaseAvatars != null && AvatarData != null)
            foreach (var avatar in BaseAvatars)
            {
                BaseAvatarInfo? avatarInfo;
                if (avatar.SpecialAvatarId > 0)
                    avatarInfo = AvatarData?.TrialAvatars?.Find(item => item.SpecialAvatarId == avatar.SpecialAvatarId);
                else
                    avatarInfo = AvatarData?.FormalAvatars?.Find(item => item.BaseAvatarId == avatar.BaseAvatarId);

                if (avatarInfo != null)
                {
                    if (avatarInfo.GetCurHp(IsExtraLineup()) <= 0 && !allowRevive) continue;
                    if (avatarInfo.GetCurHp(IsExtraLineup()) >= 10000 && count > 0) continue; // full hp
                    if (avatarInfo.GetCurHp(IsExtraLineup()) <= 0 && count < 0) continue; // dead
                    avatarInfo.SetCurHp(Math.Max(Math.Min(avatarInfo.GetCurHp(IsExtraLineup()) + count, 10000), 0),
                        IsExtraLineup());
                    result = true;
                }
            }

        return result;
    }

    public bool CostNowPercentHp(double count)
    {
        var result = false;
        if (BaseAvatars != null && AvatarData != null)
            foreach (var avatar in BaseAvatars)
            {
                BaseAvatarInfo? avatarInfo;
                if (avatar.SpecialAvatarId > 0)
                    avatarInfo = AvatarData?.TrialAvatars?.Find(item => item.SpecialAvatarId == avatar.SpecialAvatarId);
                else
                    avatarInfo = AvatarData?.FormalAvatars?.Find(item => item.BaseAvatarId == avatar.BaseAvatarId);

                if (avatarInfo != null)
                {
                    if (avatarInfo.CurrentHp <= 0) continue;
                    avatarInfo.SetCurHp((int)Math.Max(avatarInfo.GetCurHp(IsExtraLineup()) * (1 - count), 100),
                        IsExtraLineup());
                    result = true;
                }
            }

        return result;
    }

    public bool AddPercentSp(int count)
    {
        var result = false;
        if (BaseAvatars != null && AvatarData != null)
            foreach (var avatar in BaseAvatars)
            {
                BaseAvatarInfo? avatarInfo;
                if (avatar.SpecialAvatarId > 0)
                    avatarInfo = AvatarData?.TrialAvatars?.Find(item => item.SpecialAvatarId == avatar.SpecialAvatarId);
                else
                    avatarInfo = AvatarData?.FormalAvatars?.Find(item => item.BaseAvatarId == avatar.BaseAvatarId);

                if (avatarInfo != null)
                {
                    if (avatarInfo.CurrentHp <= 0) continue;
                    avatarInfo.SetCurSp(Math.Min(avatarInfo.GetCurSp(IsExtraLineup()) + count, 10000), IsExtraLineup());
                    result = true;
                }
            }

        return result;
    }

    /// <summary>
    ///     队伍在 <see cref="LineupData.Lineups" /> 里的键。旧实现取 Values 中的位置，补齐过空槽或销毁重建过额外队伍后，
    ///     字典枚举顺序与键不一致，客户端会把队伍放到错误的编号上。
    /// </summary>
    public int GetIndex()
    {
        if (LineupData?.Lineups == null) return 0;
        foreach (var (index, lineup) in LineupData.Lineups)
            if (ReferenceEquals(lineup, this))
                return index;
        return 0;
    }

    public bool IsExtraLineup()
    {
        return LineupType != 0;
    }

    public Proto.LineupInfo ToProto()
    {
        Mp = Math.Min(5 + (LineupData?.ExtraMpCount ?? 0), Mp);
        Proto.LineupInfo info = new()
        {
            Name = Name,
            MaxMp = (uint)(5 + (LineupData?.ExtraMpCount ?? 0)),
            Mp = (uint)Mp,
            ExtraLineupType = (ExtraLineupType)(LineupType == (int)ExtraLineupType.LineupHeliobus
                ? (int)ExtraLineupType.LineupNone
                : LineupType),
            Index = (uint)GetIndex(),
            IsFavourite = IsFavourite,
            IsVirtual = IsVirtual,
            PlaneId = (uint)PlaneId
        };

        if (LineupType != (int)ExtraLineupType.LineupNone) info.Index = 0;

        if (BaseAvatars?.Find(item => item.BaseAvatarId == LeaderAvatarId) != null) // find leader,if not exist,set to 0
            info.LeaderSlot = (uint)BaseAvatars.IndexOf(BaseAvatars.Find(item => item.BaseAvatarId == LeaderAvatarId)!);
        else
            info.LeaderSlot = 0;

        if (BaseAvatars != null)
            foreach (var avatar in BaseAvatars)
            {
                if (avatar == null) continue;

                if (avatar.AssistUid != 0) // assist avatar
                {
                    // A support always joins at full HP/energy and is identified by its path id.
                    var support = DatabaseHelper.Instance?.GetInstance<AvatarData>(avatar.AssistUid)
                        ?.FindByAnyId(avatar.BaseAvatarId);
                    if (support != null)
                        info.AvatarList.Add(new LineupAvatar
                        {
                            Id = (uint)support.AvatarId,
                            Slot = (uint)BaseAvatars.IndexOf(avatar),
                            AvatarType = AvatarType.AvatarAssistType,
                            Hp = 10000,
                            SpBar = new SpBarInfo { CurSp = 10000, MaxSp = 10000 }
                        });
                }
                else if (avatar.SpecialAvatarId != 0) // special avatar
                {
                    var lineupAvatar = AvatarData?.TrialAvatars
                        ?.Find(item => item.SpecialAvatarId == avatar.SpecialAvatarId)
                        ?.ToLineupInfo(BaseAvatars.IndexOf(avatar), this, AvatarType.AvatarTrialType);
                    if (lineupAvatar != null) info.AvatarList.Add(lineupAvatar);
                }
                else // normal avatar
                {
                    var lineupAvatar = AvatarData?.FormalAvatars
                        ?.Find(item => item.BaseAvatarId == avatar.BaseAvatarId)
                        ?.ToLineupInfo(BaseAvatars.IndexOf(avatar), this);
                    if (lineupAvatar != null) info.AvatarList.Add(lineupAvatar);
                }
            }

        var storyId = DatabaseHelper.Instance!.GetInstance<StoryLineData>(AvatarData!.Uid)?.CurStoryLineId;
        if (storyId != null && storyId != 0)
        {
            info.GameStoryLineId = (uint)storyId;
            BaseAvatars?.ForEach(item =>
            {
                if (item.SpecialAvatarId != 0) info.StoryLineAvatarIdList.Add((uint)item.SpecialAvatarId);
            });
        }

        return info;
    }
}

[MemoryPackable]
public partial class LineupAvatarInfo
{
    public int BaseAvatarId { get; set; }
    public int AssistUid { get; set; }
    public int SpecialAvatarId { get; set; }
}
