using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Static game data. Tables are read from the client's Resources/Localization/EN/*.txt files
    /// (tab separated; line 1 = column types, line 2 = column names, line 3 = descriptions).
    /// Level data (spawn points, spawners, triggers, objects, monsters, missions) comes from the
    /// GameData/*.json files that tools/SceneExtract generates from the client's Unity assets.
    /// </summary>
    public sealed class GameData
    {
        public sealed class LevelRow
        {
            public int Level;
            public long ExpTotal;
            public int MaxHp;
            public int MaxMp;
        }

        public sealed class AbilityRow
        {
            public int Id;
            public string Name;
            public int MpNeed;
            public int CooldownMs;
            public int Discipline;
            public float BaseVal;
            public float AbilityBonus;
            public float AttackCoefficient;

            /// <summary>Attribute the ability scales with (EAttributeType: 4 = power, 5 = defense, 6 = skill).</summary>
            public int AttrType;

            /// <summary>0 = single target, otherwise the ability hits everything within <see cref="AreaRadius"/>.</summary>
            public int AreaType;

            /// <summary>Radius of an area ability, and the reach of a single-target one.</summary>
            public float AreaRadius;

            /// <summary>Ability.TargetType: 3 = the caster (buffs and heals), otherwise enemies.</summary>
            public int TargetType;

            /// <summary>Where the ability happens: 0 = at the caster, 1 = at the sent position, 2 = in the sent direction.</summary>
            public int AttackPosType;

            /// <summary>How often one cast hits.</summary>
            public int AttackCount;

            /// <summary>Abilities like Meteor Rain or Caltrops place an object in the world; 0 = none.</summary>
            public int ObjectId;

            /// <summary>Length of the ability animation in milliseconds.</summary>
            public int AnimDurationMs;

            /// <summary>POW, DEF or SKILL (by <see cref="Discipline"/>) needed to learn it at the trainer.</summary>
            public int RequiredStat;

            /// <summary>What learning it costs.</summary>
            public int KarmaCost;

            /// <summary>How long learning takes; 0 for every ability the trainer teaches.</summary>
            public float TrainingSeconds;

            /// <summary>Abilities of one kind share the id without its last two digits (Rain of Blows I..VI = 105xx).</summary>
            public int Kind => Id / 100;

            /// <summary>1 for "I", 2 for "II", ...</summary>
            public int Rank => Id % 100;
        }

        /// <summary>An item that can drop, with the weight it has inside its table.</summary>
        public sealed class LootItemRow
        {
            public int Id;
            public string Name;
            public int DropChance;
        }

        /// <summary>A model variant of an item, usable for item levels Min..Max.</summary>
        public sealed class ItemPrefabRow
        {
            public int PrefabId;
            public string Name;
            public int MinLevel;
            public int MaxLevel;

            /// <summary>Armor class of an armor model (ArmorAttribute.Prefab Type): 8 light, 16 medium, 32 heavy.</summary>
            public int ArmorClass;
        }

        public sealed class WeaponRow
        {
            public int Id;
            public string Name;
            public string WeaponClass;
            public int DropChance;
            public int DamageMin;
            public int DamageMax;
            public float AttackRate;
            public float AttackRange;
            public int AttackAngle;

            /// <summary>"Attack Speed" column: how long a swing takes compared to a normal one (Claw 0.8 ... Great Maul 1.3).</summary>
            public float SwingFactor = 1f;

            public bool TwoHanded => WeaponClass.StartsWith("Two", System.StringComparison.OrdinalIgnoreCase);
        }

        public sealed class SceneInfo
        {
            public float[] Spawn { get; set; }

            public Vector3 SpawnPosition => new Vector3(Spawn[0], Spawn[1], Spawn[2]);

            /// <summary>Items the level's tutorial waits for the player to pick up (ActiveCollect).</summary>
            public List<CollectTarget> CollectTargets { get; set; } = new List<CollectTarget>();

            /// <summary>Vendors, trainers, bounty board and the like placed in the scene (ShopNpc).</summary>
            public List<NpcPlacement> Npcs { get; set; } = new List<NpcPlacement>();
        }

        /// <summary>An NPC of the scene; NpcId is its NpcInfo.NPCType (1001 = bounty board, ...).</summary>
        public sealed class NpcPlacement
        {
            public int NpcId { get; set; }
            public string Name { get; set; }
            public float[] Position { get; set; }
        }

        public sealed class CollectTarget
        {
            public int ItemId { get; set; }
            public int Count { get; set; } = 1;
        }

        /// <summary>A fixed item a monster drops (LootDrop.UniqueItemsPrefab), with its chance in percent.</summary>
        public sealed class UniqueDrop
        {
            public int ItemId { get; set; }
            public int PrefabId { get; set; }
            public int Chance { get; set; } = 100;
        }

        /// <summary>
        /// A named unique item from uniqueItem.rare - the catalog the original service sold uniques
        /// from (its KarmaPrice/CrystalPrice/Visible columns). The row's ID (1080, 9000, ...) is a
        /// catalog number, not an item: the client resolves items by their real id, which is the
        /// TypeID column (see MailService's Kickstarter rewards for the same finding).
        /// </summary>
        public sealed class UniqueShopRow
        {
            public int Id { get; set; }
            public int BaseId { get; set; }
            public int PrefabId { get; set; }
            public int Gem { get; set; }
            public int Element { get; set; }
            public int Enchant { get; set; }
            public int RequiredLevel { get; set; }
            public bool Visible { get; set; }
        }

        /// <summary>A monster type, read from its prefab by tools/SceneExtract.</summary>
        public sealed class MonsterRow
        {
            public int TypeId { get; set; }
            public string Name { get; set; } = "";
            public string Prefab { get; set; } = "";
            public int MaxHealth { get; set; }
            public int Level { get; set; }
            public int LootLevel { get; set; }
            public int Xp { get; set; }
            public int KarmaMin { get; set; }
            public int KarmaMax { get; set; }
            public bool IsBoss { get; set; }
            public bool IsWanted { get; set; }

            /// <summary>Wake-up animation incl. AdditinalResetTime (0 = unknown), and the same per entrance animation.</summary>
            public float WakeUpSeconds { get; set; }
            public List<float> SpawnSeconds { get; set; } = new List<float>();
            public int WantedXp { get; set; }
            public int WantedKarma { get; set; }
            public float VisionRadius { get; set; }
            public float AlertDistance { get; set; }
            public float WanderSpeed { get; set; }
            public int InitState { get; set; }
            public float AttackSpeed { get; set; }
            public float AttackDamage { get; set; }
            public float AttackRange { get; set; }

            /// <summary>The toss ability of a thrower (NPC_Toss, e.g. 30006), 0 = fights in melee.</summary>
            public int ThrowAbility { get; set; }

            /// <summary>When the ball leaves the hand (cast clip event, 0 = unknown), the cast clip's length, and the ball's speed.</summary>
            public float ThrowRelease { get; set; }
            public float ThrowCastLength { get; set; }
            public float ThrowSpeed { get; set; }

            /// <summary>The monster's attack variants (animation, cadence, damage), picked by chance.</summary>
            public List<MonsterAttack> Attacks { get; set; } = new List<MonsterAttack>();

            // ---- loot (the monster's LootDrop component) ----
            public bool CanDropLoot { get; set; }
            public List<DropRoll> EquipmentDrops { get; set; } = new List<DropRoll>();
            public List<DropRoll> ConsumableDrops { get; set; } = new List<DropRoll>();
            public List<MaterialDrop> Materials { get; set; } = new List<MaterialDrop>();
            public List<UniqueDrop> UniqueDrops { get; set; } = new List<UniqueDrop>();
        }

        /// <summary>LootDrop.MaterialDropList: a crafting material with its count and chance in percent.</summary>
        public sealed class MaterialDrop
        {
            public int ItemId { get; set; }
            public int PrefabId { get; set; }
            public int Count { get; set; } = 1;
            public int Chance { get; set; } = 100;
        }

        /// <summary>The LootDrop component of an object (chest, basket, ...).</summary>
        public sealed class LootTable
        {
            public bool CanDropLoot { get; set; }
            public List<DropRoll> EquipmentDrops { get; set; } = new List<DropRoll>();
            public List<DropRoll> ConsumableDrops { get; set; } = new List<DropRoll>();
            public List<MaterialDrop> Materials { get; set; } = new List<MaterialDrop>();
            public List<UniqueDrop> UniqueDrops { get; set; } = new List<UniqueDrop>();
        }

        /// <summary>InteractiveObj.DeathResults: type 0 = a buff (Id) for the player, with a chance of 0..1.</summary>
        public sealed class DeathResultInfo
        {
            public int Type { get; set; }
            public int Id { get; set; }
            public float Chance { get; set; } = 1f;
        }

        /// <summary>"Amount items with weight Chance" - one entry of a monster's loot roll.</summary>
        public sealed class DropRoll
        {
            public int Amount { get; set; }
            public int Chance { get; set; }
        }

        public sealed class MonsterAttack
        {
            public int Chance { get; set; }
            public int AnimIndex { get; set; }
            public float ResetTime { get; set; }
            public float Damage { get; set; }
            public int HitChance { get; set; } = 100;
            public float MinDistance { get; set; }
            public float MaxDistance { get; set; }
        }

        public sealed class SpawnGroup
        {
            public string MonsterPrefab { get; set; } = "";

            /// <summary>
            /// All monster prefabs of the group (NpcSpawner.Spawner.NpcPrefabArray): with
            /// <see cref="RandomPrefab"/> each monster picks one at random, otherwise they take turns.
            /// </summary>
            public List<string> MonsterPrefabs { get; set; } = new List<string>();
            public bool RandomPrefab { get; set; }
            public int Count { get; set; }
            public float SpawnDelay { get; set; }
            public float DelayPerNpc { get; set; }
            public int RepeatCount { get; set; }
            public int InitialState { get; set; }
            public List<float[]> SpawnPositions { get; set; } = new List<float[]>();

            /// <summary>Which of the monster's entrance animations plays in the Spawn state.</summary>
            public int SpawnAnimIndex { get; set; }

            // ---- where the monsters of a group appear (NpcSpawner.Spawner) ----

            /// <summary>Radius around a spawn location (circle) ...</summary>
            public float SpawnArea { get; set; }
            public bool Circle { get; set; } = true;

            /// <summary>... or a rectangle of this size, turned by SpawnRotation degrees.</summary>
            public float SpawnWidth { get; set; }
            public float SpawnHeight { get; set; }
            public float SpawnRotation { get; set; }

            /// <summary>Where the monsters go first; for an ambush the place its entrance jump lands.</summary>
            public List<float[]> Waypoints { get; set; } = new List<float[]>();
        }

        public sealed class SpawnerInfo
        {
            /// <summary>The spawner's own id, which triggers refer to.</summary>
            public int Id { get; set; }

            public string Name { get; set; } = "";
            public float[] Position { get; set; }

            /// <summary>
            /// Mission threat levels this spawner takes part in, as the bit mask of NpcSpawner.DoExport:
            /// bit n (1..4) = threat level n, the last digit of the mission id.
            /// </summary>
            public int Difficulty { get; set; } = 0x1F;

            public bool SpawnsAtThreat(int threat) => (Difficulty & (1 << threat)) != 0;

            public List<SpawnGroup> Groups { get; set; } = new List<SpawnGroup>();

            /// <summary>An interactive object (basket, chest, switch) brings these monsters when it is used.</summary>
            public bool EnabledByObject { get; set; }

            /// <summary>LinkedEvents.LoopingSpawner: once all its monsters are dead, it sends them again
            /// (LoopCount times; 0 = not again).</summary>
            public bool Looping { get; set; }
            public int LoopCount { get; set; }

            /// <summary>What the spawner sets off once enough of its monsters are dead.</summary>
            public List<SpawnerLink> Links { get; set; } = new List<SpawnerLink>();
        }

        /// <summary>One row of the 2013 bounty panel's threat pricing: the karma-shard price
        /// shown on its raise (+) and lower (-) buttons for that mission row.</summary>
        public sealed class ThreatCostRow
        {
            public int ThreatUp { get; set; }
            public int ThreatDown { get; set; }
        }

        /// <summary>
        /// An interactive object of a scene (InteractiveHandler / InteractiveObj): the client builds it
        /// from its own prefab by TypeId once the server announces it; hitting it until its health is
        /// gone breaks it, which can drop karma and bring monsters (Links).
        /// </summary>
        public sealed class BreakableInfo
        {
            public string Name { get; set; } = "";
            public string Script { get; set; } = "";
            public int TypeId { get; set; }
            public float[] Position { get; set; }
            public float[] Rotation { get; set; }
            public float[] Scale { get; set; }
            public int Health { get; set; } = 1;
            public int Exp { get; set; }
            public int LootLevel { get; set; }
            public int KarmaMin { get; set; }
            public int KarmaMax { get; set; }
            public int AppearChance { get; set; } = 100;
            public bool DestroyOnDeath { get; set; }
            public bool Looping { get; set; }
            public int LoopCount { get; set; }

            /// <summary>Spawners it switches on or off when it breaks (KillCount unused, Delay in seconds).</summary>
            public List<SpawnerLink> Links { get; set; } = new List<SpawnerLink>();

            /// <summary>What breaking it plays in the level (AnimDataArrays): a forest switch lowers its bridge.</summary>
            public List<TriggerResult> Animations { get; set; } = new List<TriggerResult>();

            /// <summary>What it holds (chests), null = nothing.</summary>
            public LootTable Loot { get; set; }

            public List<DeathResultInfo> DeathResults { get; set; } = new List<DeathResultInfo>();
        }

        /// <summary>NpcSpawner.LinkedEvents: after KillCount kills (and Delay seconds), act on another spawner or trigger.</summary>
        public sealed class SpawnerLink
        {
            public const string EnableSpawner = "EnableSpawner";
            public const string DisableSpawner = "DisableSpawner";
            public const string AutoTrigger = "AutoTrigger";
            public const string ToggleTrigger = "ToggleTrigger";
            public const string DisableTrigger = "DisableTrigger";

            public string Kind { get; set; } = "";
            public int KillCount { get; set; }
            public float Delay { get; set; }

            /// <summary>Spawner id or trigger id, depending on <see cref="Kind"/>.</summary>
            public int TargetId { get; set; }
        }

        public IReadOnlyList<LevelRow> Levels { get; }
        public IReadOnlyDictionary<string, SceneInfo> Scenes { get; }

        /// <summary>The three abilities a character starts with, by discipline (character "style").</summary>
        public IReadOnlyDictionary<int, IReadOnlyList<int>> StartAbilities { get; }

        /// <summary>One row of MissionArea.Area: a whole mission region and what it costs to open.</summary>
        public sealed class AreaRow
        {
            public int AreaId;
            public bool IsFree;
            public int BuyKarma;
            public int LevelCap;
        }

        /// <summary>Mission regions by area id (5100 = act 1 ... 5800 = act 8, 5000 = tutorial).</summary>
        public IReadOnlyDictionary<int, AreaRow> MissionAreas { get; }

        /// <summary>All abilities by id (client table Ability.Ability).</summary>
        public IReadOnlyDictionary<int, AbilityRow> Abilities { get; }

        /// <summary>All weapons by item id (client table WeaponAttribute.weapon).</summary>
        public IReadOnlyDictionary<int, WeaponRow> Weapons { get; }

        /// <summary>Damage modifier per item level (client table WeaponAttribute.stat).</summary>
        public IReadOnlyDictionary<int, float> WeaponDamageModifier { get; }

        /// <summary>Monster types by their client TypeID (the npcID sent in monsterEnter).</summary>
        public IReadOnlyDictionary<int, MonsterRow> Monsters { get; }

        /// <summary>Monster spawners per scene.</summary>
        public IReadOnlyDictionary<string, List<SpawnerInfo>> Spawners { get; }

        /// <summary>Trigger volumes per scene (doors, barriers, ambushes).</summary>
        public IReadOnlyDictionary<string, List<TriggerInfo>> Triggers { get; }

        /// <summary>The 2013 bounty panel's threat +/- pricing (buyThreatLevelByKarma), keyed by
        /// the target threat level: the panel tips say "Spend Karma Shards", but the amounts were
        /// 2013-server-side data (no mission components exist in the April bundles), so this
        /// ladder - doubling like the region karma prices - is the server's own. Empty for other
        /// eras, which keep threat switches free.</summary>
        public IReadOnlyDictionary<int, ThreatCostRow> ThreatCosts { get; }

        /// <summary>Whether this era's own bundle really carries the crafting recipe table. The
        /// 2014 client does; the April 2013 cut's bundle does not - its crafting UI was cut (the
        /// protocol constants survive, no client code sends them) and the era's client never got
        /// icon bundles for the enchantment materials (6xxx) either: the registry entry resolves
        /// to a bundle that was not shipped, so those items show a placeholder in the bag. A
        /// client like that should be handed materials it can actually display and that it has a
        /// use for - gems and element cores carry their own icon bundles.</summary>
        public bool EraHasCrafting { get; }

        /// <summary>Baskets, chests, switches per scene (InteractiveObj), placed by the server.</summary>
        public IReadOnlyDictionary<string, List<BreakableInfo>> Breakables { get; }

        /// <summary>Karma cost of the 1st..7th revival of a mission, per character level.</summary>
        public IReadOnlyDictionary<int, int[]> RevivalCosts { get; }

        /// <summary>
        /// The revival ladder of the January 2013 (Chinese) client: its death panel prices
        /// through its bundled RevivalCost.Costs table, so the charge must follow that ladder.
        /// Eight columns: index 0 is the level column itself (its GetRevivalKarma returns
        /// array2[revivals], and that counter is still 0 at the first death), 1..7 are the
        /// FirstRevive..SeventhRevive columns. The bundle is not vendored, so the table lives
        /// as zh2013-revivalcosts.json in the GameData folder.
        /// </summary>
        public IReadOnlyDictionary<int, int[]> RevivalCostsZh2013 { get; }

        /// <summary>MissionsThreatCycle.CoolDown: minutes a map rests after its n-th run within a day.</summary>
        public IReadOnlyList<int> MissionCooldownMinutes { get; }

        /// <summary>Mission definitions by id (from the mission prefabs).</summary>
        public IReadOnlyDictionary<int, MissionDefinition> Missions { get; }

        // ---- loot tables -----------------------------------------------------------------------

        /// <summary>Weapons that can drop, with their chances (WeaponAttribute.weapon).</summary>
        public IReadOnlyList<LootItemRow> WeaponDrops { get; }

        /// <summary>Armor pieces that can drop (ArmorAttribute.Armor): head, chest, legs and cloak.</summary>
        public IReadOnlyList<LootItemRow> ArmorDrops { get; }

        /// <summary>Accessories that can drop (AccessoriesAttribute.Accessory).</summary>
        public IReadOnlyList<LootItemRow> AccessoryDrops { get; }

        public IReadOnlyList<ItemPrefabRow> WeaponPrefabs { get; }
        public IReadOnlyList<ItemPrefabRow> ArmorPrefabs { get; }
        public IReadOnlyList<ItemPrefabRow> AccessoryPrefabs { get; }

        /// <summary>Consumables (food, potions) by id, with the level they are meant for.</summary>
        public IReadOnlyList<LootItemRow> Consumables { get; }

        /// <summary>
        /// Enchantments, elements and gems that can come on dropped equipment, per kind
        /// (0 weapon, 1 armor, 2 accessory): which of the three by item level (*.stat), then which
        /// one by its attach chance and level range (*.Enchant, *.Element, WeaponAttribute.Gem).
        /// </summary>
        public IReadOnlyList<EquipmentAttributes> LootAttributes { get; }

        public sealed class EquipmentAttributes
        {
            public Dictionary<int, (int None, int Enchant, int Gem, int Element)> ChancesByLevel { get; init; } = new();
            public List<AttributeRow> Enchants { get; init; } = new();
            public List<AttributeRow> Elements { get; init; } = new();
            public List<AttributeRow> Gems { get; init; } = new();
        }

        /// <summary>One enchantment, element or gem: its id (what the item stores), chance and level range.</summary>
        public sealed class AttributeRow
        {
            public int Id { get; init; }
            public int Chance { get; init; }
            public int MinLevel { get; init; }
            public int MaxLevel { get; init; }
        }

        /// <summary>Equipment slot an item belongs into (Props.General EquipPoint), by item id.</summary>
        public IReadOnlyDictionary<int, int> ItemEquipSlots { get; }

        /// <summary>Character level an item requires (Props.General LevelRequirement), by item id.</summary>
        public IReadOnlyDictionary<int, int> ItemLevelRequirements { get; }

        /// <summary>Base worth of weapons, armor and accessories (their tables' "Value"), by item id.</summary>
        public IReadOnlyDictionary<int, float> ItemValues { get; }

        /// <summary>Base armor of armor pieces and accessories (their tables' "Amount", ItemDeployInfo.minDef), by item id.</summary>
        public IReadOnlyDictionary<int, float> ItemArmor { get; }

        /// <summary>Armor per item level: ArmorAttribute.Stat / AccessoriesAttribute.Stat; the tooltip shows minDef * modifier.</summary>
        public IReadOnlyDictionary<int, float> ArmorModifier { get; }
        public IReadOnlyDictionary<int, float> AccessoryModifier { get; }

        /// <summary>Every item the client knows (Props.General), by item id.</summary>
        public IReadOnlyDictionary<int, ItemProps> Items { get; }

        /// <summary>The whole unique catalog (uniqueItem.rare), by catalog id.</summary>
        public IReadOnlyDictionary<int, UniqueShopRow> UniqueShop { get; }

        /// <summary>
        /// Named special items (Props 4001-4062: pledged and event gear like Razzle Dazzle and
        /// Amaterasu's Sword). They are not in the rare catalog but the client knows them fully.
        /// </summary>
        public IReadOnlyDictionary<int, ItemProps> SpecialItems { get; }

        /// <summary>
        /// Fills in the PrefabId an item has to carry for its Props.General type, and returns
        /// whether the item changed. Equipment uses a model-variant id from its own table; every
        /// other type uses the client's index into the item's ItemPrefabs entry, and index 0 is the
        /// world model, which has no icon texture - such items render blank in bag and mailbox
        /// (ItemPrefabs.GetItemIcon logs "Item download err") and quest items with 0 do not even
        /// count for the mission that asks for them (see ItemType.IsModelIndex).
        /// </summary>
        public bool EnsurePrefab(Data.ItemData item)
        {
            if (item == null || item.PrefabId != 0)
                return false;
            if (!Items.TryGetValue(item.ItemId, out ItemProps props) || !ItemType.IsModelIndex(props.Type))
                return false;
            item.PrefabId = 1;
            return true;
        }

        /// <summary>
        /// Fills in the PrefabId of every item of an account - bag, stash, equipment and mail
        /// attachments - so characters saved before the index was set still get their icon.
        /// Returns whether anything changed; the caller saves in that case.
        /// </summary>
        public bool NormalizePrefabIds(Data.AccountData account)
        {
            if (account == null)
                return false;
            return account.Characters
                .SelectMany(c => c.Bag.Concat(c.Stash).Concat(c.Equipment).Concat(c.Mail.SelectMany(m => m.Items)))
                .Count(EnsurePrefab) > 0; // Count, not Any: every item has to be looked at.
        }

        /// <summary>What a consumable does when used (ConsumableItem.General), by item id.</summary>
        public IReadOnlyDictionary<int, ConsumableRow> ConsumableEffects { get; }

        /// <summary>Buffs and status effects (Ability.Status), by id.</summary>
        public IReadOnlyDictionary<int, BuffRow> Buffs { get; }

        /// <summary>Upgrade recipes of the craftsman (UpgradeRecipes.Recipes), by (type, attribute id).</summary>
        public IReadOnlyDictionary<(int Type, int AttrId), RecipeRow> Recipes { get; }

        public sealed class RecipeRow
        {
            /// <summary>ECraftType: 1/3/5 element, 2/4/6 enchant (weapon/armor/accessory), 7 gem, 8 level.</summary>
            public int Type { get; init; }

            /// <summary>The attribute the item gets (element/enchant/gem id, or the new item level).</summary>
            public int AttrId { get; init; }
            public string Name { get; init; } = "";
            public List<(int ItemId, int Count)> Materials { get; init; } = new();
            public int KarmaPrice { get; init; }
            public int KarmaChance { get; init; }
            public int CrystalPrice { get; init; }
            public int CrystalChance { get; init; }
        }

        /// <summary>The daily login rewards, day 1, 2, ... (DayReward.reward).</summary>
        public IReadOnlyList<DayRewardRow> DayRewards { get; }

        /// <summary>All four gift tables (DayReward.reward .. reward4). The 2013 client's daily
        /// window shows one track at a time and picks it via SelectDayRewardNTF; those servers
        /// handed out every track's gift for the day. DayRewards is the first track.</summary>
        public IReadOnlyList<IReadOnlyList<DayRewardRow>> DayRewardTracks { get; }

        public sealed class DayRewardRow
        {
            public int Day { get; init; }

            /// <summary>1 = karma, 2 = crystals, 4 = a consumable, 5 = a piece of equipment.</summary>
            public int Type { get; init; }
            public int Count { get; init; }
            public Data.ItemData Item { get; init; }
            public string Name { get; init; } = "";
        }

        /// <summary>Spirit helpers and what renting them costs (PetPrice.Price), by id (type * 1000 + level).</summary>
        public IReadOnlyDictionary<int, SpiritRow> Spirits { get; }

        public sealed class SpiritRow
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int Level { get; init; }
            public int LevelRequired { get; init; }

            /// <summary>What the spirit does at this level ("Dog spirit. Increases Power 2%").</summary>
            public string Effect { get; init; } = "";

            /// <summary>Experience the spirit needs in total to reach this level.</summary>
            public ulong XpNeeded { get; init; }

            /// <summary>Price and "is crystal" for one hour, one day and one week.</summary>
            public (int Price, bool Crystal) Hour { get; init; }
            public (int Price, bool Crystal) Day { get; init; }
            public (int Price, bool Crystal) Week { get; init; }
        }

        /// <summary>Every level of every weapon and armor mastery (EquipMastery.level), by id.</summary>
        public IReadOnlyDictionary<int, MasteryRow> Masteries { get; }

        public sealed class MasteryRow
        {
            public int Id { get; init; }

            /// <summary>EMasteryType: 1 = one-handed, 2 = dual, 4 = two-handed, 8/16/32 = light/medium/heavy armor.</summary>
            public int Type { get; init; }
            public int Level { get; init; }
            public int KarmaCost { get; init; }

            /// <summary>POW, DEF or SKILL (by <see cref="Discipline"/>) needed to train it.</summary>
            public int RequiredStat { get; init; }
            public int Discipline { get; init; }
            public int TrainSeconds { get; init; }
            public string Name { get; init; } = "";
        }

        /// <summary>
        /// Item types of Props.General "Type" as the client's inventory uses them
        /// (InventoryCtrl.GetSlotFromType): armor, accessories, weapons, quest items,
        /// materials and consumables.
        /// </summary>
        public static class ItemType
        {
            public const int Head = 1;
            public const int Neck = 2;
            public const int Chest = 3;
            public const int Cloak = 4;
            public const int Ring = 5;
            public const int Legs = 6;
            public const int OneHanded = 7;
            public const int TwoHanded = 8;
            public const int QuestItem = 9;
            public const int Gem = 11;
            public const int ElementCore = 12;
            public const int Material = 13;
            public const int Consumable = 14;

            /// <summary>
            /// Whether an item's PrefabId is a model-variant id (equipment, looked up in
            /// Armor/Weapon/AccessoriesAttribute.Prefab) or the client's icon/model index inside
            /// its ItemPrefabs registry entry. Equipment index 0 is the world model, which carries
            /// no icon texture (ItemPrefabs.GetItemIcon logs "Item download err" and falls back to
            /// defaultImg); everything else - food, potions, scrolls, quest items, crafting
            /// materials - is shown from index 1, exactly as the client itself builds it in the
            /// consumable shop (_UI_CS_Consumable.InitItemList), the crafting panel
            /// (CraftingDetailPanel), the material lists and the daily reward screen
            /// (GetItemObject(id, 1, 0, 0, 0, 1)).
            /// </summary>
            public static bool IsModelIndex(int itemType) => itemType >= QuestItem;
        }

        public sealed class ItemProps
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int Type { get; init; }
            public int SecondType { get; init; }
            public int EquipPoint { get; init; }
            public int LevelRequirement { get; init; }
            public int SellPrice { get; init; }

            /// <summary>How many fit into one bag slot (Accumulation); 1 for gear.</summary>
            public int StackSize { get; init; } = 1;

            /// <summary>Sold for crystals (the real-money currency) instead of karma.</summary>
            public bool UsesRealMoney { get; init; }
        }

        public sealed class ConsumableRow
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public int StatusEffect { get; init; }
            public int BuffId { get; init; }
        }

        /// <summary>
        /// What the enchantments, elements and gems stored on items do: enchantment and element ids
        /// lead to a status (Ability.Status), whose description says the effect; gems add discipline
        /// points (GemTelent: 1 power, 2 defense, 3 power+defense, 4 skill, 5 skill+power,
        /// 6 defense+skill, 7 all, 16 karma shard value).
        /// </summary>
        public IReadOnlyDictionary<(int Kind, int Id), int> EnchantStatus { get; private set; } = new Dictionary<(int, int), int>();
        public IReadOnlyDictionary<(int Kind, int Id), int> ElementStatus { get; private set; } = new Dictionary<(int, int), int>();
        public IReadOnlyDictionary<int, (int Talent, float Value)> Gems { get; private set; } = new Dictionary<int, (int, float)>();

        /// <summary>The bonus numbers the client multiplies an item's worth by (tooltip value and
        /// transmute formula): enchantments are column 5 of each kind's Enchant table; elements sit
        /// at a per-kind column (weapon 8, armor 9, accessory 7); the gem effect the client calls
        /// gemEffectVal is column 6 of the gem table. Keys are (kind, id) in loot kind order:
        /// 0 weapon, 1 armor, 2 accessory.</summary>
        public IReadOnlyDictionary<(int Kind, int Id), float> EnchantValues { get; private set; } = new Dictionary<(int, int), float>();
        public IReadOnlyDictionary<(int Kind, int Id), float> ElementValues { get; private set; } = new Dictionary<(int, int), float>();
        public IReadOnlyDictionary<int, float> GemEffectValues { get; private set; } = new Dictionary<int, float>();

        /// <summary>What each mastery level does (EquipMastery.level, second Description column).</summary>
        public IReadOnlyDictionary<int, string> MasteryEffects { get; private set; } = new Dictionary<int, string>();

        public sealed class BuffRow
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";

            /// <summary>Milliseconds.</summary>
            public int Duration { get; init; }
            public string Description { get; init; } = "";
        }

        /// <summary>Karma cost of the next revival (the client shows the same value).</summary>
        public int RevivalCost(int characterLevel, int revivalsSoFar)
        {
            if (!RevivalCosts.TryGetValue(characterLevel, out int[] costs) || costs.Length == 0)
                return 0;
            return costs[Math.Clamp(revivalsSoFar, 0, costs.Length - 1)];
        }

        /// <summary>Same for the January ladder; levels past its table revive for free.</summary>
        public int RevivalCostZh2013(int characterLevel, int revivalsSoFar)
        {
            if (!RevivalCostsZh2013.TryGetValue(characterLevel, out int[] costs) || costs.Length == 0)
                return 0;
            return costs[Math.Clamp(revivalsSoFar, 0, costs.Length - 1)];
        }

        public GameData(string clientAssetsDir, string dataDir, string fallbackTables = null, string fallbackData = null)
        {
            // An older client era's content is mostly the current content: wherever that era's
            // bundle never had a table (the April 2013 cut has no crafting recipes, no threat
            // cycle, no stash) or the era's scene JSON folder does not exist yet, the modern
            // file is read instead. A fork therefore only carries what actually changed.
            string Pick(string dir, string fallback, string name) =>
                fallback == null || File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : Path.Combine(fallback, name);
            List<Row> ReadTable(string path) =>
                GameData.ReadTable(File.Exists(path) || fallbackTables == null
                    ? path : Path.Combine(fallbackTables, Path.GetFileName(path)));
            string Json(string name) => File.ReadAllText(Pick(dataDir, fallbackData, name));

            string sceneFile = Json("scenes.json");
            string tables = Path.Combine(clientAssetsDir, "Resources", "Localization", "EN");
            Levels = ReadTable(Path.Combine(tables, "Level.level.EN.txt"))
                .Select(r => new LevelRow
                {
                    Level = r.Int("Level"),
                    ExpTotal = r.Long("Exp"),
                    MaxHp = r.Int("MaxHP"),
                    MaxMp = r.Int("Max MP"),
                })
                .OrderBy(r => r.Level)
                .ToList();

            Scenes = JsonSerializer.Deserialize<Dictionary<string, SceneInfo>>(sceneFile);

            // The character creation screen lists each discipline's starting abilities. The April
            // 2013 bundle's CreateCharaInfo table carries only descriptions - no Name/Ability
            // columns - but its Ability table holds the same id family (10501, 11101, 11401 are
            // all rows), so the modern table seeds the starts and the era's own table overwrites
            // them wherever it carries the columns.
            var starts = new Dictionary<int, IReadOnlyList<int>>();
            foreach (string dir in new[] { fallbackTables, tables })
            {
                if (dir == null)
                    continue;
                foreach (Row row in ReadTable(Path.Combine(dir, "CreateCharaInfo.Description.EN.txt")))
                {
                    int style = DisciplineStyle(row.Str("Name"));
                    if (style != 0)
                        starts[style] = new[] { row.Int("Ability1"), row.Int("Ability2"), row.Int("Ability3") };
                }
            }
            StartAbilities = starts;

            // MissionArea.Area: the 2013-era clients gate whole regions behind karma purchases
            // ("Jawbone Ravine - Unlock 50 shards"). The 2014 client's copy of the table is empty
            // (that progression runs on mission chains), so the dictionary simply stays empty there.
            MissionAreas = ReadTable(Path.Combine(tables, "MissionArea.Area.EN.txt"))
                .Where(r => r.Int("AreaID") > 0)
                .Select(r => new AreaRow
                {
                    AreaId = r.Int("AreaID"),
                    IsFree = r.Int("IsFree") != 0,
                    BuyKarma = r.Int("Buykarma"),
                    LevelCap = r.Int("UnlockPlayerMaxLvl"),
                })
                .ToDictionary(a => a.AreaId);

            Abilities = ReadTable(Path.Combine(tables, "Ability.Ability.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => new AbilityRow
                {
                    Id = g.Key,
                    Name = g.First().Str("Name"),
                    MpNeed = g.First().Int("MPNeed"),
                    CooldownMs = g.First().Int("Cooldown"),
                    Discipline = g.First().Int("Discipline"),
                    BaseVal = g.First().Float("BaseVal"),
                    AbilityBonus = g.First().Float("AbilityBonus"),
                    AttackCoefficient = g.First().Float("AttackCoefficient"),
                    AttrType = g.First().Int("AttrType"),
                    AreaType = g.First().Int("AreaType"),
                    AreaRadius = Math.Max(g.First().Float("AreaWidth"), g.First().Float("AreaLength")),
                    TargetType = g.First().Int("TargetType"),
                    AttackPosType = g.First().Int("AttackPosType"),
                    AttackCount = Math.Max(1, g.First().Int("AttackCount")),
                    ObjectId = g.First().Int("ObjectID"),
                    AnimDurationMs = g.First().Int("AnimDuration"),
                    RequiredStat = g.First().Int("ReqBaseStatAmount"),
                    KarmaCost = g.First().Int("Karma"),
                    TrainingSeconds = g.First().Float("TrainingTime"),
                });

            Weapons = ReadTable(Path.Combine(tables, "WeaponAttribute.weapon.EN.txt"))
                .Where(r => r.Int("WeaponID") != 0)
                .GroupBy(r => r.Int("WeaponID"))
                .ToDictionary(g => g.Key, g => new WeaponRow
                {
                    Id = g.Key,
                    Name = g.First().Str("Weapon"),
                    WeaponClass = g.First().Str("WeaponClass"),
                    DamageMin = g.First().Int("DamageRange"),
                    DamageMax = g.First().Int("Damage Range"),
                    AttackRate = g.First().Float("AttackRate"),
                    AttackRange = g.First().Float("AttackRange"),
                    AttackAngle = g.First().Int("AttackAngle"),
                    SwingFactor = g.First().Float("Attack Speed") > 0 ? g.First().Float("Attack Speed") : 1f,
                    DropChance = g.First().Int("DropChance"), // ReadTable trims the header ("DropChance ")
                });

            WeaponDrops = Weapons.Values
                .Where(w => w.DropChance > 0)
                .Select(w => new LootItemRow { Id = w.Id, Name = w.Name, DropChance = w.DropChance })
                .ToList();

            ArmorDrops = ReadTable(Path.Combine(tables, "ArmorAttribute.Armor.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .Select(r => new LootItemRow { Id = r.Int("ID"), Name = r.Str("Weapon"), DropChance = r.Int("Chance") })
                .ToList();

            AccessoryDrops = ReadTable(Path.Combine(tables, "AccessoriesAttribute.Accessory.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .Select(r => new LootItemRow { Id = r.Int("ID"), Name = r.Str("Weapon"), DropChance = r.Int("Chance") })
                .ToList();

            List<AttributeRow> Attributes(string file) => ReadTable(Path.Combine(tables, file))
                .Where(r => r.Int("ID") != 0)
                .Select(r => new AttributeRow
                {
                    Id = r.Int("ID"),
                    Chance = r.Str("AttachChance") != "" ? r.Int("AttachChance") : r.Int("Chance"),
                    MinLevel = r.Int("Min"),
                    MaxLevel = r.Int("Max"),
                })
                .ToList();
            Dictionary<int, (int, int, int, int)> Chances(string file) => ReadTable(Path.Combine(tables, file))
                .Where(r => r.Int("Level") != 0)
                .GroupBy(r => r.Int("Level"))
                .ToDictionary(g => g.Key, g => (g.First().Int("ChanceNoAttribute"), g.First().Int("Enchant"), g.First().Int("Colour"), g.First().Int("Element")));
            List<AttributeRow> gems = Attributes("WeaponAttribute.Gem.EN.txt");
            LootAttributes = new[]
            {
                new EquipmentAttributes { ChancesByLevel = Chances("WeaponAttribute.stat.EN.txt"), Enchants = Attributes("WeaponAttribute.Enchant.EN.txt"), Elements = Attributes("WeaponAttribute.Element.EN.txt"), Gems = gems },
                new EquipmentAttributes { ChancesByLevel = Chances("ArmorAttribute.Stat.EN.txt"), Enchants = Attributes("ArmorAttribute.Enchant.EN.txt"), Elements = Attributes("ArmorAttribute.Element.EN.txt"), Gems = gems },
                new EquipmentAttributes { ChancesByLevel = Chances("AccessoriesAttribute.Stat.EN.txt"), Enchants = Attributes("AccessoriesAttribute.Enchant.EN.txt"), Elements = Attributes("AccessoriesAttribute.Element.EN.txt"), Gems = gems },
            };

            // The "Value" column is the base worth of a piece of equipment (ItemDeployInfo._ItemVal).
            ItemValues = new[] { "WeaponAttribute.weapon.EN.txt", "ArmorAttribute.Armor.EN.txt", "AccessoriesAttribute.Accessory.EN.txt" }
                .SelectMany(file => ReadTable(Path.Combine(tables, file)))
                .Select(r => (Id: r.Int("WeaponID") != 0 ? r.Int("WeaponID") : r.Int("ID"), Value: r.Float("Value")))
                .Where(v => v.Id != 0)
                .GroupBy(v => v.Id)
                .ToDictionary(g => g.Key, g => g.First().Value);

            ItemArmor = new[] { "ArmorAttribute.Armor.EN.txt", "AccessoriesAttribute.Accessory.EN.txt" }
                .SelectMany(file => ReadTable(Path.Combine(tables, file)))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => g.First().Float("Amount"));
            ArmorModifier = ReadTable(Path.Combine(tables, "ArmorAttribute.Stat.EN.txt"))
                .Where(r => r.Int("Level") != 0)
                .GroupBy(r => r.Int("Level"))
                .ToDictionary(g => g.Key, g => g.First().Float("ArmorModifier"));
            AccessoryModifier = ReadTable(Path.Combine(tables, "AccessoriesAttribute.Stat.EN.txt"))
                .Where(r => r.Int("Level") != 0)
                .GroupBy(r => r.Int("Level"))
                .ToDictionary(g => g.Key, g => g.First().Float("DamageModifier"));

            WeaponPrefabs = ReadPrefabs(Path.Combine(tables, "WeaponAttribute.prefab.EN.txt"));
            ArmorPrefabs = ReadPrefabs(Path.Combine(tables, "ArmorAttribute.Prefab.EN.txt"));
            AccessoryPrefabs = ReadPrefabs(Path.Combine(tables, "AccessoriesAttribute.Prefab.EN.txt"));

            Consumables = ReadTable(Path.Combine(tables, "ConsumableItem.General.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .Select(r => new LootItemRow { Id = r.Int("ID"), Name = r.Str("Name"), DropChance = 1 })
                .ToList();

            List<Row> props = ReadTable(Path.Combine(tables, "Props.General.EN.txt")).Where(r => r.Int("ID") != 0).ToList();
            ItemEquipSlots = props.GroupBy(r => r.Int("ID")).ToDictionary(g => g.Key, g => g.First().Int("EquipPoint"));
            ItemLevelRequirements = props.GroupBy(r => r.Int("ID")).ToDictionary(g => g.Key, g => g.First().Int("LevelRequirement"));
            Items = props.GroupBy(r => r.Int("ID")).ToDictionary(g => g.Key, g => new ItemProps
            {
                Id = g.Key,
                Name = g.First().Str("Name"),
                Type = g.First().Int("Type"),
                SecondType = g.First().Int("SecondType"),
                EquipPoint = g.First().Int("EquipPoint"),
                LevelRequirement = g.First().Int("LevelRequirement"),
                SellPrice = g.First().Int("SellPrice"),
                StackSize = Math.Max(1, g.First().Int("Accumulation")),
                UsesRealMoney = g.First().Int("isUseRealMoney") != 0,
            });
            // Named pledge/event gear (Razzle Dazzle, Amaterasu's Sword, ...): not in the rare
            // catalog, but the client knows the rows and renders them in mail and bag.
            SpecialItems = Items.Values.Where(i => i.Id >= 4000 && i.Id < 5000)
                .GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => g.First());
            UniqueShop = ReadTable(Path.Combine(tables, "uniqueItem.rare.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => new UniqueShopRow
                {
                    Id = g.Key,
                    BaseId = g.First().Int("TypeID"),
                    PrefabId = g.First().Int("PrefabID"),
                    Gem = g.First().Int("Gem"),
                    Element = g.First().Int("Element"),
                    Enchant = g.First().Int("Enchant"),
                    RequiredLevel = g.First().Int("Level"),
                    Visible = g.First().Int("Visible") != 0,
                });
            ConsumableEffects = ReadTable(Path.Combine(tables, "ConsumableItem.General.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => new ConsumableRow
                {
                    Id = g.Key,
                    Name = g.First().Str("Name"),
                    StatusEffect = g.First().Int("StatusEffect"),
                    BuffId = g.First().Int("BuffID"),
                });
            // Minutes a mission map rests after the 1st, 2nd, ... run of the day.
            MissionCooldownMinutes = ReadTable(Path.Combine(tables, "MissionsThreatCycle.CoolDown.EN.txt"))
                .Select(r => r.Int("coolDownTime"))
                .Where(m => m > 0)
                .ToList();

            Recipes = ReadTable(Path.Combine(tables, "UpgradeRecipes.Recipes.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => (r.Int("Type"), r.Int("AttrID")))
                .ToDictionary(g => g.Key, g => new RecipeRow
                {
                    Type = g.Key.Item1,
                    AttrId = g.Key.Item2,
                    Name = g.First().Str("AttrName"),
                    Materials = Enumerable.Range(1, 4)
                        .Select(i => (g.First().Int("Material" + i), g.First().Int("Count" + i)))
                        .Where(m => m.Item1 != 0 && m.Item2 > 0)
                        .ToList(),
                    KarmaPrice = g.First().Int("KSPrice"),
                    KarmaChance = g.First().Int("KSChance"),
                    CrystalPrice = g.First().Int("CrPrice"),
                    CrystalChance = g.First().Int("CrChance"),
                });

            DayRewards = DayRows("DayReward.reward.EN.txt");
            DayRewardTracks = new[]
            {
                DayRewards,
                DayRows("DayReward.reward2.EN.txt"),
                DayRows("DayReward.reward3.EN.txt"),
                DayRows("DayReward.reward4.EN.txt"),
            };

            IReadOnlyList<DayRewardRow> DayRows(string file) => ReadTable(Path.Combine(tables, file))
                .Where(r => r.Int("Day") != 0)
                .Select(r => new DayRewardRow
                {
                    Day = r.Int("Day"),
                    Type = r.Int("RewardType"),
                    Count = Math.Max(1, r.Int("Count")),
                    Name = r.Str("Name"),
                    Item = r.Int("TypeID") == 0 ? null : new Data.ItemData
                    {
                        ItemId = r.Int("TypeID"),
                        // A consumable reward carries the client's icon index (DailyRewardScreenCtrl
                        // builds it as GetItemObject(id, 1, 0, 0, 0, 1)); only equipment has a real
                        // model variant in the table.
                        PrefabId = r.Int("RewardType") == 5 ? r.Int("PrefabID") : 1,
                        Gem = r.Int("Gem"),
                        Element = r.Int("Element"),
                        Enchant = r.Int("Enchant"),
                        Level = Math.Max(1, r.Int("Level")),
                        Count = Math.Max(1, r.Int("Count")),
                    },
                })
                .OrderBy(r => r.Day)
                .ToList();

            // ID, IconID+1, Name, Rent, Description, Buff, EffectIdx, LevelReq, IsShopShow, Level, XPNeed,
            // 1Hour, IsCrystal, 1Day, IsCrystal, 1Week, IsCrystal, SHType ("IsCrystal" three times).
            Spirits = ReadCells(Path.Combine(tables, "PetPrice.Price.EN.txt"))
                .Where(c => Cell(c, 0) != 0)
                .GroupBy(c => Cell(c, 0))
                .ToDictionary(g => g.Key, g => new SpiritRow
                {
                    Id = g.Key,
                    Name = g.First()[2],
                    LevelRequired = Cell(g.First(), 7),
                    Effect = g.First().Length > 5 ? g.First()[5].Trim() : "",
                    XpNeeded = (ulong)Math.Max(0, Cell(g.First(), 10)),
                    Level = Cell(g.First(), 9),
                    Hour = (Cell(g.First(), 11), Cell(g.First(), 12) != 0),
                    Day = (Cell(g.First(), 13), Cell(g.First(), 14) != 0),
                    Week = (Cell(g.First(), 15), Cell(g.First(), 16) != 0),
                });

            // EquipClass is the EMasteryType bit (1, 2, 4, 8, 16, 32; ids 1xxx..6xxx);
            // TrainTime is in minutes (MasteryLoader: * 60).
            Masteries = ReadTable(Path.Combine(tables, "EquipMastery.level.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => new MasteryRow
                {
                    Id = g.Key,
                    Type = g.First().Int("EquipClass"),
                    Level = g.First().Int("MasteryLevel"),
                    KarmaCost = g.First().Int("karmaCost"),
                    RequiredStat = g.First().Int("BaseStatLvNeeded"),
                    Discipline = g.First().Int("Discipline"),
                    TrainSeconds = g.First().Int("TrainTime") * 60,
                    Name = g.First().Str("Name"),
                });

            LoadItemEffects(tables);

            Buffs = ReadTable(Path.Combine(tables, "Ability.Status.EN.txt"))
                .Where(r => r.Int("ID") != 0)
                .GroupBy(r => r.Int("ID"))
                .ToDictionary(g => g.Key, g => new BuffRow
                {
                    Id = g.Key,
                    Name = g.First().Str("Name"),
                    Duration = g.First().Int("Duration"),
                    Description = g.First().Str("Des"),
                });

            WeaponDamageModifier = ReadTable(Path.Combine(tables, "WeaponAttribute.stat.EN.txt"))
                .Where(r => r.Int("Level") != 0)
                .GroupBy(r => r.Int("Level"))
                .ToDictionary(g => g.Key, g => g.First().Float("DamageModifier"));

            Monsters = JsonSerializer.Deserialize<List<MonsterRow>>(Json("monsters.json"))
                .ToDictionary(m => m.TypeId);

            Spawners = JsonSerializer.Deserialize<Dictionary<string, List<SpawnerInfo>>>(Json("spawners.json"));

            Triggers = JsonSerializer.Deserialize<Dictionary<string, List<TriggerInfo>>>(Json("triggers.json"));

            // The threat +/- prices of the 2013 bounty panel: the client only carries the tip
            // text ("Spend Karma Shards to raise/lower the threat level"); the amounts were
            // 2013 server-side data, so threatcosts.json is the revival's own ladder.
            string threatCosts = Pick(dataDir, fallbackData, "threatcosts.json");
            ThreatCosts = File.Exists(threatCosts)
                ? JsonSerializer.Deserialize<Dictionary<int, ThreatCostRow>>(File.ReadAllText(threatCosts))
                : new Dictionary<int, ThreatCostRow>();

            // Crafting is era real only when the era's own bundle carries the recipe table; the
            // April 2013 tables dir does not (its UI was cut, and its material icons never
            // shipped), so that era rolls materials the client can actually show.
            EraHasCrafting = File.Exists(Path.Combine(tables, "UpgradeRecipes.Recipes.EN.txt"));

            string breakableFile = Pick(dataDir, fallbackData, "breakables.json");
            Breakables = File.Exists(breakableFile)
                ? JsonSerializer.Deserialize<Dictionary<string, List<BreakableInfo>>>(File.ReadAllText(breakableFile))
                : new Dictionary<string, List<BreakableInfo>>();

            Missions = JsonSerializer.Deserialize<Dictionary<int, MissionDefinition>>(Json("missions.json"),
                new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString });

            RevivalCosts = ReadTable(Path.Combine(tables, "RevivalCost.Costs.EN.txt"))
                .GroupBy(r => r.Int("PlayerLevel"))
                .ToDictionary(g => g.Key, g => new[]
                {
                    g.First().Int("FirstRevive"), g.First().Int("SecondRevive"), g.First().Int("ThirdRevive"),
                    g.First().Int("FourthRevive"), g.First().Int("FifthRevive"), g.First().Int("SixthRevive"),
                    g.First().Int("SeventhRevive"),
                });

            // The January client's death-menu ladder (its bundle is not vendored; the table was
            // read out of its resources.assets and carries the level column first - see
            // RevivalCostsZh2013). Only ZH sessions charge it, everyone else ignores the file.
            string zhRevival = Pick(dataDir, fallbackData, "zh2013-revivalcosts.json");
            RevivalCostsZh2013 = File.Exists(zhRevival)
                ? JsonSerializer.Deserialize<Dictionary<int, int[]>>(File.ReadAllText(zhRevival))
                : new Dictionary<int, int[]>();
        }

        private void LoadItemEffects(string tables)
        {
            // Kinds as in the loot: 0 weapon, 1 armor, 2 accessory.
            var files = new[] { "WeaponAttribute", "ArmorAttribute", "AccessoriesAttribute" };
            var enchants = new Dictionary<(int, int), int>();
            var elements = new Dictionary<(int, int), int>();
            for (int kind = 0; kind < files.Length; kind++)
            {
                foreach (Row r in ReadTable(Path.Combine(tables, files[kind] + ".Enchant.EN.txt")).Where(r => r.Int("ID") != 0))
                    enchants[(kind, r.Int("ID"))] = r.Int("StatusID");
                foreach (Row r in ReadTable(Path.Combine(tables, files[kind] + ".Element.EN.txt")).Where(r => r.Int("ID") != 0))
                {
                    int status = r.Int("SkillID") != 0 ? r.Int("SkillID") : r.Int("StatusID");
                    if (status != 0)
                        elements[(kind, r.Int("ID"))] = status;
                }
            }
            EnchantStatus = enchants;
            ElementStatus = elements;

            // ID Color AttachChance GemProgramType GemTelent EffectBonus Value Value Value ...
            Gems = ReadCells(Path.Combine(tables, "WeaponAttribute.Gem.EN.txt"))
                .Where(c => Cell(c, 0) != 0)
                .GroupBy(c => Cell(c, 0))
                .ToDictionary(g => g.Key, g => (Cell(g.First(), 4), CellFloat(g.First(), 8)));

            // The worth multipliers the client's tooltip and transmute formulas read - same
            // columns, in the same (broken) per-kind layout the client itself parses:
            // enchant col 5 everywhere; element weapon 8 / armor 9 / accessory 7; gem col 6.
            EnchantValues = Enumerable.Range(0, files.Length)
                .SelectMany(kind => ReadCells(Path.Combine(tables, files[kind] + ".Enchant.EN.txt"))
                    .Where(c => Cell(c, 0) != 0)
                    .Select(c => (Key: (kind, Cell(c, 0)), Value: CellFloat(c, 5))))
                .GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            int[] elementColumns = { 8, 9, 7 };
            ElementValues = Enumerable.Range(0, files.Length)
                .SelectMany(kind => ReadCells(Path.Combine(tables, files[kind] + ".Element.EN.txt"))
                    .Where(c => Cell(c, 0) != 0)
                    .Select(c => (Key: (kind, Cell(c, 0)), Value: CellFloat(c, elementColumns[kind]))))
                .GroupBy(p => p.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            GemEffectValues = ReadCells(Path.Combine(tables, "WeaponAttribute.Gem.EN.txt"))
                .Where(c => Cell(c, 0) != 0)
                .GroupBy(c => Cell(c, 0))
                .ToDictionary(g => g.Key, g => CellFloat(g.First(), 6));

            // ID EquipClass MasteryLevel MasteryType karmaCost BaseStatLvNeeded BuffID Name Description Description ...
            MasteryEffects = ReadCells(Path.Combine(tables, "EquipMastery.level.EN.txt"))
                .Where(c => Cell(c, 0) != 0 && c.Length > 9)
                .GroupBy(c => Cell(c, 0))
                .ToDictionary(g => g.Key, g => g.First()[9].Trim());
        }

        private static List<ItemPrefabRow> ReadPrefabs(string path)
        {
            return ReadTable(path)
                .Where(r => r.Int("prefabID") != 0)
                .Select(r => new ItemPrefabRow
                {
                    PrefabId = r.Int("prefabID"),
                    Name = r.Str("prefab"),
                    MinLevel = r.Int("Min"),
                    MaxLevel = r.Int("Max"),
                    ArmorClass = r.Int("Type"),
                })
                .ToList();
        }

        /// <summary>Looks up a monster by the prefab path stored in the spawner data.</summary>
        public MonsterRow MonsterByPrefab(string prefabPath)
        {
            string name = Path.GetFileNameWithoutExtension(prefabPath ?? "");
            return Monsters.Values.FirstOrDefault(m => m.Prefab == name);
        }

        /// <summary>Maps a discipline name to the character "style" value (AbilityDetailInfo.EDisciplineType).</summary>
        private static int DisciplineStyle(string name) => name.Trim().ToLowerInvariant() switch
        {
            "prowess" => 1,
            "fortitude" => 2,
            "cunning" => 4,
            _ => 0,
        };

        public LevelRow Level(int level)
        {
            level = Math.Clamp(level, 1, Levels.Count);
            return Levels[level - 1];
        }

        // ---- table reader ---------------------------------------------------------------------

        public sealed class Row
        {
            private readonly Dictionary<string, string> _values;

            public Row(Dictionary<string, string> values) => _values = values;

            public string Str(string column) => _values.TryGetValue(column, out string v) ? v : "";
            public int Int(string column) => int.TryParse(Str(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            public long Long(string column) => long.TryParse(Str(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
            public float Float(string column) => float.TryParse(Str(column), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }

        /// <summary>The data rows of a table as raw cells, for tables that repeat a column name.</summary>
        public static List<string[]> ReadCells(string path)
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Split('\n').Skip(3)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Split('\t').Select(c => c.Trim()).ToArray())
                .ToList();
        }

        private static int Cell(string[] cells, int index) =>
            index < cells.Length && int.TryParse(cells[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static float CellFloat(string[] cells, int index) =>
            index < cells.Length && float.TryParse(cells[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        public static List<Row> ReadTable(string path)
        {
            // Some tables are UTF-16 (with BOM), others UTF-8; StreamReader detects the BOM.
            string[] lines;
            using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

            string[] names = lines[1].Split('\t').Select(n => n.Trim()).ToArray();
            var rows = new List<Row>();
            foreach (string line in lines.Skip(3))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string[] cells = line.Split('\t');
                var values = new Dictionary<string, string>();
                for (int i = 0; i < names.Length && i < cells.Length; i++)
                    values.TryAdd(names[i], cells[i].Trim());
                rows.Add(new Row(values));
            }
            return rows;
        }
    }
}
