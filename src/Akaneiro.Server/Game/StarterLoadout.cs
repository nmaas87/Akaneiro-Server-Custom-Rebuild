using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Starting equipment and abilities per discipline. The client shows exactly this gear in the character
    /// creation screen (CreateScreenCtrl.InitProwessEquip/InitFortitudeEquip/InitCunningEquip),
    /// so the server hands out the same items.
    /// </summary>
    public static class StarterLoadout
    {
        // Character "style" = AbilityDetailInfo.EDisciplineType
        public const int Prowess = 1;
        public const int Fortitude = 2;
        public const int Cunning = 4;

        private static readonly Dictionary<int, (int Slot, int ItemId, int PrefabId)[]> Gear = new Dictionary<int, (int, int, int)[]>
        {
            [Prowess] = new[] { (6, 1002, 1), (7, 1002, 1), (0, 2001, 301), (2, 2002, 301), (8, 2003, 301) },
            [Fortitude] = new[] { (6, 1007, 1), (0, 2001, 401), (2, 2002, 401), (8, 2003, 401) },
            [Cunning] = new[] { (6, 1001, 1), (0, 2001, 201), (2, 2002, 201), (8, 2003, 201) },
        };

        /// <summary>The discipline's starting pieces (slot, item, model variant) - also what a summoned friend wears.</summary>
        public static IReadOnlyList<(int Slot, int ItemId, int PrefabId)> GearFor(int style) =>
            Gear.TryGetValue(style, out var items) ? items : Gear[Cunning];

        /// <summary>
        /// Gives a character its starting equipment and abilities once. Returns true if anything changed.
        /// The abilities are the three the character creation screen advertises for the discipline.
        /// </summary>
        public static bool Grant(CharacterData character, GameData data)
        {
            // Gear and abilities are tracked separately so characters created before either
            // existed still receive the missing half on their next login.
            bool changed = false;

            if (!character.StarterGearGranted)
            {
                if (!Gear.TryGetValue(character.Style, out var items))
                    items = Gear[Cunning];

                foreach (var (slot, itemId, prefabId) in items)
                {
                    if (character.Equipment.Any(e => e.Slot == slot))
                        continue;
                    character.Equipment.Add(new ItemData { Slot = slot, ItemId = itemId, PrefabId = prefabId });
                }
                character.StarterGearGranted = true;
                changed = true;
            }

            if (!character.StarterAbilitiesGranted)
            {
                if (data.StartAbilities.TryGetValue(character.Style, out var abilities) ||
                    data.StartAbilities.TryGetValue(Cunning, out abilities))
                {
                    int slot = 0;
                    foreach (int abilityId in abilities)
                    {
                        if (abilityId == 0 || character.Abilities.Any(a => a.Id == abilityId))
                            continue;
                        character.Abilities.Add(new AbilityData { Id = abilityId });
                        character.Shortcuts.Add(new ShortcutData { Group = 0, Index = slot++, AbilityId = abilityId });
                    }
                }
                character.StarterAbilitiesGranted = true;
                changed = true;
            }

            return changed;
        }
    }
}
