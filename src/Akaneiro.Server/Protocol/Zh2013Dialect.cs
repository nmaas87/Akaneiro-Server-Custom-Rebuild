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
    /// untouched. Client-side layouts were taken from the shipped Assembly-CSharp.dll (ILSpy).
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
