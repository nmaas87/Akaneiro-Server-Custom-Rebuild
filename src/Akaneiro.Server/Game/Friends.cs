using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// A friend ally: a companion character the player hired from the summon window and
    /// which now walks the current scene. The client drives it (its own AI sends the ally's
    /// moves and attacks); the server keeps the authoritative health and the hire time and
    /// resolves every hit through the usual battle packets.
    /// </summary>
    public sealed class FriendAlly
    {
        /// <summary>The friend's character id, as it was in the summon list.</summary>
        public int FriendId { get; init; }

        /// <summary>Stand-in character (level, discipline, gear) that carries the stats.</summary>
        public CharacterData Character { get; init; }

        public PlayerStats Stats { get; init; }

        /// <summary>Battle object in the current scene; 0 = hired but not in a scene.</summary>
        public int ObjectId { get; set; }

        public Vector3 Position { get; set; }

        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Mp { get; set; }
        public int MaxMp { get; set; }

        /// <summary>End of the hire (the summon window shows "Duration 30 minutes").</summary>
        public DateTime HiredUntil { get; set; }

        /// <summary>Last hit taken; regeneration pauses after it for a moment.</summary>
        public DateTime LastHitAt { get; set; }

        /// <summary>When the next swing may start (the client's animation already paces it).</summary>
        public DateTime NextSwingAt { get; set; }

        /// <summary>Ability id -> ready time, and the next attribute sync to the client.</summary>
        public Dictionary<int, DateTime> Cooldowns { get; } = new Dictionary<int, DateTime>();
        public DateTime NextRegenerateAt { get; set; }

        /// <summary>The health and energy the client was last told, to know when to send a sync.</summary>
        public int SyncHp { get; set; }
        public int SyncMp { get; set; }

        /// <summary>The abilities in the list, plus the three the client always gives an ally.</summary>
        public IReadOnlyList<int> Skills { get; init; }

        public bool Present => ObjectId != 0 && Hp > 0;
    }

    /// <summary>
    /// The summon window's friend list. In the original these were real friends' characters
    /// from the social network, and hiring one was free (the window shows "Free" and
    /// "Duration 30 minutes"). The friend network is gone, so the server offers three
    /// invented companions per character - one per discipline - sized to the player: their
    /// level, gear level and abilities come from the game's own tables, so they fight about
    /// as well as a character of that level should. The trio is fixed per character
    /// (ids and names), their gear grows with the player.
    /// </summary>
    public static class Friends
    {
        /// <summary>
        /// Friend character ids stay far above the real character ids (which come from the
        /// account store's counter); they only ever appear inside one connection.
        /// </summary>
        public const int FriendIdBase = 900_000;

        public static int FriendIdFor(CharacterData me, int style) => FriendIdBase + me.Id * 8 + style;

        private static readonly (int Style, string Name, int Sex)[] Trio =
        {
            (StarterLoadout.Prowess, "Kenshin", 0),
            (StarterLoadout.Fortitude, "Benkei", 0),
            (StarterLoadout.Cunning, "Koga", 1),
        };

        /// <summary>What the summon window's FRIENDS tab shows for this character.</summary>
        public static List<SFriendCharInfo> Roster(CharacterData me, GameData data)
        {
            var roster = new List<SFriendCharInfo>();
            foreach ((int style, string name, int sex) in Trio)
            {
                CharacterData friend = Make(me, style, name, sex, data);
                var skills = new vectorInt();
                foreach (int skillId in SkillsOf(friend))
                    skills.Add(skillId);
                var info = new SFriendCharInfo
                {
                    ID = friend.Id,
                    nickname = friend.Name,
                    sex = new ESex(friend.Sex),
                    style = style,
                    level = friend.Level,
                    Exp = (ulong)Math.Max(0, LevelExperience(data, friend.Level)),
                    equipinfo = Equipment(friend),
                    skillVec = skills,
                };
                roster.Add(info);
            }
            return roster;
        }

        /// <summary>
        /// Hires one friend from the roster: a fresh companion sized to the player's current
        /// level, or null when the id is not on the list.
        /// </summary>
        public static FriendAlly Hire(CharacterData me, GameData data, int friendId)
        {
            foreach ((int style, string name, int sex) in Trio)
            {
                if (friendId != FriendIdFor(me, style))
                    continue;
                CharacterData friend = Make(me, style, name, sex, data);
                PlayerStats stats = PlayerStats.Compute(friend, data);
                return new FriendAlly
                {
                    FriendId = friendId,
                    Character = friend,
                    Stats = stats,
                    Hp = stats.MaxHp,
                    MaxHp = stats.MaxHp,
                    Mp = stats.MaxMp,
                    MaxMp = stats.MaxMp,
                    Skills = SkillsOf(friend),
                };
            }
            return null;
        }

        /// <summary>
        /// The companion as a character: the discipline's starter weapon and clothing (the
        /// same items character creation advertises, see StarterLoadout) but carried at the
        /// player's item level, the player's character level, and its talent points in its
        /// own discipline - a character balanced like the one the player made there.
        /// </summary>
        private static CharacterData Make(CharacterData me, int style, string name, int sex, GameData data)
        {
            var friend = new CharacterData
            {
                Id = FriendIdFor(me, style),
                Name = name,
                Sex = sex,
                Style = style,
                Level = Math.Max(1, me.Level),
                Exp = (ulong)Math.Max(0, LevelExperience(data, Math.Max(1, me.Level))),
                StarterGearGranted = true,
                StarterAbilitiesGranted = true,
            };
            friend.TalentProwess = style == StarterLoadout.Prowess ? friend.Level - 1 : 0;
            friend.TalentFortitude = style == StarterLoadout.Fortitude ? friend.Level - 1 : 0;
            friend.TalentCunning = style == StarterLoadout.Cunning ? friend.Level - 1 : 0;

            foreach ((int slot, int itemId, int prefabId) in StarterLoadout.GearFor(style))
                friend.Equipment.Add(new ItemData
                {
                    Slot = slot,
                    ItemId = itemId,
                    PrefabId = prefabId,
                    Level = friend.Level,
                });

            if (data.StartAbilities.TryGetValue(style, out IReadOnlyList<int> start))
                foreach (int abilityId in start)
                    if (abilityId != 0)
                        friend.Abilities.Add(new AbilityData { Id = abilityId });
            return friend;
        }

        private static long LevelExperience(GameData data, int level) =>
            data.Levels.FirstOrDefault(l => l.Level == level)?.ExpTotal ?? 0;

        /// <summary>The wire form of the companion's gear (list and summon panel show it).</summary>
        private static vectorSItemuuid Equipment(CharacterData friend)
        {
            var items = new vectorSItemuuid();
            foreach (ItemData item in friend.Equipment)
                items.Add(new itemuuid
                {
                    slotPart = item.Slot,
                    itemID = item.ItemId,
                    itemLevel = Math.Max(1, item.Level),
                    prefabID = item.PrefabId,
                });
            return items;
        }

        /// <summary>
        /// The abilities on the list. The ally state machine also always gives the character
        /// Shockwave, Ice Barricade and Caltrops themselves (AllyNpc.SetInitialAttribute),
        /// so those are on the list too and usable in battle.
        /// </summary>
        public static IReadOnlyList<int> SkillsOf(CharacterData friend) =>
            friend.Abilities.Select(a => a.Id)
                .Concat(new[] { Shockwave, IceBarricade, Caltrops })
                .ToList();

        public const int Shockwave = 10601, IceBarricade = 11101, Caltrops = 11701;
    }
}
