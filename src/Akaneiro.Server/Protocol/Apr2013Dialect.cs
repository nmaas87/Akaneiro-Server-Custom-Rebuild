using System;
using System.Collections.Generic;
using System.Linq;

namespace Akaneiro.Server.Protocol
{
    /// <summary>
    /// The April 2013 client build ("Win04162", the English download client) sits between the
    /// January 2013 Chinese build and the 2014 wire: it already knows the well, redeem gifts,
    /// ping, the shop visibility flag and the purchase UUID (those packets go through unchanged),
    /// but it has no parser for the 2014 mail system, char slots, rename/gender, cooldown
    /// speedups or the stash bag, and four shared structs still have the older field lists:
    ///
    ///   SAccountInfo        no curCharSlotNum / isGetRevivalCard (reviveNum + event + well stay)
    ///   SCharacterInfoLogin no attrVec
    ///   SCharacterInfoBasic no cooldownData (eventData stays)
    ///   SBagGroup           three bags, no stashbag
    ///   SMissionProcess     one relive counter
    ///   SAcceptMissionRelate  no coolDownTime / lastPassLvl per mission list entry
    ///   SRedeemGift         four strings, item id vector instead of full item infos
    ///
    /// Same pattern as Zh2013Dialect: unknown methods are dropped, the structs above are rebuilt
    /// by re-reading the fresh 2014 packet. Client-side layouts taken from the shipped
    /// Assembly-CSharp.dll of the April 16, 2013 build (ILSpy).
    /// </summary>
    public static class Apr2013Dialect
    {
        /// <summary>Every method id this client can parse (its own protocol name table).</summary>
        private static readonly HashSet<string> Known = new HashSet<string>
        {
            "AcceptMission", "AcceptMissionFail", "AcceptMissionOk", "AddExpNTF", "BroadCastCraftingItemOK",
            "BuyConsumeItem", "BuyConsumeItemSuccAck", "BuyItem", "BuyItemFailed", "BuyItemSuccAck",
            "BuyPet", "BuyPetAck", "ChangeCharacterAttribNTF", "ChatFailed", "ChatRequest",
            "ChatRequestAck", "ChatResponse", "CraftingItem", "CraftingItemFailed", "CraftingItemOK",
            "DelItem", "DelItemFailed", "EquipErrorFailed", "EquipItem", "GetDayReward",
            "GetDayRewardResult", "GetFriendReward", "GetGiftAck", "GetGiftReq", "GetLeaderboardInfo",
            "GetRedeemGift", "GetRedeemGiftResult", "GetWellData", "GetWellDataResult", "GetWellKarma",
            "GetWellKarmaResult", "LeaveWorld", "LevelUp", "MissionAreaInfoAck", "MissionCompleteNTF",
            "MissionFailAck", "MissionFailReq", "MissionFailedNTF", "MissionListAck", "MissionListReq",
            "MissionProcessReq", "NewRedeemGift", "NotifyItemDestroy", "NotifyItemSwap", "NotifyItemUpdate",
            "PetRentTimeEndNTF", "ProcessFriendReward", "ProcessRedeemGift", "ProcessRedeemGiftResult", "ReloadMap",
            "RequestBagInfo", "RequestDayReward", "RequestDayRewardFailed", "RequestDayRewardOK", "RequestPlayerConsumeShopInfo",
            "RequestPlayerShopInfo", "RoleFreePetListNTF", "RoleMasteryListAck", "RoleMasteryListReq", "RolePetListAck",
            "RolePetListReq", "SaleItem", "SaleItemFailed", "SelectDayRewardNTF", "SendBagInfo",
            "SendFriendRewardInfo", "SendMissionProcess", "SendShopConsumeIteminfo", "SendShopIteminfo", "SendShopRareitemTrackInfo",
            "SendSkillInfo", "SplitItem", "SplitItemFailed", "StudyAbility", "StudyAbilityFailed",
            "StudyAbilitySucceed", "SwapItem", "SwapItemFailed", "TutorialEnterAck", "TutorialEnterReq",
            "UnEquipErrorFailed", "UnequipItem", "UpdateBadge", "UpdateBaseAttr", "UpdateCacheMoneyInfo",
            "UpdateCharStatData", "UpdateElementAttr", "UpdateEvent", "UpdateJoule", "UpdateLeaderboardInfo",
            "UpdateMoneyInfo", "UpdateReviveNum", "UpdateTutorialInfo", "UpgradeWell", "UpgradeWellResult",
            "UserCharacterInfo", "UserCreateAccountCharacter", "UserCreateCharacter", "UserCreateCharacterFailed", "UserCreateCharacterOK",
            "UserDelCharacter", "UserDelCharacterResult", "UserLogin", "UserLoginFailed", "UserLoginOK",
            "UserLogout", "UserReconnect", "UserReconnectNotify", "UserSelectCharacter", "UserSelectCharacterFailed",
            "UserSelectCharacterOK", "YouCheat", "assignTalentAck", "assignTalentReq", "branchCompleteNTF",
            "buyAllAreaByKarmaAck", "buyAllAreaByKarmaReq", "buyThreatLevelByKarma", "buyThreatLevelByKarmaAck", "choosePet",
            "choosePetAck", "conversationNpcCheckAck", "conversationNpcCheckReq", "exploretaskcompleteAck", "exploretaskcompleteReq",
            "hasAssignedTalentPointAck", "hasAssignedTalentPointReq", "masteryLvlupAck", "masteryLvlupReq", "pingRequest",
            "pingRequestAck", "protecttaskBeginReq", "protecttaskEndReq", "roleAttribReq", "taskCompleteNTF",
            "unLockAreaAck", "unLockAreaReq", "useItem",
        };

        private static HashSet<int> _unknownIds;

        public static bool Supports(Packet packet)
        {
            int id = MethodId(packet);
            return !UnknownIds().Contains(id);
        }

        /// <summary>The packet unchanged (April 2013 can parse it as-is), a rebuilt 2013-layout
        /// packet, or null to drop.</summary>
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
                ProtocolGame_METHOD.uMethodID_SendMissionProcess => SendMissionProcess(cs),
                ProtocolGame_METHOD.uMethodID_MissionListAck => MissionListAck(cs),
                ProtocolGame_METHOD.uMethodID_ProcessRedeemGiftResult => ProcessRedeemGiftResult(cs),
                _ => packet,
            };
        }

        private static int MethodId(Packet packet) => BitConverter.ToInt32(packet.GetByteStream(), 6);

        // ---- mission id families -------------------------------------------------------------
        //
        // The client's own bounty-panel math (isOpenRegion: missionID / 100 % 10 == region + 1;
        // SetLevelInfo: missionID % 100 / 10 - 1 = slot, % 10 - 1 = threat) runs on the server's
        // 5RTM map ids - the shipped region-level prefabs' 1RM table ids (111..183) only appear
        // where the client fills CurrentMissionID from its own prefab data, so mission ids cross
        // inbound through ToServerMissionId and stay untouched going out. World map regions are
        // addressed as 5R00 areas (5100..5800, tutorial 5000), which the region-area collapse
        // in GameHandler.MissionListReq sends for both 2013 dialects.

        /// <summary>A 2013 table id (1RM, tutorial 101) back to the server mission (threat 1
        /// variant); the server's own 5RTM ids pass through.</summary>
        public static int ToServerMissionId(int clientMissionId)
        {
            if (clientMissionId >= 5000)
                return clientMissionId;
            int region = clientMissionId / 10 % 10, mission = clientMissionId % 10;
            return region is < 0 or > 8 || mission is < 1 or > 4 ? clientMissionId
                : 5000 + region * 100 + mission * 10 + 1;
        }

        // ---- error code eras -----------------------------------------------------------------
        //
        // EServerErrorType numbers drifted between the 2013 build and 2014 (codes inserted in the
        // item/mission/pet blocks). Sending 2014 numbers makes the April client render the wrong
        // popup - 109 (2014's "already got the day reward") prints as its 109
        // "eMission_Dont_Have_ReqLevel". The pairs below are read off both builds' TypeDefine.

        private static readonly Dictionary<int, int> ErrorByEra = new()
        {
            [(int)EServerErrorType.eGS_LessReviveItem] = 52,
            [(int)EServerErrorType.eGS_SkillNotExist] = 54,
            [(int)EServerErrorType.eFriendError_NoExist] = 68,
            [(int)EServerErrorType.eAlreadyHaveAlly] = 78,
            [(int)EServerErrorType.eItemError_NoExist] = 85,
            [(int)EServerErrorType.eItemError_CantSellItem] = 89,
            [(int)EServerErrorType.eItemError_BagFull] = 92,
            [(int)EServerErrorType.eItemError_CantBeSwapped] = 96,
            [(int)EServerErrorType.eDayRewardError_AlreadyGet] = 107,
            [(int)EServerErrorType.eMission_Not_exist] = 113,
            [(int)EServerErrorType.eMission_have_buy_area] = 117,
            [(int)EServerErrorType.eLeftTalentPoint_lack] = 120,
        };

        /// <summary>A 2014 error number as the April 2013 build counts it; unmapped codes (and
        /// eSuccess/eUnknownError, which match in both eras) pass through.</summary>
        public static int ToClientError(int code) => ErrorByEra.TryGetValue(code, out int era) ? era : code;

        private static HashSet<int> UnknownIds() => _unknownIds ??= new HashSet<int>(
            GameResponses.Names.Keys.Where(id => !Known.Contains(GameResponses.Names[id])));

        // ---- April 2013 struct writers (field lists exactly as the shipped client reads them) ----

        private static Packet Begin(int methodId)
        {
            var os = new Packet();
            os.WriteInt(0);
            os.WriteShort(0);
            os.WriteInt(methodId);
            return os;
        }

        /// <summary>PlayerReviveNumNTF under the 2013 protocol: a single int, how often the
        /// hunter has already stood up - the client feeds it to GetRevivalKarma as the column
        /// index into RevivalCost.Costs. The 2014 twin carries the crystal and karma revive
        /// counts; scroll counts arrive via UpdateReviveNum in both eras.</summary>
        public static Packet PlayerReviveNumNTF2013(int reviveNum)
        {
            Packet os = Begin(ProtocolBattle_METHOD.uMethodID_PlayerReviveNumNTF);
            os.WriteInt(reviveNum);
            os.Patch(os.get_used_size() - 6, 0);
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
            os.WriteInt(v.reviveNum);
            mapEvent.Write(os, v.eventData);
            SWellData.Write(os, v.wellData);
            // (no curCharSlotNum / isGetRevivalCard yet in April 2013)
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
            mapEvent.Write(os, v.eventData);
            // (no cooldownData map yet in April 2013)
        }

        private static void MissionRelate(Packet os, SAcceptMissionRelate2 v)
        {
            os.WriteInt(v.missionID);
            // (no coolDownTime in 2013)
            os.WriteInt(v.IsHaveCompleted);
            os.WriteBool(v.IsHaveTriggered);
            os.WriteInt(v.star);
            os.WriteInt(v.lastPassTime);
            os.WriteInt(v.canAcceptMaxLvl);
            // (no lastPassLvl in 2013)
        }

        private static void RedeemGift(Packet os, SRedeemGift v)
        {
            os.WriteString(v.title);
            os.WriteString(v.sender);
            os.WriteString(v.redeemCode);
            os.WriteString(v.giftXML); // the 2013 field was a plain content string
            os.WriteInt(v.karma);
            os.WriteInt(v.gold);
            // (2013 carried bare item ids, not the full item infos)
            os.WriteInt(v.ItemVec == null ? 0 : v.ItemVec.Count);
            if (v.ItemVec != null)
                foreach (SItemInfo item in v.ItemVec)
                    os.WriteInt(item.ID);
            vectorInt.Write(os, v.petIDVec);
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
            // (no stashbag in April 2013 - the client has no stash window)
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

            // Like the January build, this client has one relive counter; the karma one is what
            // its relive shop spent. The mission id stays the server's (the client's panel math
            // works on it directly).
            Packet os = Begin(ProtocolGame_METHOD.uMethodID_SendMissionProcess);
            os.WriteUint(missionID);
            vectorSBranch.Write(os, branches);
            os.WriteInt(karmaReliveNum);
            return Finish(os);
        }

        private static Packet MissionListAck(byte[] cs)
        {
            Packet ins = Body(cs);
            vectorSAcceptMissionRelate list = new vectorSAcceptMissionRelate();
            vectorSAcceptMissionRelate.Read(ins, out list);

            // The entries are the server's own ids (the panel's math runs on them) but written
            // with April's field list (no coolDownTime after the id, no lastPassLvl). Slots are
            // armed by MissionLevelToID, which searches each map's THREAT-1 id (5111, never the
            // 5110 map base the 2014 client uses), so the base moves up one. The tutorial map is
            // dropped (the village NPC starts it) as is each region's fourth mission (2014-only).
            var kept = new List<SAcceptMissionRelate2>();
            foreach (SAcceptMissionRelate2 m in list)
            {
                if (m.missionID / 100 % 10 == 0 || m.missionID % 100 / 10 == 4)
                    continue;
                m.missionID += 1;
                kept.Add(m);
            }

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_MissionListAck);
            os.WriteInt(kept.Count);
            foreach (SAcceptMissionRelate2 m in kept)
                MissionRelate(os, m);
            return Finish(os);
        }

        private static Packet ProcessRedeemGiftResult(byte[] cs)
        {
            Packet ins = Body(cs);
            SRedeemGift gift = new SRedeemGift(); SRedeemGift.Read(ins, out gift);
            EServerErrorType result = Proto.Error(EServerErrorType.eUnknownError);
            EServerErrorType.Read(ins, out result);

            Packet os = Begin(ProtocolGame_METHOD.uMethodID_ProcessRedeemGiftResult);
            RedeemGift(os, gift);
            EServerErrorType.Write(os, result);
            return Finish(os);
        }
    }
}
