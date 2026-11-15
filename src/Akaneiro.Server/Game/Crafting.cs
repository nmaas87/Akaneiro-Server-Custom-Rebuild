using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Resak the craftsman (STEELNPC): raises an item's level, element, enchantment or gem by one
    /// step following UpgradeRecipes.Recipes. The client sends the attribute the item has now
    /// (recipe AttrID - 1); the recipe for the next one names the materials, and either karma
    /// with a chance of success (KSPrice/KSChance) or crystals with a sure success (CrPrice/CrChance).
    /// A failed attempt uses up karma and materials (CraftingItemFailed 130 plays the client's
    /// failure animation). Only attributes an item already has can be raised: the first step of
    /// every element, enchantment and gem comes with the item.
    /// <para>
    /// The client's material list (materialMap) miscounts stacks, so the server takes the
    /// recipe's amounts from the bag itself. Crystals, the real-money currency, cost nothing here,
    /// which also covers materials bought with crystals (buyMaterial).
    /// </para>
    /// </summary>
    public sealed class Crafting
    {
        private readonly Session _s;

        public Crafting(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private GameData Data => _s.Data;

        public void Craft(byte bagId, uint slot, int craftType, int moneyType, int currentAttr, bool buyMaterial)
        {
            List<ItemData> bag = bagId == Views.BagEquip ? Character.Equipment : bagId == Views.BagBase ? Character.Bag : null;
            ItemData item = bag?.FirstOrDefault(i => i.Slot == slot);
            GameData.ItemProps props = item == null ? null : Data.Items.GetValueOrDefault(item.ItemId);
            GameData.RecipeRow recipe = null;
            string problem = props == null ? "no item in that slot"
                : !Fits(craftType, props.Type) ? $"craft type {craftType} does not fit item type {props.Type}"
                : Current(item, craftType) != currentAttr ? $"item has {Current(item, craftType)}, client sent {currentAttr}"
                : !Data.Recipes.TryGetValue((craftType, currentAttr + 1), out recipe) ? $"no recipe ({craftType}, {currentAttr + 1})"
                : null;
            if (problem != null)
            {
                _s.Log.LogInformation("   crafting rejected: {Problem} (bag {Bag} slot {Slot})", problem, bagId, slot);
                Fail(EServerErrorType.eItemCraftError_ParamError);
                return;
            }

            bool crystals = moneyType == EMoneyType.eMoneyType_FK;
            int price = crystals ? 0 : recipe.KarmaPrice;
            int chance = crystals ? recipe.CrystalChance : recipe.KarmaChance;
            if (chance <= 0 && price <= 0 && recipe.Materials.Count == 0)
            {
                Fail(EServerErrorType.eItemCraftError_MaxLevel); // the first step of a row is no recipe
                return;
            }
            if (_s.Account.Karma < price)
            {
                Fail(EServerErrorType.eItemCraftError_NotEnoughMoneny);
                return;
            }
            var missing = recipe.Materials.Where(m => Owned(m.ItemId) < m.Count).ToList();
            if (missing.Count > 0 && !(crystals || buyMaterial))
            {
                _s.Log.LogInformation("   crafting {Recipe} rejected: missing {Materials}", recipe.Name,
                    string.Join(", ", missing.Select(m => $"{m.Count - Owned(m.ItemId)} x {m.ItemId}")));
                Fail(EServerErrorType.eItemCraftError_ParamError);
                return;
            }

            _s.Account.Karma -= price;
            foreach (var (materialId, count) in recipe.Materials)
                Take(materialId, count); // what is missing was bought with crystals
            bool success = chance >= 100 || _s.World.Random.Next(100) < chance;
            _s.SaveAccount();
            _s.SendMoney();
            if (!success)
            {
                _s.Log.LogInformation("   crafting {Recipe} failed ({Chance} %)", recipe.Name, chance);
                Fail(EServerErrorType.eItemCraftError_CraftFailure);
                return;
            }

            Apply(item, craftType, recipe.AttrId);
            _s.SaveAccount();
            _s.Log.LogInformation("   crafted {Recipe} onto item {Item}", recipe.Name, item.ItemId);
            _s.Send(GameResponses.CraftingItemOK(bagId, (byte)slot, Views.Item(item)));
            if (bagId == Views.BagEquip)
                _s.Battle.SendAttributes();
        }

        /// <summary>Which recipe types an item type takes (InGameScreenShopCraftingCtrl.getRecipeInfos).</summary>
        private static bool Fits(int craftType, int itemType) => craftType switch
        {
            ECraftType.eCraftType_Level => itemType <= GameData.ItemType.TwoHanded,
            ECraftType.eCraftType_Gem => itemType is GameData.ItemType.OneHanded or GameData.ItemType.TwoHanded,
            ECraftType.eCraftType_WeaponElement or ECraftType.eCraftType_WeaponEnchant =>
                itemType is GameData.ItemType.OneHanded or GameData.ItemType.TwoHanded,
            ECraftType.eCraftType_AccessoryElement or ECraftType.eCraftType_AccessoryEnchant =>
                itemType is GameData.ItemType.Neck or GameData.ItemType.Ring,
            ECraftType.eCraftType_ArmorElement or ECraftType.eCraftType_ArmorEnchant =>
                itemType is GameData.ItemType.Head or GameData.ItemType.Chest or GameData.ItemType.Cloak or GameData.ItemType.Legs,
            _ => false,
        };

        private static int Current(ItemData item, int craftType) => craftType switch
        {
            ECraftType.eCraftType_Level => item.Level,
            ECraftType.eCraftType_Gem => item.Gem,
            ECraftType.eCraftType_WeaponElement or ECraftType.eCraftType_ArmorElement or ECraftType.eCraftType_AccessoryElement => item.Element,
            _ => item.Enchant,
        };

        private static void Apply(ItemData item, int craftType, int attr)
        {
            switch (craftType)
            {
                case ECraftType.eCraftType_Level: item.Level = attr; break;
                case ECraftType.eCraftType_Gem: item.Gem = attr; break;
                case ECraftType.eCraftType_WeaponElement:
                case ECraftType.eCraftType_ArmorElement:
                case ECraftType.eCraftType_AccessoryElement: item.Element = attr; break;
                default: item.Enchant = attr; break;
            }
        }

        private int Owned(int itemId) => Character.Bag.Where(i => i.ItemId == itemId).Sum(i => Math.Max(1, i.Count));

        private void Take(int itemId, int count)
        {
            foreach (ItemData stack in Character.Bag.Where(i => i.ItemId == itemId).OrderBy(i => i.Slot).ToList())
            {
                if (count <= 0)
                    return;
                int used = Math.Min(count, Math.Max(1, stack.Count));
                count -= used;
                _s.Inventory.Consume(Views.BagBase, stack, used);
            }
        }

        private void Fail(int error) => _s.Send(GameResponses.CraftingItemFailed(Proto.Error(error)));
    }
}
