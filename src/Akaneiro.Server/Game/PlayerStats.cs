using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Akaneiro.Server.Data;

namespace Akaneiro.Server.Game
{
    /// <summary>A buff on the player (food, tea, ...) and when it runs out.</summary>
    public sealed record ActiveBuff(GameData.BuffRow Buff, DateTime Until);

    /// <summary>
    /// An effect a hit can set off (weapon elements, the Two-Handed mastery): stun, slow, knockback,
    /// burn or heal, with its chance in percent, how long it lasts and its strength.
    /// </summary>
    public sealed record HitProc(string Kind, float Chance, float Seconds, float Value, int StatusId);

    /// <summary>
    /// Everything that makes up the character's numbers: talents, gems, enchantments, masteries and
    /// active food buffs. The original server's formulas are lost; the effects are read from the
    /// descriptions the client shows for them ("5% Boost to Physical Damage", "Power +9",
    /// "5% Chance to Stun for 3s", "Adds 3% to Critical Damage ..."). Effects without a number in
    /// their text ("a small boost to POWER") use the values in <see cref="Small"/>.
    /// </summary>
    public sealed class PlayerStats
    {
        public int Power, Defense, Skill;
        public int MaxHp, MaxMp;

        /// <summary>Recovery per regeneration tick (two ticks a second, see Views.RegenTicksPerSecond).</summary>
        public int HpRegen, MpRegen;

        /// <summary>Percent bonuses.</summary>
        public float DamagePct, DamageReductionPct, XpPct, LootPct, KarmaPct;

        /// <summary>Chance of a critical hit in percent, and how hard it hits (percent of a normal hit).</summary>
        public float CritChance, CritDamage;

        /// <summary>Percent of normal speed, as the client expects them.</summary>
        public int MoveSpeed = 100, AttackSpeed = 100;

        public List<HitProc> Procs { get; } = new List<HitProc>();

        // ---- the rules that are not in the data (assumptions) ------------------------------------

        /// <summary>Critical hit chance without any bonus, in percent.</summary>
        public const float BaseCritChance = 5f;

        /// <summary>Crit chance per point of SKILL (GameConfig.xml DisciplineMultiplier Critical="0.25", per 10 points).</summary>
        public const float CritPerSkill = 0.025f;

        /// <summary>Critical hits do 135 % (GameConfig.xml CharAttr CritBase="135").</summary>
        public const float BaseCritDamage = 135f;

        /// <summary>Weapon damage per point of POW in percent (GameConfig.xml DisciplineMultiplier Attack="2", per 10 points).</summary>
        public const float DamagePctPerPower = 0.2f;

        /// <summary>Values for buffs whose text has no number: "small boost", "doubles", "more than doubles", "large boost".</summary>
        public static class Small
        {
            public const float Stat = 25f, StatDoubled = 110f, Speed = 10f, Crit = 10f, Xp = 25f, Loot = 25f, LootLarge = 80f;
            public const float Regen = 50f, RegenDoubled = 100f;
        }

        public static PlayerStats Compute(CharacterData c, GameData data, IEnumerable<ActiveBuff> buffs = null, DateTime? now = null)
        {
            var s = new PlayerStats();
            var basic = Views.BaseStats(c);
            GameData.LevelRow level = data.Level(c.Level);
            float statPctPower = 0, statPctDefense = 0, statPctSkill = 0, hpPct = 0, mpPct = 0, hpRegenPct = 0, mpRegenPct = 0;
            float moveSpeed = 0, attackSpeed = 0, mpRegenFlat = 0;
            int gemPower = 0, gemDefense = 0, gemSkill = 0;

            // ---- equipment: gems, enchantments, elements -----------------------------------------
            foreach (ItemData item in c.Equipment)
            {
                int kind = KindOf(item, data);
                if (item.Gem != 0 && data.Gems.TryGetValue(item.Gem, out var gem))
                {
                    int v = (int)Math.Round(gem.Value);
                    switch (gem.Talent)
                    {
                        case 1: gemPower += v; break;
                        case 2: gemDefense += v; break;
                        case 3: gemPower += v; gemDefense += v; break;
                        case 4: gemSkill += v; break;
                        case 5: gemSkill += v; gemPower += v; break;
                        case 6: gemDefense += v; gemSkill += v; break;
                        case 7: gemPower += v; gemDefense += v; gemSkill += v; break;
                        case 16: s.KarmaPct += gem.Value * 100f; break;
                    }
                }
                if (item.Enchant != 0 && data.EnchantStatus.TryGetValue((kind, item.Enchant), out int enchantStatus))
                    ApplyText(Describe(enchantStatus, data));
                if (item.Element != 0 && data.ElementStatus.TryGetValue((kind, item.Element), out int elementStatus))
                    ApplyText(Describe(elementStatus, data), elementStatus);
            }

            // ---- masteries ---------------------------------------------------------------------
            List<ItemData> hands = c.Equipment.Where(e => e.Slot is Inventory.PartMainHand or Inventory.PartOffHand).ToList();
            bool twoHanded = hands.Any(h => data.Items.TryGetValue(h.ItemId, out var p) && p.Type == GameData.ItemType.TwoHanded);
            int oneHanded = hands.Count(h => data.Items.TryGetValue(h.ItemId, out var p) && p.Type == GameData.ItemType.OneHanded);
            foreach (var (type, mastery) in c.Masteries)
            {
                GameData.MasteryRow row = data.Masteries.Values.FirstOrDefault(m => m.Type == type && m.Level == mastery);
                if (row == null || !data.MasteryEffects.TryGetValue(row.Id, out string text))
                    continue;
                float pct = Number(text);
                switch (type)
                {
                    case 1 when oneHanded == 1 && !twoHanded: s.CritDamage += pct; break;  // Dueling
                    case 2 when oneHanded == 2: attackSpeed += pct; break;                   // Dual Wielding
                    case 4 when twoHanded: s.Procs.Add(new HitProc("Stun", pct, 2f, 0, 0)); break; // Two-Handed
                    case 8: moveSpeed += pct * ArmorPieces(c, data, 8); break;               // Light Armor
                    case 16: mpRegenPct += pct * ArmorPieces(c, data, 16); break;            // Medium Armor
                    case 32: hpRegenPct += pct * ArmorPieces(c, data, 32); break;            // Heavy Armor
                }
            }

            // ---- the spirit helper at the player's side ------------------------------------------
            GameData.SpiritRow spirit = ActiveSpirit(c, data, now);
            if (spirit != null)
            {
                string e = spirit.Effect;
                float n = Number(e);
                if (e.Contains("Power", StringComparison.OrdinalIgnoreCase)) statPctPower += n;
                if (e.Contains("Skill", StringComparison.OrdinalIgnoreCase)) statPctSkill += n;
                if (e.Contains("Defense", StringComparison.OrdinalIgnoreCase)) statPctDefense += n;
                if (e.Contains("Energy regen", StringComparison.OrdinalIgnoreCase)) mpRegenPct += n;
                if (e.Contains("Health regen", StringComparison.OrdinalIgnoreCase)) hpRegenPct += n;
                if (e.Contains("max Energy", StringComparison.OrdinalIgnoreCase)) mpPct += n;
                if (e.Contains("Attack Speed", StringComparison.OrdinalIgnoreCase)) attackSpeed += n;
                if (e.Contains("Critical Damage", StringComparison.OrdinalIgnoreCase)) s.CritDamage += n;
            }

            // ---- food and drink ------------------------------------------------------------------
            foreach (ActiveBuff active in buffs ?? Enumerable.Empty<ActiveBuff>())
            {
                if (now.HasValue && active.Until <= now.Value)
                    continue;
                string d = active.Buff.Description ?? "";
                bool doubled = d.Contains("doubles", StringComparison.OrdinalIgnoreCase);
                bool large = d.Contains("large", StringComparison.OrdinalIgnoreCase);
                float n = Number(d);
                if (d.Contains("POWER", StringComparison.Ordinal)) statPctPower += doubled ? Small.StatDoubled : Small.Stat;
                else if (d.Contains("SKILL", StringComparison.Ordinal)) statPctSkill += doubled ? Small.StatDoubled : Small.Stat;
                else if (d.Contains("DEFENSE", StringComparison.Ordinal)) statPctDefense += doubled ? Small.StatDoubled : Small.Stat;
                else if (d.Contains("Movement", StringComparison.OrdinalIgnoreCase)) moveSpeed += n > 0 ? n : Small.Speed;
                else if (d.Contains("Critical", StringComparison.OrdinalIgnoreCase)) s.CritChance += n > 0 ? n : Small.Crit;
                else if (d.Contains("XP", StringComparison.Ordinal)) s.XpPct += n > 0 ? n : Small.Xp;
                else if (d.Contains("loot", StringComparison.OrdinalIgnoreCase)) s.LootPct += large ? Small.LootLarge : Small.Loot;
                else if (d.Contains("Energy regen", StringComparison.OrdinalIgnoreCase)) mpRegenPct += doubled ? Small.RegenDoubled : Small.Regen;
                else if (d.Contains("HP regen", StringComparison.OrdinalIgnoreCase)) hpRegenPct += doubled ? Small.RegenDoubled : Small.Regen;
            }

            // ---- totals --------------------------------------------------------------------------
            s.Power = (int)Math.Round((basic.Power + gemPower) * (1 + statPctPower / 100f));
            s.Defense = (int)Math.Round((basic.Defense + gemDefense) * (1 + statPctDefense / 100f));
            s.Skill = (int)Math.Round((basic.Skill + gemSkill) * (1 + statPctSkill / 100f));
            s.MaxHp = (int)Math.Round(level.MaxHp * (1 + hpPct / 100f));
            s.MaxMp = (int)Math.Round(level.MaxMp * (1 + mpPct / 100f));

            int baseMpRegen = Views.MpRecoverPerTick(level);
            s.MpRegen = (int)Math.Round(baseMpRegen * (1 + mpRegenPct / 100f) + mpRegenFlat / Views.RegenTicksPerSecond);
            // Health does not come back on its own; buffs and Heavy Armor give it a trickle,
            // measured against one percent of the maximum per second.
            float hpTrickle = s.MaxHp / 100f / (float)Views.RegenTicksPerSecond;
            s.HpRegen = (int)Math.Round(hpTrickle * hpRegenPct / 100f);

            s.CritChance += BaseCritChance + s.Skill * CritPerSkill;
            s.CritDamage += BaseCritDamage;
            s.DamagePct += s.Power * DamagePctPerPower;
            s.MoveSpeed = (int)Math.Round(100 + moveSpeed);
            s.AttackSpeed = (int)Math.Round(100 + attackSpeed);
            return s;

            void ApplyText(string text, int statusId = 0)
            {
                if (string.IsNullOrEmpty(text))
                    return;
                Match m;
                if ((m = Regex.Match(text, @"([\d.]+)% Boost to Physical Damage", RegexOptions.IgnoreCase)).Success) s.DamagePct += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Boost to base Energy", RegexOptions.IgnoreCase)).Success) mpPct += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Boost to base Health", RegexOptions.IgnoreCase)).Success) hpPct += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Boost to Damage Reduction", RegexOptions.IgnoreCase)).Success) s.DamageReductionPct += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Boost to base Move Speed", RegexOptions.IgnoreCase)).Success) moveSpeed += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Boost to Crit Chance", RegexOptions.IgnoreCase)).Success) s.CritChance += F(m, 1);
                else if ((m = Regex.Match(text, @"Energy Regen by ([\d.]+)/s", RegexOptions.IgnoreCase)).Success) mpRegenFlat += F(m, 1);
                else if ((m = Regex.Match(text, @"([\d.]+)% Chance to Stun for ([\d.]+)s", RegexOptions.IgnoreCase)).Success) s.Procs.Add(new HitProc("Stun", F(m, 1), F(m, 2), 0, statusId));
                else if ((m = Regex.Match(text, @"([\d.]+)% Chance to Slow for ([\d.]+)s", RegexOptions.IgnoreCase)).Success) s.Procs.Add(new HitProc("Slow", F(m, 1), F(m, 2), 0, statusId));
                else if ((m = Regex.Match(text, @"([\d.]+)% Chance to Knockback", RegexOptions.IgnoreCase)).Success) s.Procs.Add(new HitProc("Knockback", F(m, 1), 0, 3f, statusId));
                else if ((m = Regex.Match(text, @"([\d.]+)% Chance to Burn -([\d.]+) dmg over ([\d.]+)s", RegexOptions.IgnoreCase)).Success) s.Procs.Add(new HitProc("Burn", F(m, 1), F(m, 3), F(m, 2), statusId));
                else if ((m = Regex.Match(text, @"([\d.]+)% chance to Heal ([\d.]+)% of Damage", RegexOptions.IgnoreCase)).Success) s.Procs.Add(new HitProc("Heal", F(m, 1), 0, F(m, 2), statusId));
            }
        }

        private static GameData.SpiritRow ActiveSpirit(CharacterData c, GameData data, DateTime? now)
        {
            long unix = new DateTimeOffset(now ?? DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
            SpiritRental rental = c.Spirits.FirstOrDefault(r => r.Chosen && unix < r.BeginUnix + r.Seconds);
            if (rental == null)
                return null;
            // Same rule as the spirit service: the rental is the line (its level-1 id) and the
            // shared experience counter says how far that line has grown. Account files written
            // before that model promoted the rental itself (1005 = level 5): their id still
            // carries the level, which is folded in here without touching the save.
            int kind = rental.PetId / 100;
            ulong experience = c.SpiritExp.GetValueOrDefault(kind);
            if (data.Spirits.TryGetValue(rental.PetId, out GameData.SpiritRow atId))
                experience = Math.Max(experience, atId.XpNeeded);
            return data.Spirits.Values
                .Where(s => s.Id / 100 == kind && s.XpNeeded <= experience)
                .OrderByDescending(s => s.Level)
                .FirstOrDefault();
        }

        /// <summary>0 weapon, 1 armor, 2 accessory - the tables an item's enchantment and element come from.</summary>
        public static int KindOf(ItemData item, GameData data)
        {
            if (data.Weapons.ContainsKey(item.ItemId))
                return 0;
            if (data.Items.TryGetValue(item.ItemId, out GameData.ItemProps props)
                && props.Type is GameData.ItemType.Neck or GameData.ItemType.Ring)
                return 2;
            return 1;
        }

        /// <summary>Worn armor pieces of a class (8 light, 16 medium, 32 heavy), by their model.</summary>
        private static int ArmorPieces(CharacterData c, GameData data, int armorClass) =>
            c.Equipment.Count(e => KindOf(e, data) == 1
                                   && data.ArmorPrefabs.Any(p => p.PrefabId == e.PrefabId && p.ArmorClass == armorClass));

        private static string Describe(int statusId, GameData data) =>
            data.Buffs.TryGetValue(statusId, out GameData.BuffRow buff) ? buff.Description : "";

        /// <summary>The first number in a text ("Adds 3% to ..." -> 3), 0 if there is none.</summary>
        private static float Number(string text)
        {
            Match m = Regex.Match(text ?? "", @"(\d+(?:\.\d+)?)\s*%");
            return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0f;
        }

        private static float F(Match m, int group) => float.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
    }
}
