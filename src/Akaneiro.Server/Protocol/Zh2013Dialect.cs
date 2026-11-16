using System;
using System.Collections.Generic;
using System.Linq;

namespace Akaneiro.Server.Protocol
{
    /// <summary>
    /// The 2013 Chinese client build ("Akaneiro Demon Hunters (ZH)") speaks an earlier cut of the
    /// same protocol: all 200 of its method ids are byte-identical to this server's, but 60 newer
    /// methods (mail, well, redeem gifts, cooldown speedups, char slots, rename/gender, ...) never
    /// existed on it, and a handful of shared structs have a shorter or older field list.
    ///
    /// This adapter runs at the single send point (Session.Send) for such connections: messages the
    /// client has no parser for are dropped, and the eleven structs are rewritten by re-reading the
    /// freshly built 2014 packet and re-emitting the 2013 field list. Everything else goes through
    /// untouched. Client-side layouts were taken from the shipped Assembly-CSharp.dll (ILSpy) and
    /// re-verified against an untouched archive.org copy of the build (2026-11): protocol classes,
    /// struct field lists and the EServerErrorType table are byte-for-byte what this adapter assumes.
    /// The build is from August 2014 but never picked up the 2013/2014 protocol additions - it is
    /// strictly the January 2013 cut, NOT the April 2013 English dialect (no well, redeem gifts or
    /// ping methods; its error codes are renumbered a second time, see ErrorByEra).
    /// </summary>
    public static class Zh2013Dialect
    {
        /// <summary>Every method id the 2013 client can parse (names from its own protocol class).</summary>
        private static readonly HashSet<string> Known = new HashSet<string>
        {
            "AcceptMission", "AcceptMissionFail", "AcceptMissionOk", "ActiveTriggerReq", "AddExpNTF",
            "AllyNpcEnter", "AllyRegenerate", "AllyUpdateMoveRequest", "BeginUseSkill", "BreakSing",
            "BreakableActorEnter", "BroadCastCraftingItemOK", "BulletHit", "BuyConsumeItem", "BuyConsumeItemSuccAck",
            "BuyItem", "BuyItemFailed", "BuyItemSuccAck", "BuyPet", "BuyPetAck",
            "ChangeCharacterAttribNTF", "CharacterPrivateInfoUpdate", "CharacterStartInfo", "CharacterUpdate", "ChatFailed",
            "ChatRequest", "ChatRequestAck", "ChatResponse", "CraftingItem", "CraftingItemFailed",
            "CraftingItemOK", "CreateAllyNpcRequest", "DelItem", "DelItemFailed", "EnemyRewardReq",
            "EnterScene", "EnterSceneOk", "EquipErrorFailed", "EquipItem", "FriendAllyEnter",
            "GetDayReward", "GetDayRewardResult", "GetFriendList", "GetFriendListResult", "GetGiftAck",
            "GetGiftReq", "GetLeaderboardInfo", "ItemEnter", "LeaveWorld", "LevelUp",
            "MapLoadOK", "MissionAreaInfoAck", "MissionCompleteNTF", "MissionFailAck", "MissionFailReq",
            "MissionFailedNTF", "MissionListAck", "MissionListReq", "MissionProcessReq", "MoveUnit",
            "NotifyItemDestroy", "NotifyItemSwap", "NotifyItemUpdate", "ObjectEnterTest", "ObjectLeave",
            "PetRentTimeEndNTF", "PickupItemFailed", "PickupItemReq", "PickupMoneyReq", "PickupMoneyResult",
            "PlayerChangeWeapon", "PlayerChangeWeaponOk", "PlayerFireReq", "PlayerRegenerate",
            "PlayerReviveNumNTF", "PlayerUseCoolWeaponReq", "ProcessFriendReward", "ReloadMap", "RequestBagInfo",
            "RequestDayReward", "RequestDayRewardFailed", "RequestDayRewardOK", "RequestPlayerConsumeShopInfo", "RequestPlayerShopInfo",
            "ReviveReq", "ReviveResult", "RoleFreePetListNTF", "RoleMasteryListAck", "RoleMasteryListReq",
            "RolePetListAck", "RolePetListReq", "RushHit", "SaleItem", "SaleItemFailed",
            "SelectFriendAlly", "SelectFriendAllyResult", "SendBagInfo", "SendFriendRewardInfo", "SendMissionProcess",
            "SendShopConsumeIteminfo", "SendShopIteminfo", "SendShopRareitemTrackInfo", "SendSkillInfo", "SerialKillOver",
            "SetKnockBackSpeed", "SetMoveType", "SetMoveTypeFail", "SetMoveTypeResult", "SetObjectPosition",
            "SetObjectVecPosition", "SetSkillShortcut", "SkillObjectEnter", "SpawnnerKilled", "SpeechResult",
            "SplitItem", "SplitItemFailed", "StopBeginUseSkill", "StudyAbility", "StudyAbilityFailed",
            "StudyAbilitySucceed", "SwapItem", "SwapItemFailed", "SyncCurTime", "TelePortMoveResult",
            "TrapHit", "TriggerAll", "TriggerAnimationResult", "TutorialEnterAck", "TutorialEnterReq",
            "UnEquipErrorFailed", "UnequipItem", "UpdateAnimation", "UpdateAnimationNotify", "UpdateBadge",
            "UpdateBaseAttr", "UpdateCacheMoneyInfo", "UpdateCharStatData", "UpdateElementAttr", "UpdateJoule",
            "UpdateLeaderboardInfo", "UpdateMoneyInfo", "UpdateMoveFail", "UpdateMoveRequest", "UpdateMoveResult",
            "UpdateSerialKillNum", "UpdateStatusEffect", "UpdateTutorialInfo", "UseAllyNormalAttack", "UseAllySkill",
            "UseFightSkill", "UseFightSkillFailed", "UseFightSkillResult", "UseNormalAttack", "UseNormalAttackFailed",
            "UseNormalAttackResult", "UserCharacterInfo", "UserCreateAccountCharacter", "UserCreateCharacter", "UserCreateCharacterFailed",
            "UserCreateCharacterOK", "UserDelCharacter", "UserDelCharacterResult", "UserLogin", "UserLoginFailed",
            "UserLoginOK", "UserLogout", "UserReconnect", "UserReconnectNotify", "UserSelectCharacter",
            "UserSelectCharacterFailed", "UserSelectCharacterOK", "YouCheat", "assignTalentAck", "assignTalentReq",
            "branchCompleteNTF", "buyAllAreaByKarmaAck", "buyAllAreaByKarmaReq", "buyThreatLevelByKarma", "buyThreatLevelByKarmaAck",
            "choosePet", "choosePetAck", "conversationNpcCheckAck", "conversationNpcCheckReq", "exploretaskcompleteAck",
            "exploretaskcompleteReq", "hasAssignedTalentPointAck", "hasAssignedTalentPointReq", "masteryLvlupAck", "masteryLvlupReq",
            "moneyEnter", "monsterEnter", "npcEnter", "protecttaskBeginReq", "protecttaskEndReq",
            "roleAttribReq", "taskCompleteNTF", "unLockAreaAck", "unLockAreaReq", "useItem",
        };

        // method ids of the response names the 2013 client cannot parse; resolved lazily from the
        // generated name table so the list above stays human-readable (and small).
        private static HashSet<int> _unknownIds;

        public static bool Supports(Packet packet)
        {
            int id = MethodId(packet);
            // one battle method is newer than this build (skill-object activation): the client
            // would parse-fail it and log "OnReceive parse failed!" for every skill object fired.
            if (id == ProtocolBattle_METHOD.uMethodID_SkillObjectActive)
                return false;
            return !UnknownIds().Contains(id);
        }

        /// <summary>The packet unchanged (2013 can parse it as-is), a rebuilt 2013-layout packet, or null to drop.</summary>
        public static Packet Translate(Packet packet)
        {
            int id = MethodId(packet);
            byte[] cs = packet.GetByteStream();
            return id switch
            {
                ProtocolGame_METHOD.uMethodID_UserLoginOK => UserLoginOK(cs),
                ProtocolGame_METHOD.uMethodID_UserCreateCharacterOK => UserCreateCharacterOK(cs),
                ProtocolGame_METHOD.uMethodID_UserSelectCharacterOK => UserSelectCharacterOK(cs),
                ProtocolGame_METHOD.uMethodID_UserCharacterInfo => UserCharacterInfo(cs),
                ProtocolGame_METHOD.uMethodID_SendBagInfo => SendBagInfo(cs),
                ProtocolGame_METHOD.uMethodID_SendShopIteminfo => SendShopIteminfo(cs),
                ProtocolGame_METHOD.uMethodID_SendShopRareitemTrackInfo => SendShopRareitemTrackInfo(cs),
                ProtocolGame_METHOD.uMethodID_BuyItemSuccAck => BuyItemSuccAck(cs),
                ProtocolGame_METHOD.uMethodID_RequestDayRewardOK => RequestDayRewardOK(cs),
                ProtocolGame_METHOD.uMethodID_SendMissionProcess => SendMissionProcess(cs),
                ProtocolGame_METHOD.uMethodID_SendFriendRewardInfo => SendFriendRewardInfo(cs),
                _ => packet,
            };
        }

        private static int MethodId(Packet packet) => BitConverter.ToInt32(packet.GetByteStream(), 6);

        private static HashSet<int> UnknownIds() => _unknownIds ??= new HashSet<int>(
            GameResponses.Names.Keys.Where(id => !Known.Contains(GameResponses.Names[id])));

        /// <summary>
        /// The 2013 build's EServerErrorType numbering predates the karma/FK money split and the
        /// revive-item / ally-slot / friend-account error families: from 51 on, its codes sit one
        /// to eight numbers below the 2014 ones (its On_ReviveResult literally checks for 51 =
        /// "not enough karma"). Table built by matching the enum names in the shipped
        /// Assembly-CSharp.dll against the 2014 table; codes this build never had (mail, well,
        /// mastery cooldown, ...) cannot occur on a 2013 session and pass through unchanged.
        /// </summary>
        private static readonly Dictionary<int, int> ErrorByEra = new()
        {
            [(int)EServerErrorType.eGS_LessMoney_fk] = 51, // 2013 had one money error (eGS_LessMoney)
            [(int)EServerErrorType.eGS_LessLevel] = 52,  // 2014 54
            [(int)EServerErrorType.eGS_SkillNotExist] = 53,  // 2014 56
            [(int)EServerErrorType.eGS_PlayerDead] = 54,  // 2014 57
            [(int)EServerErrorType.eCreateCharacter_InvalidPlayer] = 55,  // 2014 59
            [(int)EServerErrorType.eCreateCharacter_AlreadyNickName] = 56,  // 2014 60
            [(int)EServerErrorType.eCreateCharacter_ParamError] = 57,  // 2014 61
            [(int)EServerErrorType.eCreateCharacter_CharacterFull] = 58,  // 2014 62
            [(int)EServerErrorType.eCreateCharacter_InGame] = 59,  // 2014 63
            [(int)EServerErrorType.eSelectCharacter_InvalidPlayer] = 60,  // 2014 64
            [(int)EServerErrorType.eSelectCharacter_NoCharacter] = 61,  // 2014 65
            [(int)EServerErrorType.eSelectCharacter_InGame] = 62,  // 2014 66
            [(int)EServerErrorType.eDelCharacter_InvalidPlayer] = 63,  // 2014 67
            [(int)EServerErrorType.eDelCharacter_NoCharacter] = 64,  // 2014 68
            [(int)EServerErrorType.eDelCharacter_StateErorr] = 65,  // 2014 69
            [(int)EServerErrorType.eFriendError_NoExist] = 66,  // 2014 70
            [(int)EServerErrorType.eFriendError_AddSelf] = 67,  // 2014 71
            [(int)EServerErrorType.eFriendError_Offline] = 68,  // 2014 72
            [(int)EServerErrorType.eFriendError_NotCanAddFriend] = 69,  // 2014 73
            [(int)EServerErrorType.eFriendError_AlreadyFriend] = 70,  // 2014 74
            [(int)EServerErrorType.eFriendError_FullFriend] = 71,  // 2014 75
            [(int)EServerErrorType.eFriendError_InBlackList] = 72,  // 2014 76
            [(int)EServerErrorType.eFriendError_AlreadyBlack] = 73,  // 2014 77
            [(int)EServerErrorType.eFriendError_InFriendList] = 74,  // 2014 78
            [(int)EServerErrorType.eFriendError_FullBlack] = 75,  // 2014 79
            [(int)EServerErrorType.eAccountFriendError_NoExist] = 76,  // 2014 81
            [(int)EServerErrorType.eAccountFriendError_AddSelf] = 77,  // 2014 82
            [(int)EServerErrorType.eAccountFriendError_Offline] = 78,  // 2014 83
            [(int)EServerErrorType.eAccountFriendError_RefuseAddFriend] = 79,  // 2014 84
            [(int)EServerErrorType.eAccountFriendError_AlreadyFriend] = 80,  // 2014 85
            [(int)EServerErrorType.eAccountFriendError_FullFriend] = 81,  // 2014 86
            [(int)EServerErrorType.eItemError_NoExist] = 82,  // 2014 87
            [(int)EServerErrorType.eItemError_BuyCantFindItem] = 83,  // 2014 88
            [(int)EServerErrorType.eItemError_BuyCountBeyondLimit] = 84,  // 2014 89
            [(int)EServerErrorType.eItemError_BuyNotEnoughMoneny] = 85,  // 2014 90
            [(int)EServerErrorType.eItemError_CantSellItem] = 86,  // 2014 91
            [(int)EServerErrorType.eItemError_rareItemSellOut] = 87,  // 2014 92
            [(int)EServerErrorType.eItemError_CantCarryMoreofThis] = 88,  // 2014 93
            [(int)EServerErrorType.eItemError_BagFull] = 89,  // 2014 94
            [(int)EServerErrorType.eItemError_Mismatching] = 90,  // 2014 95
            [(int)EServerErrorType.eItemError_CantSplitItem] = 91,  // 2014 96
            [(int)EServerErrorType.eItemError_SplitMoreThanCount] = 92,  // 2014 97
            [(int)EServerErrorType.eItemError_CantBeSwapped] = 93,  // 2014 98
            [(int)EServerErrorType.eItemError_CantBeEquiped] = 94,  // 2014 99
            [(int)EServerErrorType.eItemError_EquipmentSlotAvailable] = 95,  // 2014 100
            [(int)EServerErrorType.eItemError_CampLimit] = 96,  // 2014 101
            [(int)EServerErrorType.eItemError_InBattle] = 97,  // 2014 102
            [(int)EServerErrorType.eItemError_AlreadyAdded] = 98,  // 2014 103
            [(int)EServerErrorType.eItemError_AlreadyEquiped] = 99,  // 2014 104
            [(int)EServerErrorType.eItemError_CantBeUnEquiped] = 100,  // 2014 105
            [(int)EServerErrorType.eItemError_SellOut] = 101,  // 2014 106
            [(int)EServerErrorType.eItemError_TimeOut] = 102,  // 2014 107
            [(int)EServerErrorType.eItemError_PlayerLvlNotEnough] = 103,  // 2014 108
            [(int)EServerErrorType.eDayRewardError_AlreadyGet] = 104,  // 2014 109
            [(int)EServerErrorType.eMissionError_MissionHaveAccept] = 105,  // 2014 110
            [(int)EServerErrorType.eMission_Dont_Have_ReqLevel] = 106,  // 2014 111
            [(int)EServerErrorType.eMission_OverTime] = 107,  // 2014 112
            [(int)EServerErrorType.eMission_fail_exitScene] = 108,  // 2014 113
            [(int)EServerErrorType.eMission_fail_protect] = 109,  // 2014 114
            [(int)EServerErrorType.eMission_Not_exist] = 110,  // 2014 115
            [(int)EServerErrorType.eMission_PredecessorsNotCompleteOrNoteExist] = 111,  // 2014 116
            [(int)EServerErrorType.eMission_not_reset] = 112,  // 2014 117
            [(int)EServerErrorType.eMission_not_enough_badge] = 113,  // 2014 118
            [(int)EServerErrorType.eMission_have_buy_area] = 114,  // 2014 119
            [(int)EServerErrorType.ePetError_Full] = 115,  // 2014 121
            [(int)EServerErrorType.ePetError_repeatPet] = 116,  // 2014 122
            [(int)EServerErrorType.eLeftTalentPoint_lack] = 117,  // 2014 123
            [(int)EServerErrorType.eItemCraftError_NoExist] = 118,  // 2014 124
            [(int)EServerErrorType.eItemCraftError_MaxLevel] = 119,  // 2014 126
            [(int)EServerErrorType.eItemCraftError_AttribMaxLevel] = 120,  // 2014 127
            [(int)EServerErrorType.eItemCraftError_NotEnoughMoneny] = 121,  // 2014 128
            [(int)EServerErrorType.eItemCraftError_GemError] = 122,  // 2014 129
            [(int)EServerErrorType.eItemCraftError_CraftFailure] = 123,  // 2014 130
            [(int)EServerErrorType.eGift_NotExist] = 124,  // 2014 131
            [(int)EServerErrorType.eGift_HaveBeenUsed] = 125,  // 2014 132
            [(int)EServerErrorType.eMasteryError_fullLvl] = 126,  // 2014 133
            [(int)EServerErrorType.eGm_Account_has_block] = 127,  // 2014 135
        };

        public static int ToClientError(int code) => ErrorByEra.TryGetValue(code, out int era) ? era : code;

        // ---- 2013 struct writers (field lists exactly as the shipped client reads them) ---------

        private static Packet Begin(int methodId)
        {
            var os = new Packet();
            os.WriteInt(0);
            os.WriteShort(0);
            os.WriteInt(methodId);
            return os;
        }

        private static Packet Finish(Packet os)
        {
            os.Patch(os.get_used_size() - 6, 0);
            return os;
        }

        private static Packet Body(byte[] csBytes)
            => new Packet(csBytes[10..]); // read mode, past length, tag and method id

        private static void AccountInfo(Packet os, SAccountInfo v)
        {
            os.WriteString(v.account);
            os.WriteString(v.password);
            os.WriteString(v.nickName);
            os.WriteInt(v.id);
            os.WriteInt(v.gender);
            os.WriteInt(v.type);
            EUserType.Write(os, v.userType);
            os.WriteInt(v.points);
            os.WriteString(v.createTime);
            os.WriteInt(v.status);
            os.WriteNatureTime(v.statusChangeTime);
            os.WriteNatureTime(v.totalOnlineTime);
            os.WriteNatureTime(v.todayOnlineTime);
            os.WriteString(v.lastLoginIP);
            os.WriteNatureTime(v.lastLoginTime);
            os.WriteString(v.currentIP);
            os.WriteInt(v.lastCharID);
            os.WriteInt(v.friendVersion);
            os.WriteInt(v.FK);
            os.WriteInt(v.SK);
            os.WriteInt(v.level);
            // (no curCharSlotNum, reviveNum, eventData, wellData, isGetRevivalCard in 2013)
        }

        private static void CharacterInfoLogin(Packet os, SCharacterInfoLogin v)
        {
            os.WriteInt(v.ID);
            os.WriteString(v.nickname);
            os.WriteInt(v.badgeNum);
            ESex.Write(os, v.sex);
            os.WriteInt(v.style);
            os.WriteInt(v.level);
            os.WriteUint64(v.Exp);
            os.WriteNatureTime(v.creationTime);
            os.WriteInt(v.status);
            vectorSItemuuid.Write(os, v.equipinfo);
            os.WriteBool(v.isTutorial);
            // (no attrVec in 2013)
        }

        private static void CharacterInfoBasic(Packet os, SCharacterInfoBasic v)
        {
            os.WriteInt(v.ID);
            os.WriteString(v.nickname);
            os.WriteInt(v.style);
            os.WriteInt(v.level);
            os.WriteUint64(v.exp);
            ESex.Write(os, v.sex);
            os.WriteInt(v.Badges);
            os.WriteUint(v.BadgeTime);
            os.WriteInt(v.Armor);
            os.WriteInt(v.Harm);
            os.WriteInt(v.Critical);
            os.WriteNatureTime(v.creationTime);
            os.WriteBool(v.firstlogin);
            // (no eventData / cooldownData in 2013)
        }

        private static void ShopItemInfo(Packet os, SShopItemInfo v)
        {
            os.WriteInt(v.UUID);
            os.WriteInt(v.ID);
            os.WriteInt(v.perfrab);
            os.WriteUint(v.level);
            ItemGuid.Write(os, v.guid);
            os.WriteInt(v.enchant);
            os.WriteInt(v.element);
            os.WriteInt(v.gem);
            os.WriteInt(v.moneyType);
            os.WriteInt(v.price);
            // (no isVisible in 2013 - every shelf entry is simply visible)
            os.WriteInt(v.isRare);
            os.WriteInt(v.leftBuyCount);
            os.WriteInt(v.leftBuyTime);
            os.WriteInt(v.isDiscount);
        }

        private static void BuyItemInfo(Packet os, SBuyitemInfo v)
        {
            // (no leading UUID in 2013)
            os.WriteInt(v.ID);
            os.WriteInt(v.perfrab);
            os.WriteUint(v.level);
            os.WriteInt(v.enchant);
            os.WriteInt(v.element);
            os.WriteInt(v.gem);
            os.WriteUint(v.count);
            os.WriteInt8(v.currencyType);
        }

        private static void FriendHireReward(Packet os, SFriendHireReward v)
        {
            os.WriteInt(v.charID);
            os.WriteString(v.charName);
            os.WriteInt(v.exp);
            os.WriteInt(v.sk);
            // (no style / sex in 2013)
        }

        // ---- rebuilt responses -------------------------------------------------------------------

        private static Packet UserLoginOK(byte[] cs)
        {
            Packet ins = Body(cs);
            bool reconnected = ins.ReadBool();
            SAccountInfo account = Proto.New<SAccountInfo>(); SAccountInfo.Read(ins, out account);
            vectorCharacterInfoLogins characters = new vectorCharacterInfoLogins();
            vectorCharacterInfoLogins.Read(ins, out characters);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_UserLoginOK);
            os.WriteBool(reconnected);
            AccountInfo(os, account);
            os.WriteInt(characters.Count);
            foreach (SCharacterInfoLogin c in characters)
                CharacterInfoLogin(os, c);
            return Finish(os);
        }

        private static Packet UserCreateCharacterOK(byte[] cs)
        {
            Packet ins = Body(cs);
            SCharacterInfoLogin character = new SCharacterInfoLogin();
            SCharacterInfoLogin.Read(ins, out character);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_UserCreateCharacterOK);
            CharacterInfoLogin(os, character);
            return Finish(os);
        }

        private static Packet UserSelectCharacterOK(byte[] cs)
        {
            Packet ins = Body(cs);
            SCharacterInfoBasic basic = new SCharacterInfoBasic(); SCharacterInfoBasic.Read(ins, out basic);
            vectorAttrChange attrs = new vectorAttrChange(); vectorAttrChange.Read(ins, out attrs);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_UserSelectCharacterOK);
            CharacterInfoBasic(os, basic);
            vectorAttrChange.Write(os, attrs);
            return Finish(os);
        }

        private static Packet UserCharacterInfo(byte[] cs)
        {
            Packet ins = Body(cs);
            SCharacterInfoBasic basic = new SCharacterInfoBasic(); SCharacterInfoBasic.Read(ins, out basic);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_UserCharacterInfo);
            CharacterInfoBasic(os, basic);
            return Finish(os);
        }

        private static Packet SendBagInfo(byte[] cs)
        {
            Packet ins = Body(cs);
            SBagGroup bags = new SBagGroup(); SBagGroup.Read(ins, out bags);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendBagInfo);
            SBagInfo.Write(os, bags.equipbag);
            SBagInfo.Write(os, bags.basebag);
            SBagInfo.Write(os, bags.bankbag);
            // (no stashbag in 2013 - the client has no stash window)
            return Finish(os);
        }

        private static Packet SendShopIteminfo(byte[] cs)
        {
            Packet ins = Body(cs);
            bool updateIn12 = ins.ReadBool();
            vectorShopItemInfos items = new vectorShopItemInfos(); vectorShopItemInfos.Read(ins, out items);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendShopIteminfo);
            os.WriteBool(updateIn12);
            os.WriteInt(items.Count);
            foreach (SShopItemInfo item in items)
                ShopItemInfo(os, item);
            return Finish(os);
        }

        private static Packet SendShopRareitemTrackInfo(byte[] cs)
        {
            Packet ins = Body(cs);
            bool updateIn12 = ins.ReadBool();
            vectorShopItemInfos items = new vectorShopItemInfos(); vectorShopItemInfos.Read(ins, out items);
            int wDay = ins.ReadInt();

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendShopRareitemTrackInfo);
            os.WriteBool(updateIn12);
            os.WriteInt(items.Count);
            foreach (SShopItemInfo item in items)
                ShopItemInfo(os, item);
            os.WriteInt(wDay);
            return Finish(os);
        }

        private static Packet BuyItemSuccAck(byte[] cs)
        {
            Packet ins = Body(cs);
            int isRare = ins.ReadInt();
            SBuyitemInfo info = new SBuyitemInfo(); SBuyitemInfo.Read(ins, out info);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_BuyItemSuccAck);
            os.WriteInt(isRare);
            BuyItemInfo(os, info);
            return Finish(os);
        }

        private static Packet RequestDayRewardOK(byte[] cs)
        {
            Packet ins = Body(cs);
            int dayID = ins.ReadInt();
            long daytime = ins.ReadLong();
            EServerErrorType result = Proto.Error(EServerErrorType.eUnknownError);
            EServerErrorType.Read(ins, out result);

            // The 2013 answer carries a plain flag plus the day's reward items; those items reach a
            // 2013 client through the shared NotifyItemAdd/GetDayRewardResult path anyway, so the
            // list itself goes out empty. The 2014 dayRewardStatusVec is not part of the old wire.
            Packet os = Begin(ProtocolGame_METHOD.uMethodID_RequestDayRewardOK);
            os.WriteInt(dayID);
            os.WriteLong(daytime);
            os.WriteBool(result.Get() == EServerErrorType.eSuccess);
            os.WriteInt(0); // vectorItemInfos: empty
            return Finish(os);
        }

        private static Packet SendMissionProcess(byte[] cs)
        {
            Packet ins = Body(cs);
            uint missionID = ins.ReadUint();
            vectorSBranch branches = new vectorSBranch(); vectorSBranch.Read(ins, out branches);
            ins.ReadInt(); // crystalReliveNum (2014 only)
            int karmaReliveNum = ins.ReadInt();
            // (2014's exp, karma and materialMap are simply not read back)

            // The 2013 client knows one relive counter; the karma one is what its shop spent.
            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendMissionProcess);
            os.WriteUint(missionID);
            vectorSBranch.Write(os, branches);
            os.WriteInt(karmaReliveNum);
            return Finish(os);
        }

        private static Packet SendFriendRewardInfo(byte[] cs)
        {
            Packet ins = Body(cs);
            mapFriendHireReward rewards = new mapFriendHireReward(); mapFriendHireReward.Read(ins, out rewards);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendFriendRewardInfo);
            os.WriteInt(rewards.Count);
            foreach (KeyValuePair<int, SFriendHireReward> pair in rewards)
            {
                os.WriteInt(pair.Key);
                FriendHireReward(os, pair.Value);
            }
            return Finish(os);
        }
    }
}
