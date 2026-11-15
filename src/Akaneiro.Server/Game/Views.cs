using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;

namespace Akaneiro.Server.Game
{
    /// <summary>Builds protocol structures from save data.</summary>
    public static class Views
    {
        public static SAccountInfo AccountInfo(AccountData account, bool aprilCharges = false)
        {
            var info = Proto.New<SAccountInfo>();
            info.account = account.Account;
            info.nickName = account.Account;
            info.id = account.Id;
            info.userType = new EUserType(EUserType.eUserType_Steam);
            info.createTime = DateTimeOffset.FromUnixTimeSeconds(account.CreatedUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            info.lastCharID = account.LastCharacterId;
            info.SK = account.Karma;
            info.FK = account.Crystals;
            info.curCharSlotNum = account.CharSlots;
            // The April client shows this number as its Shinigami Scroll counter; in the 2013
            // model it is the account's charge number, not a bag count.
            info.reviveNum = aprilCharges ? account.ReviveCharges : ReviveScrollCount(account);
            return info;
        }

        /// <summary>Item id of the Shinigami Scroll (Props.General); one scroll revives once.</summary>
        public const int ReviveScrollItem = 20103;

        /// <summary>
        /// Revive scrolls lying in the bags of the account's characters (the 2014 model). The
        /// client shows this number on the revive screen ("left revive number", accountInfo.reviveNum
        /// and On_UpdateReviveNum); a scroll-type revival (eReviveType_Item) consumes one from the
        /// active character's bag. The 2013 build instead keeps account.ReviveCharges.
        /// </summary>
        public static int ReviveScrollCount(AccountData account) =>
            account.Characters.Sum(c => c.Bag.Where(i => i.ItemId == ReviveScrollItem).Sum(i => i.Count));

        /// <summary>
        /// Turns 2013-model leftovers - scroll items carried over from 2014-era play - into the
        /// account's charge number, so the April counter matches what the client can spend.
        /// Returns whether anything moved.
        /// </summary>
        public static bool MigrateAprilScrolls(AccountData account)
        {
            int moved = 0;
            foreach (CharacterData character in account.Characters)
                for (int i = character.Bag.Count - 1; i >= 0; i--)
                    if (character.Bag[i].ItemId == ReviveScrollItem)
                    {
                        moved += character.Bag[i].Count;
                        character.Bag.RemoveAt(i);
                    }
            if (moved == 0)
                return false;
            account.ReviveCharges += moved;
            return true;
        }

        public static vectorCharacterInfoLogins CharacterList(AccountData account, GameData data)
        {
            var list = new vectorCharacterInfoLogins();
            foreach (CharacterData c in account.Characters)
                list.Add(CharacterLogin(c, data));
            return list;
        }

        public static SCharacterInfoLogin CharacterLogin(CharacterData c, GameData data)
        {
            var info = Proto.New<SCharacterInfoLogin>();
            info.ID = c.Id;
            info.nickname = c.Name;
            info.sex = new ESex(c.Sex);
            info.style = c.Style;
            info.level = c.Level;
            info.Exp = c.Exp;
            info.creationTime = new NatureTime((int)c.CreatedUnix);
            info.isTutorial = !c.TutorialDone;
            info.attrVec = Attributes(c, data);
            foreach (ItemData item in c.Equipment)
            {
                info.equipinfo.Add(new itemuuid
                {
                    slotPart = item.Slot,
                    itemID = item.ItemId,
                    itemLevel = item.Level,
                    gemID = item.Gem,
                    elementID = item.Element,
                    enchantID = item.Enchant,
                    prefabID = item.PrefabId,
                });
            }
            return info;
        }

        // Bag ids as used by the client's item requests (e.g. EquipItem(part, 2, slot) for the main bag).
        public const byte BagEquip = 1;
        public const byte BagBase = 2;
        public const byte BagBank = 3;
        public const byte BagStash = 4;

        /// <summary>All bags of a character (SendBagInfo). The client rebuilds the player model from the equip bag.</summary>
        public static SBagGroup Bags(CharacterData c)
        {
            var group = Proto.New<SBagGroup>();
            group.equipbag = Bag(BagEquip, 9, c.Equipment);
            group.basebag = Bag(BagBase, 40, c.Bag);
            group.bankbag = Bag(BagBank, 0, new List<ItemData>());
            // The client takes capacity / 12 as the number of stash tabs (PlayerDataManager.SetStashMaxTab).
            group.stashbag = Bag(BagStash, (uint)(c.StashTabs * Inventory.StashSlotsPerTab), c.Stash);
            return group;
        }

        private static SBagInfo Bag(byte id, uint capacity, List<ItemData> items)
        {
            var bag = Proto.New<SBagInfo>();
            bag.bagID = id;
            bag.capacity = capacity;
            foreach (ItemData item in items)
                bag.iteminfos.Add(Item(item));
            return bag;
        }

        /// <summary>Learned abilities and their shortcut slots (SendSkillInfo).</summary>
        public static SPlayerSkillInfo Skills(CharacterData c)
        {
            var info = Proto.New<SPlayerSkillInfo>();
            foreach (AbilityData ability in c.Abilities)
            {
                info.skillInfoVec.Add(new SSkillInfo
                {
                    skillID = ability.Id,
                    level = ability.Level,
                    skillExp = ability.Exp,
                    coldTime = 0,
                });
            }
            foreach (ShortcutData shortcut in c.Shortcuts)
            {
                info.skillShortcutVec.Add(new SSkillShortcut
                {
                    groupIdx = shortcut.Group,
                    idx = shortcut.Index,
                    skillID = shortcut.AbilityId,
                });
            }
            return info;
        }

        public static SItemInfo Item(ItemData item)
        {
            var info = Proto.New<SItemInfo>();
            info.ID = item.ItemId;
            info.perfrab = item.PrefabId;
            info.level = (uint)item.Level;
            info.guid.serialID = item.Serial;
            info.enchant = item.Enchant;
            info.element = item.Element;
            info.gem = item.Gem;
            info.slot = (uint)item.Slot;
            info.count = (uint)item.Count;
            return info;
        }

        public static SCharacterInfoBasic CharacterBasic(CharacterData c)
        {
            var info = Proto.New<SCharacterInfoBasic>();
            info.ID = c.Id;
            info.nickname = c.Name;
            info.style = c.Style;
            info.level = c.Level;
            info.exp = c.Exp;
            info.sex = new ESex(c.Sex);
            info.creationTime = new NatureTime((int)c.CreatedUnix);
            info.firstlogin = !c.TutorialDone;
            return info;
        }

        // The client regenerates by itself every 0.5 s using the HPRecover/MPRecover attributes
        // (PlayerAttributionManager.RegenHPAndMP), so they decide the regeneration speed.
        // Health does not regenerate on its own (PlayerStats adds it for buffs and Heavy Armor):
        // the game has food items for that, and regeneration would otherwise outpace enemy damage.
        // Energy refills in ~25 s (rate not known from the data).
        public static int MpRecoverPerTick(GameData.LevelRow level) => Math.Max(1, level.MaxMp / 50);

        /// <summary>Regeneration ticks per second (the client's 0.5 s rhythm).</summary>
        public const double RegenTicksPerSecond = 2.0;

        /// <summary>Full attribute set of a character, from <see cref="PlayerStats"/>.</summary>
        public static vectorAttrChange Attributes(CharacterData c, GameData data, PlayerStats stats = null)
        {
            return Proto.Attrs(AttributeMap(c, data, stats));
        }

        /// <summary>
        /// POW, DEF and SKILL one talent point brings, per discipline (LevelUpScreenCtrl.InitBaseData:
        /// prowess 30/10/20, fortitude 20/30/10, cunning 10/20/30).
        /// </summary>
        private static readonly Dictionary<int, (int Power, int Defense, int Skill)> TalentGain = new()
        {
            [StarterLoadout.Prowess] = (30, 10, 20),
            [StarterLoadout.Fortitude] = (20, 30, 10),
            [StarterLoadout.Cunning] = (10, 20, 30),
        };

        /// <summary>
        /// The character's POW/DEF/SKILL: one point for the discipline chosen at creation plus every
        /// talent point spent since. Abilities need them to be learned (Ability.ReqBaseStatAmount)
        /// and scale with them.
        /// </summary>
        public static (int Power, int Defense, int Skill) BaseStats(CharacterData c)
        {
            int power = 0, defense = 0, skill = 0;
            void Add(int discipline, int points)
            {
                if (points > 0 && TalentGain.TryGetValue(discipline, out var gain))
                {
                    power += gain.Power * points;
                    defense += gain.Defense * points;
                    skill += gain.Skill * points;
                }
            }
            Add(c.Style, 1);
            Add(StarterLoadout.Prowess, c.TalentProwess);
            Add(StarterLoadout.Fortitude, c.TalentFortitude);
            Add(StarterLoadout.Cunning, c.TalentCunning);
            return (power, defense, skill);
        }

        /// <summary>Talent points earned but not spent yet (one per level from level 2).</summary>
        public static int FreeTalentPoints(CharacterData c) =>
            Math.Max(0, c.Level - 1 - c.TalentProwess - c.TalentFortitude - c.TalentCunning);

        /// <summary>
        /// The attributes the client shows and uses (move and attack speed, regeneration), from
        /// <see cref="PlayerStats"/>; without running buffs when none are given.
        /// </summary>
        public static Dictionary<int, int> AttributeMap(CharacterData c, GameData data, PlayerStats stats = null)
        {
            stats ??= PlayerStats.Compute(c, data);
            return new Dictionary<int, int>
            {
                [EAttributeType.ATTR_CurHP] = stats.MaxHp,
                [EAttributeType.ATTR_CurMP] = stats.MaxMp,
                [EAttributeType.ATTR_MaxHP] = stats.MaxHp,
                [EAttributeType.ATTR_MaxMP] = stats.MaxMp,
                [EAttributeType.ATTR_Power] = stats.Power,
                [EAttributeType.ATTR_Defense] = stats.Defense,
                [EAttributeType.ATTR_Skill] = stats.Skill,
                [EAttributeType.ATTR_PhyAtk] = 10,
                [EAttributeType.ATTR_DamageReduction] = (int)Math.Round(Combat.DamageReduction(c, data, stats) * 100),
                [EAttributeType.ATTR_Critical] = (int)Math.Round(stats.CritChance),
                [EAttributeType.ATTR_HPRecover] = stats.HpRegen,
                [EAttributeType.ATTR_MPRecover] = stats.MpRegen,
                [EAttributeType.ATTR_MoveSpeed] = stats.MoveSpeed,     // percent of normal
                [EAttributeType.ATTR_AttackSpeed] = stats.AttackSpeed, // percent of normal
                [EAttributeType.ATTR_CriticalDamage] = (int)Math.Round(stats.CritDamage),
            };
        }
    }
}
