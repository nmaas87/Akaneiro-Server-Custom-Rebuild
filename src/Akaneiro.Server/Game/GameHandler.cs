using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>Game protocol (account, characters, inventory, missions, ...).</summary>
    public sealed class GameHandler : GameRequestHandlerBase
    {
        private const int MaxNameLength = 16;

        private readonly Session _s;

        public GameHandler(Session session) => _s = session;

        protected override void OnUnhandled(string request) => _s.Log.LogWarning("   unhandled game request {Request}", request);

        // ---- account ------------------------------------------------------------------------

        public override void UserLogin(string account, EUserType userType, EClientType clientType, string password, string tocken)
        {
            // Web login ("client ver"): client.php handed out a gameCode for this uid; it arrives
            // here as the token. This runs before the Steam path and only activates for tokens this
            // server issued through WebLoginService, so Steam logins take exactly the old branch.
            // Older client builds are marked on their gameCode: every send then goes through the
            // matching dialect rewrite/drop filter.
            ClientDialect dialect = ClientDialect.Modern;
            bool webAuthenticated = _s.World.Options.WebLogin
                && _s.World.WebUsers.ValidateToken(tocken, account, _s.World.Clock.UtcNow, out dialect);
            _s.Dialect = webAuthenticated ? dialect : ClientDialect.Modern;
            switch (_s.Dialect)
            {
                case ClientDialect.Zh2013:
                    _s.Log.LogInformation("   login {Account}: January 2013 client build, switching packet dialect", account);
                    break;
                case ClientDialect.Apr2013:
                    _s.Log.LogInformation("   login {Account}: April 2013 client build, switching packet dialect", account);
                    break;
            }

            // A uid that belongs to a web account must not be usable without its gameCode; every
            // other account string (Steam IDs and anything the editor path sends) keeps behaving as before.
            if (_s.World.Options.WebLogin && !webAuthenticated && _s.World.WebUsers.IsWebUid(account))
            {
                _s.Log.LogInformation("   web login uid {Account} rejected: no valid gameCode", account);
                _s.Send(GameResponses.UserLoginFailed(Proto.Error(EServerErrorType.eWrongAccountOrPwd)));
                return;
            }

            // The optional "who may play" list (Akaneiro:LoginAllowlist). As long as the file does
            // not exist everyone logs in; once it exists only the listed Steam IDs and e-mail
            // addresses can. A web login arrives as a numeric uid, so its account's e-mail is what
            // the list is matched against - that is what keeps the fat client's mail login working
            // once the host turns the list on. Runs before the reconnect handshake, so a listed
            // account is also the only one that can take over its own zombie session.
            if (!_s.World.Allowlist.Allows(account, _s.World.WebUsers.EmailOf(account)))
            {
                _s.Log.LogInformation("   login {Account} refused: not on the allow list", account);
                _s.Send(GameResponses.UserLoginFailed(Proto.Error(EServerErrorType.eWrongAccountOrPwd)));
                return;
            }

            // Steam builds send the Steam ID as account and the web-service token; nothing is verified locally.
            if (string.IsNullOrWhiteSpace(account))
                account = "local";

            // A live session still holds this account (a dropped connection the server has not
            // noticed yet, or a second machine). The protocol's reconnect handshake decides what
            // happens; the shipped clients answer UserReconnectNotify with false, which means
            // "kick the old one and log in fresh" - exactly the fix for a zombie session.
            Session previous = _s.World.OwnerOf(account);
            if (previous != null && previous != _s)
            {
                _s.PendingReconnect = account;
                _s.Log.LogInformation("   login {Account}: session #{Old} still lives, asking how to proceed",
                    account, previous.Character?.Name ?? "(no character)");
                _s.Send(GameResponses.UserReconnectNotify(
                    previous.Character?.Name ?? previous.LastCharacterName,
                    previous.Mission?.Mission.Name ?? ""));
                return;
            }

            FinishLogin(account);
        }

        /// <summary>The normal login tail: load the account, claim it, greet the client.</summary>
        private void FinishLogin(string account, bool reconnected = false)
        {
            _s.Account = _s.World.Accounts.LoadOrCreate(account);
            // Characters created before starter gear existed get it here, so the select screen shows it.
            if (_s.Account.Characters.Aggregate(false, (changed, c) => StarterLoadout.Grant(c, _s.Data) || changed))
                _s.SaveAccount();
            if (_s.Data.NormalizePrefabIds(_s.Account))
                _s.SaveAccount(); // items saved before they carried the client's icon index
            _s.ClaimAccount(account);
            _s.Log.LogInformation("   login: account {Account} ({Chars} characters)", account, _s.Account.Characters.Count);

            _s.Send(GameResponses.UserLoginOK(reconnected, Views.AccountInfo(_s.Account), Views.CharacterList(_s.Account, _s.Data)));
        }

        /// <summary>
        /// The answer to <see cref="UserReconnectNotify"/> (client <c>_UI_CS_Login</c> reconnect
        /// panel; the shipped NGUI builds answer false on their own). Both branches evict the old
        /// session - keeping it alive would let its saves roll back the new one's progress - and
        /// reload the account from disk so nothing written by the old one is lost. true resumes
        /// under the same character choice; false is a plain fresh login.
        /// </summary>
        public override void UserReconnect(bool isReconnect)
        {
            string account = _s.PendingReconnect;
            _s.PendingReconnect = null;
            if (account == null)
            {
                _s.Log.LogWarning("   UserReconnect({Choice}) without a pending handshake", isReconnect);
                return;
            }

            Session previous = _s.World.OwnerOf(account);
            if (previous != null && previous != _s)
            {
                _s.Log.LogInformation("   reconnect {Account}: ending session of '{Old}' ({Choice})",
                    account, previous.Character?.Name ?? "(no character)", isReconnect ? "resume" : "fresh login");
                _s.World.ReleaseAccount(account, previous);
                previous.Supersede();
            }

            FinishLogin(account, reconnected: isReconnect);
        }

        public override void UserLogout()
        {
            _s.SaveAccount();
            if (_s.AccountKey != null)
                _s.World.ReleaseAccount(_s.AccountKey, _s);
            _s.EndReason = "logged out";
        }

        public override void pingRequest() => _s.Send(GameResponses.pingRequestAck());

        // ---- characters ---------------------------------------------------------------------

        public override void UserCreateCharacter(SCharacterCreationData createdata)
        {
            if (_s.Account == null)
                return;

            string name = (createdata.nickname ?? "").Trim();
            int error = EServerErrorType.eSuccess;
            if (name.Length == 0 || name.Length > MaxNameLength)
                error = EServerErrorType.eUnknownError;
            else if (_s.Account.Characters.Count >= _s.Account.CharSlots)
                error = EServerErrorType.eUnknownError;
            else if (_s.World.Accounts.IsNameTaken(name))
                error = EServerErrorType.eUnknownError;

            if (error != EServerErrorType.eSuccess)
            {
                _s.Log.LogInformation("   character creation rejected: '{Name}'", name);
                _s.Send(GameResponses.UserCreateCharacterFailed(Proto.Error(error)));
                return;
            }

            var character = new CharacterData
            {
                Id = _s.World.Accounts.NewCharacterId(),
                Name = name,
                Sex = createdata.sex?.Get() ?? ESex.eSex_Male,
                Style = createdata.style,
                AvatarTraits = new Dictionary<int, int>(createdata.avatarTraits ?? new mapIntInt()),
                CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            StarterLoadout.Grant(character, _s.Data);
            _s.Mail.GrantKickstarterMail(character); // the "Kickstarter Exclusives" thank-you letter
            _s.Mail.GrantStarterPackMail(character); // the "Starter Pack Gifts" letter
            _s.Mail.GrantCatalogMail(character);     // "The Complete Rare Catalog": every unique + special
            _s.Account.Characters.Add(character);
            _s.SaveAccount();
            _s.Log.LogInformation("   created character #{Id} '{Name}'", character.Id, character.Name);

            _s.Send(GameResponses.UserCreateCharacterOK(Views.CharacterLogin(character, _s.Data)));
        }

        public override void UserDelCharacter(int characterID)
        {
            if (_s.Account == null)
                return;

            int removed = _s.Account.Characters.RemoveAll(c => c.Id == characterID);
            _s.SaveAccount();
            _s.Send(GameResponses.UserDelCharacterResult(characterID,
                Proto.Error(removed > 0 ? EServerErrorType.eSuccess : EServerErrorType.eGS_NoCharacter)));
        }

        public override void UserSelectCharacter(int characterID)
        {
            CharacterData character = _s.Account?.Characters.FirstOrDefault(c => c.Id == characterID);
            if (character == null)
            {
                _s.Send(GameResponses.UserSelectCharacterFailed(Proto.Error(EServerErrorType.eGS_NoCharacter)));
                return;
            }

            StarterLoadout.Grant(character, _s.Data); // characters created before starter gear existed
            Trainer.GrantStarterMasteries(character);
            _s.Character = character;
            _s.LastCharacterName = character.Name;
            _s.ActiveBuffs.Clear(); // food eaten by another character of this login
            _s.Account.LastCharacterId = character.Id;
            _s.SaveAccount();
            _s.Log.LogInformation("   selected character #{Id} '{Name}'", character.Id, character.Name);

            _s.Send(GameResponses.UserSelectCharacterOK(Views.CharacterBasic(character), Views.Attributes(character, _s.Data)));
            // The bag info makes the client build the player model (equipment and gender).
            _s.Send(GameResponses.SendBagInfo(Views.Bags(character)));
            // Abilities and their shortcut slots; the client answers with RoleMasteryListReq.
            _s.Send(GameResponses.SendSkillInfo(Views.Skills(character)));
            _s.Trainer.SendTimers();
        }

        public override void RoleMasteryListReq()
        {
            if (_s.Character != null)
                _s.Trainer.SendMasteries();
            else
                _s.Send(GameResponses.RoleMasteryListAck(new vectorMasteryLevelInfo()));
        }

        // ---- trainer -----------------------------------------------------------------------

        public override void StudyAbility(int npcID, int abilityID, EMoneyType moneyType)
        {
            if (_s.Character != null)
                _s.Trainer.StudyAbility(npcID, abilityID);
        }

        public override void masteryLvlupReq(EMasteryType masteryType)
        {
            if (_s.Character != null)
                _s.Trainer.LevelUpMastery(masteryType.Get());
        }

        public override void SpeedUpCoolDown(ECooldownType cooldownType, int id, EDecreaseCooldownType decreaseCooldownType)
        {
            if (_s.Character != null)
                _s.Trainer.SpeedUp(cooldownType.Get(), id, decreaseCooldownType.Get());
        }

        public override void LeaveWorld()
        {
            // Back to character selection (options menu "Log out").
            _s.SaveAccount();
            _s.Character = null;
            _s.SceneName = "";
        }

        // ---- village entry chain -------------------------------------------------------------
        // After loading the village the client asks, in this order:
        //   RequestDayReward -> (code 109 "already claimed") GetFriendReward -> (no rewards) hasAssignedTalentPointReq
        // and only returns to the in-game screen after the talent point answer.

        /// <summary>
        /// The daily login reward: once per (UTC) day, counting up while the player comes back day
        /// after day (DayReward.reward; after the last day it starts over). The client shows the
        /// reward screen and goes on with GetFriendReward; code 109 skips the screen.
        /// </summary>
        public override void RequestDayReward()
        {
            var reward = Proto.New<SDayReward>();
            DateTime now = _s.World.Clock.UtcNow;
            int today = now.Year * 10000 + now.Month * 100 + now.Day;
            DateTime yesterdayDate = now.AddDays(-1);
            int yesterday = yesterdayDate.Year * 10000 + yesterdayDate.Month * 100 + yesterdayDate.Day;
            AccountData account = _s.Account;
            IReadOnlyList<GameData.DayRewardRow> days = _s.Data.DayRewards;

            if (account == null || _s.Character == null || account.DayRewardLastDate == today || days.Count == 0)
            {
                reward.returnCode = Proto.Error(EServerErrorType.eDayRewardError_AlreadyGet);
                _s.Send(GameResponses.RequestDayRewardOK(reward));
                return;
            }

            account.DayRewardStreak = account.DayRewardLastDate == yesterday ? account.DayRewardStreak % days.Count + 1 : 1;
            account.DayRewardLastDate = today;
            GameData.DayRewardRow day = days[account.DayRewardStreak - 1];
            switch (day.Type)
            {
                case 1:
                    account.Karma += day.Count;
                    break;
                case 2:
                    account.Crystals += day.Count;
                    break;
                default:
                    if (day.Item != null && !_s.Inventory.AddToBag(Clone(day.Item)))
                        _s.Log.LogInformation("   bag full, day reward {Item} is lost", day.Name);
                    break;
            }
            _s.SaveAccount();
            _s.Log.LogInformation("   daily reward day {Day}: {Reward}", account.DayRewardStreak, day.Name);

            reward.dayID = account.DayRewardStreak;
            reward.daytime = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
            _s.DayRewardOnScreen = true; // its "Next" button asks for the friend reward (GetFriendReward)
            reward.returnCode = Proto.Error(EServerErrorType.eSuccess);
            _s.Send(GameResponses.RequestDayRewardOK(reward));
            _s.SendMoney();
        }

        private static ItemData Clone(ItemData item) => new ItemData
        {
            ItemId = item.ItemId, PrefabId = item.PrefabId, Level = item.Level, Gem = item.Gem,
            Enchant = item.Enchant, Element = item.Element, Count = item.Count,
        };

        /// <summary>
        /// After loading a map the client asks this twice: once when the loading screen ends, and again
        /// once the day reward turns out to be taken already (code 109), or when the day reward screen
        /// is closed. Its answer decides what comes next: with a finished mission the summary screen,
        /// otherwise the talent check, which switches to the normal game screen - so a second answer
        /// closes the summary the first one opened. Only one is answered per visit: with a day reward
        /// on screen the one after it (its "Next" button), otherwise the first.
        /// </summary>
        public override void GetFriendReward()
        {
            if (_s.FriendRewardAnswered || (_s.DayRewardOnScreen && !_s.FriendRewardSkippedOnce))
            {
                if (_s.DayRewardOnScreen)
                    _s.FriendRewardSkippedOnce = true;
                _s.Log.LogInformation("   friend reward request ignored (already answered this visit)");
                return;
            }
            _s.FriendRewardAnswered = true;
            _s.Send(GameResponses.SendFriendRewardInfo(new mapFriendHireReward()));
        }

        /// <summary>
        /// Spent and free talent points. With points left the client opens its level-up screen
        /// (CurLV > spent + 1), where the player picks a discipline (assignTalentReq).
        /// </summary>
        public override void hasAssignedTalentPointReq()
        {
            CharacterData c = _s.Character;
            if (c == null)
            {
                _s.Send(GameResponses.hasAssignedTalentPointAck(0, 0, 0, 0));
                return;
            }
            _s.Send(GameResponses.hasAssignedTalentPointAck(c.TalentProwess, c.TalentFortitude, c.TalentCunning, Views.FreeTalentPoints(c)));
        }

        public override void assignTalentReq(int talent1, int talent2, int talent3)
        {
            CharacterData c = _s.Character;
            if (c == null)
                return;
            if (talent1 < 0 || talent2 < 0 || talent3 < 0 || talent1 + talent2 + talent3 > Views.FreeTalentPoints(c))
            {
                _s.Send(GameResponses.assignTalentAck(Proto.Error(EServerErrorType.eLeftTalentPoint_lack)));
                return;
            }
            c.TalentProwess += talent1;
            c.TalentFortitude += talent2;
            c.TalentCunning += talent3;
            _s.SaveAccount();
            _s.Log.LogInformation("   talents: prowess {P}, fortitude {F}, cunning {C}", c.TalentProwess, c.TalentFortitude, c.TalentCunning);
            _s.Send(GameResponses.assignTalentAck(Proto.Error(EServerErrorType.eSuccess)));
            _s.Battle.SendAttributes();
        }

        public override void RequestBagInfo()
        {
            if (_s.Character != null)
                _s.Send(GameResponses.SendBagInfo(Views.Bags(_s.Character)));
        }

        // ---- inventory ---------------------------------------------------------------------

        public override void EquipItem(byte part, byte bagID, uint slot)
        {
            if (_s.Character != null)
                _s.Inventory.Equip(part, bagID, slot);
        }

        public override void UnequipItem(byte part, byte bagID, uint slot)
        {
            if (_s.Character != null)
                _s.Inventory.Unequip(part, bagID, slot);
        }

        public override void SwapItem(byte srcbagID, uint srcslot, byte dstbagID, uint dstslot)
        {
            if (_s.Character != null)
                _s.Inventory.Swap(srcbagID, srcslot, dstbagID, dstslot);
        }

        public override void DelItem(byte bagID, uint slot)
        {
            if (_s.Character != null)
                _s.Inventory.Delete(bagID, slot);
        }

        public override void useItem(byte bagID, uint slot)
        {
            if (_s.Character != null)
                _s.Inventory.Use(bagID, slot);
        }

        // ---- stash, well, spirit helpers --------------------------------------------------------

        public override void buyStash()
        {
            if (_s.Character != null)
                _s.Inventory.BuyStashTab();
        }

        /// <summary>The well asks for its state as soon as it is placed in the village (NPC_Well.Awake).</summary>
        public override void GetWellData()
        {
            if (_s.Account != null)
                _s.Well.SendData();
        }

        public override void GetWellKarma()
        {
            if (_s.Account != null)
                _s.Well.Collect();
        }

        public override void RolePetListReq()
        {
            if (_s.Character != null)
                _s.Spirits.SendList();
        }

        public override void BuyPet(int PetID, int Num)
        {
            if (_s.Character != null)
                _s.Spirits.Rent(PetID, Num);
        }

        public override void choosePet(int PetID)
        {
            if (_s.Character != null)
                _s.Spirits.Choose(PetID);
        }

        public override void CraftingItem(byte bagID, uint itemslot, ECraftType craftType, EMoneyType moneyType, int craftID, mapCraftMaterial materialMap, bool buyMaterial)
        {
            if (_s.Character != null)
                _s.Crafting.Craft(bagID, itemslot, craftType.Get(), moneyType.Get(), craftID, buyMaterial);
        }

        // ---- small things -------------------------------------------------------------------
        // Answers the client waits for; single player has nobody to chat or trade with.

        /// <summary>Chat has no other players to reach; the message is echoed so it shows in the chat box.</summary>
        public override void ChatRequest(EChatType chatType, int targetID, int itemID, string chatMsg) =>
            _s.Send(GameResponses.ChatRequestAck(chatType, targetID, itemID, chatMsg, EServerErrorType.eSuccess));

        /// <summary>Cuts a mission map's rest short (free: crystals cost nothing here).</summary>
        public override void decreaseCoolDown(int missionID, int bType)
        {
            if (_s.Character != null)
            {
                MissionMap.Shorten(_s.Character, missionID, bType,
                    new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds());
                _s.SaveAccount();
            }
            _s.Send(GameResponses.decreaseCoolDownAck(Proto.Error(EServerErrorType.eSuccess), missionID, bType));
        }

        /// <summary>Gift and redeem codes were handed out by the original operators; none are valid here.</summary>
        public override void ProcessRedeemGift(string redeemCode) =>
            _s.Send(GameResponses.ProcessRedeemGiftResult(Proto.New<SRedeemGift>(), Proto.Error(EServerErrorType.eUnknownError)));

        public override void GetGiftReq(string key) =>
            _s.Send(GameResponses.GetGiftAck(Proto.New<SGift>(), Proto.Error(EServerErrorType.eUnknownError)));

        // The mailbox holds the reward letters (see MailService); the client polls it every minute.
        public override void DeleteMail(int mailID)
        {
            if (_s.Character != null)
                _s.Mail.Delete(mailID);
        }

        public override void GetMailItem(int mailID, int itemSlotID)
        {
            if (_s.Character != null)
                _s.Mail.GetItem(mailID, itemSlotID);
        }

        public override void GetMailPet(int mailID, int petSlotID) =>
            _s.Send(GameResponses.GetMailPetResult(Proto.Error(EServerErrorType.eUnknownError), mailID, petSlotID));

        public override void GetMailKarma(int mailID)
        {
            if (_s.Character != null)
                _s.Mail.GetKarma(mailID);
        }

        public override void GetMailCrystal(int mailID)
        {
            if (_s.Character != null)
                _s.Mail.GetCrystal(mailID);
        }

        public override void GetMailAll(int mailID)
        {
            if (_s.Character != null)
                _s.Mail.GetAll(mailID);
        }

        /// <summary>Opening a letter marks it read (the unread badge on the HUD drops); no answer.</summary>
        public override void OpenMail(int mailID)
        {
            if (_s.Character != null)
                _s.Mail.Open(mailID);
        }

        /// <summary>Player to player mail needs the recipient's account loaded; not supported here.</summary>
        public override void SendMail(SMailInfo mailInfo, vectorInt itemSlotVec) =>
            _s.Send(GameResponses.SendMailResult(Proto.Error(EServerErrorType.eMailError_MailPlayerNotExist),
                Proto.New<SMailInfo>()));

        // ---- vendors -----------------------------------------------------------------------

        public override void RequestPlayerShopInfo(bool isRare)
        {
            if (_s.Character != null)
                _s.Shop.SendStock(isRare);
        }

        public override void BuyItem(bool isRare, SBuyitemInfo info)
        {
            if (_s.Character != null)
                _s.Shop.Buy(isRare, info);
        }

        public override void BuyConsumeItem(int consumeItemType, int itemID, int buyCount)
        {
            if (_s.Character != null)
                _s.Shop.BuyConsumable(consumeItemType, itemID, buyCount);
        }

        public override void SaleItem(byte bagID, uint slot)
        {
            if (_s.Character != null)
                _s.Shop.Sell(bagID, slot);
        }

        /// <summary>
        /// The client reports achievements-like events here (EventSystem: level reached, first
        /// kill, ...) and expects no answer; the original stored them for statistics.
        /// </summary>
        public override void UpdateEvent(EEventDBType eventDBType, int eventID, string eventData)
        {
        }

        /// <summary>
        /// The missions the player may pick on the world map. The client opens its mission screen
        /// when this arrives (On_MissionListAck).
        /// <para>
        /// One entry per map, under the map's base id (5111..5114 -> 5110): each map sprite of the
        /// mission screen looks itself up by that id and hides when it is missing, and the hunt
        /// button accepts base id + threat (1..4). <c>star</c> is a bit mask of the threats already
        /// beaten (1, 2, 4, 8), <c>canAcceptMaxLvl</c> the number of threat buttons unlocked.
        /// </para>
        /// </summary>
        public override void MissionListReq()
        {
            if (_s.Character == null)
                return;

            var list = new vectorSAcceptMissionRelate();
            HashSet<int> unlocked = MissionMap.Unlocked(_s.Character, _s.Data);
            long now = new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
            foreach (IGrouping<int, MissionDefinition> map in _s.Data.Missions.Values
                         .GroupBy(m => m.Id - m.Id % 10).OrderBy(g => g.Key))
            {
                // Maps nobody has opened up yet stay dark on the map (MissionPanel: not in the list = locked).
                if (!map.Any(m => unlocked.Contains(m.Id)))
                    continue;
                int openThreat = map.Where(m => unlocked.Contains(m.Id)).Max(m => m.Id % 10);
                int beaten = 0, highest = 0;
                foreach (MissionDefinition variant in map)
                {
                    int threat = variant.Id % 10;
                    if (threat is >= 1 and <= 4 && _s.Character.CompletedMissions.Contains(variant.Id))
                    {
                        beaten |= 1 << (threat - 1);
                        highest = Math.Max(highest, threat);
                    }
                }
                int threats = map.Max(m => m.Id % 10);
                list.Add(new SAcceptMissionRelate2
                {
                    missionID = map.Key,
                    // No rest between runs: the client's speed-up panel does not reach the server here,
                    // and on a local single-player server a waiting time only gets in the way
                    // (the original's MissionsThreatCycle rules stay in MissionMap, switched off).
                    coolDownTime = MissionMap.CooldownsEnabled ? MissionMap.CooldownSeconds(_s.Character, map.Key, now) : 0,
                    IsHaveCompleted = beaten != 0 ? 1 : 0,
                    IsHaveTriggered = beaten != 0,
                    star = beaten,
                    lastPassTime = 0,
                    // Beating a threat unlocks the next one; the lowest is always open.
                    canAcceptMaxLvl = Math.Clamp(Math.Max(highest + 1, openThreat), 1, Math.Max(threats, 1)),
                    lastPassLvl = highest,
                });
            }
            _s.Log.LogInformation("   mission list: {Count} maps", list.Count);

            // 2013-era clients only bring their mission panel up when the matching area-info push
            // follows (On_MissionAreaInfoAck -> UpdateMapLockState + initMissionMap); the 2014 UI
            // already opens on the list answer itself. The April client addresses whole areas
            // (5000 tutorial, 5100..5800) and its UpdateMapLockState matches those ids exactly,
            // so the maps of one region collapse into one entry; the January client keeps the map
            // base ids. isBuy marks karma-purchased areas (the lock check ignores it - unlocked
            // is unlocked). The entries are read before the list goes out: for the April dialect
            // that send rewrites them in place to the client's own mission id family.
            if (_s.Dialect != ClientDialect.Modern)
            {
                var areas = new vectorSMissionAreaRelate();
                var seen = new HashSet<int>();
                foreach (SAcceptMissionRelate2 m in list)
                {
                    int areaId = _s.Dialect == ClientDialect.Apr2013
                        ? Apr2013Dialect.ToClientAreaId(m.missionID) : m.missionID;
                    // the tutorial's area (5000) has no region icon on the April world map;
                    // the January client's push keeps its entries untouched
                    if (_s.Dialect == ClientDialect.Apr2013 && areaId / 100 % 10 == 0)
                        continue;
                    if (seen.Add(areaId))
                        areas.Add(new SMissionAreaRelate { areaID = areaId, isBuy = _s.Character.UnlockedAreas.Contains(areaId) });
                }
                _s.Send(GameResponses.MissionListAck(list));
                _s.Send(GameResponses.MissionAreaInfoAck(areas));
            }
            else
            {
                _s.Send(GameResponses.MissionListAck(list));
            }
        }

        /// <summary>
        /// The 2013-era clients buy whole mission regions with karma on the bounty map
        /// ("Jawbone Ravine - Unlock 50 shards", MissionArea.Area: IsFree / Buykarma); the 2014
        /// client gates regions through mission chains instead and never sends this. The ack is
        /// (areaID, error): the client only lights the region icon up on eSuccess, and
        /// eMission_have_buy_area (119) / eMission_not_enough_badge (118, the karma shortage code)
        /// pop up a message.
        /// </summary>
        public override void unLockAreaReq(int areaID)
        {
            if (_s.Character == null || _s.Account == null)
                return;

            if (!_s.Data.MissionAreas.TryGetValue(areaID, out GameData.AreaRow area))
            {
                _s.Log.LogWarning("   unlock area {Area}: not in the area table", areaID);
                _s.Send(GameResponses.unLockAreaAck(areaID, Proto.Error(115))); // eMission_Not_exist
                return;
            }

            if (_s.Character.UnlockedAreas.Contains(areaID))
            {
                _s.Send(GameResponses.unLockAreaAck(areaID, Proto.Error(EServerErrorType.eSuccess)));
                return;
            }

            if (!area.IsFree && _s.Account.Karma < area.BuyKarma)
            {
                _s.Send(GameResponses.unLockAreaAck(areaID, Proto.Error(118))); // not enough karma
                _s.SendMoney();
                return;
            }

            int cost = area.IsFree ? 0 : area.BuyKarma;
            _s.Account.Karma -= cost;
            _s.Character.UnlockedAreas.Add(areaID);
            _s.SaveAccount();
            _s.Log.LogInformation("   area {Area} unlocked for {Cost} karma", areaID, cost);
            _s.Send(GameResponses.unLockAreaAck(areaID, Proto.Error(EServerErrorType.eSuccess)));
            _s.SendMoney();
            // the fresh list + area push lights the region up without another panel open
            MissionListReq();
        }

        /// <summary>Objectives the client reports itself: reaching a place or talking to an NPC.</summary>
        public override void conversationNpcCheckReq(int MisssionID, int taskid, int trackType, int objectID, int NpcID)
        {
            _s.Battle.RecordMissionProgress(MissionObjectiveType.Travel, objectID);
            _s.Send(GameResponses.conversationNpcCheckAck(MisssionID, taskid, trackType, objectID, NpcID,
                Proto.Error(EServerErrorType.eSuccess)));
        }

        public override void exploretaskcompleteReq(int missionID, int branchID, int taskid, int trackType, int objectID)
        {
            _s.Battle.RecordMissionProgress(MissionObjectiveType.Travel, objectID);
            _s.Send(GameResponses.exploretaskcompleteAck(missionID, taskid, trackType, objectID,
                Proto.Error(EServerErrorType.eSuccess)));
        }

        public override void MissionFailReq(int missionID)
        {
            // a 2013 client names the mission in its own id family (the MissionFailAck echo goes
            // back through the dialect, which maps ids for that dialect as well)
            if (_s.Dialect == ClientDialect.Apr2013)
                missionID = Apr2013Dialect.ToServerMissionId(missionID);
            _s.Log.LogInformation("   mission {Mission} given up", missionID);
            _s.Mission = null;
            _s.Send(GameResponses.MissionFailAck(missionID, 1));
        }

        // ---- mail --------------------------------------------------------------------------

        public override void GetPlayerMail()
        {
            if (_s.Character == null)
                _s.Send(GameResponses.GetPlayerMailResult(new vectorMailInfos()));
            else
                _s.Mail.SendMailbox(); // thanks to the Kickstarter backers, and friend gifts
        }

        /// <summary>
        /// The claim button of the friend-reward screen (client <c>SummonRewardPanel</c>). Single
        /// player has nobody who could have hired this character, so there is nothing to collect;
        /// answering with the (empty) reward list is what lets the client leave that screen.
        /// </summary>
        public override void ProcessFriendReward() =>
            _s.Send(GameResponses.SendFriendRewardInfo(new mapFriendHireReward()));

        /// <summary>
        /// The dev/test login's "load this map right away" path (<c>_UI_CS_TestModeCtrl</c>): the
        /// client sends the map list instead of the usual EnterScene, and loads each map when the
        /// EnterSceneOk for it arrives.
        /// </summary>
        public override void ReloadMap(vectorString mapnameVec)
        {
            foreach (string mapName in mapnameVec)
                _s.Battle.EnterSceneByName(mapName);
        }

        // ---- missions -----------------------------------------------------------------------

        public override void TutorialEnterReq(int charID)
        {
            // The client follows up with AcceptMission for the tutorial village; nothing to do here.
        }

        public override void AcceptMission(int npcID, int missionID)
        {
            // The 2013 client picks missions by its table id (1RM); the definitions live under
            // the 5RTM family, threat 1 (2013 had no threat mission variants).
            if (_s.Dialect == ClientDialect.Apr2013)
                missionID = Apr2013Dialect.ToServerMissionId(missionID);

            // The client already knows which scene belongs to the mission and loads it on success
            // (On_AcceptMissionOk -> EnterScene). Everything it can pick comes from the mission
            // list, so anything outside the unlocked set is a modified client: refuse it like
            // every other progression gate.
            if (!_s.Data.Missions.TryGetValue(missionID, out MissionDefinition mission))
            {
                _s.Log.LogWarning("   accept mission {Mission}: no definition found", missionID);
                _s.Send(GameResponses.AcceptMissionFail(1));
                return;
            }
            if (_s.Character == null || !MissionMap.Unlocked(_s.Character, _s.Data).Contains(missionID))
            {
                _s.Log.LogWarning("   accept mission {Mission} '{Name}': not unlocked for this character",
                    missionID, mission.Name);
                _s.Send(GameResponses.AcceptMissionFail(1));
                return;
            }

            _s.Mission = new MissionProgress(mission);
            _s.Log.LogInformation("   accept mission {Mission} '{Name}' ({Branches} branches)",
                missionID, mission.Name, mission.Branches.Count);

            _s.Send(GameResponses.AcceptMissionOk(1));
        }
    }
}
