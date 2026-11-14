using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// The village vendors. Gato (SHOPNPC) sells equipment the server rolls for the character's
    /// level and replaces every 12 hours; the four rare offers (Goto's shelf, displayed in the same
    /// window) sell named uniques from the original catalog (uniqueItem.rare). The elder's shop sells
    /// food, potions and materials from the client's own tables (BuyConsumeItem). Everything can be
    /// sold back.
    /// <para>
    /// Prices: the client values equipment at level * (1 + element + enchant + gem) * base value * 0.4
    /// (PlayerDataManager.GetItemValue(2, ...)) and shows that in the tooltip; that is what selling
    /// pays. Buying costs 2.5 times as much (level * base value). Stackables sell for a fifth of
    /// their shop price. The buy factors are an assumption, the original server code is lost.
    /// </para>
    /// </summary>
    public sealed class Shop
    {
        public static readonly TimeSpan StockLifetime = TimeSpan.FromHours(12);
        public const int OffersPerStock = 16;
        public const int RareOffers = 4;

        /// <summary>Bundle kinds (ShopOffer.Bundle): whole-catalog packs delivered by mail.</summary>
        public const int BundleWeapons = 1, BundleArmor = 2, BundleAccessories = 3, BundleCollector = 4;

        private readonly Session _s;

        public Shop(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private GameData Data => _s.World.Data;

        // ---- prices -------------------------------------------------------------------------------

        /// <summary>What the inventory tooltip shows and selling pays (GetItemValue type 2).</summary>
        public int SellValue(ItemData item)
        {
            if (Data.ItemValues.TryGetValue(item.ItemId, out float baseValue))
                return (int)(item.Level * baseValue * 0.4f);
            GameData.ItemProps props = Data.Items.GetValueOrDefault(item.ItemId);
            return props == null ? 0 : props.SellPrice * Math.Max(1, item.Count) / 5;
        }

        public int BuyPrice(ItemData item) =>
            Data.ItemValues.TryGetValue(item.ItemId, out float baseValue) ? (int)(item.Level * baseValue) : 0;

        // ---- stock --------------------------------------------------------------------------------

        /// <summary>The current offers, rolled anew when they are 12 hours old or the character leveled.</summary>
        public ShopStock Stock()
        {
            long now = new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
            ShopStock stock = Character.Shop;
            if (stock == null || stock.Level != Character.Level || now - stock.RolledUnix >= (long)StockLifetime.TotalSeconds)
            {
                stock = Roll(now);
                Character.Shop = stock;
                _s.SaveAccount();
            }
            return stock;
        }

        private ShopStock Roll(long now)
        {
            var stock = new ShopStock { RolledUnix = now, Level = Character.Level };
            int uuid = 1;
            for (int i = 0; i < OffersPerStock * 4 && stock.Items.Count < OffersPerStock; i++)
                Add(stock.Items, Loot.RollEquipment(Data, Character.Level, _s.World.Random), ref uuid, 1);
            // Goto's shelf: named uniques from the rare catalog, a little ahead of the character and
            // at twice the price. The catalog only shows a handful of rows per level, so slots the
            // uniques cannot fill (small catalog, big shelf) fall back to rolled gear.
            var offeredUniques = new HashSet<int>();
            for (int i = 0; i < RareOffers * 4 && stock.RareItems.Count < RareOffers; i++)
            {
                ItemData unique = Loot.RollUniqueShop(Data, Character.Level + 2, _s.World.Random, offeredUniques);
                if (unique != null)
                {
                    offeredUniques.Add(unique.ItemId);
                    Add(stock.RareItems, unique, ref uuid, 2);
                }
                else
                {
                    Add(stock.RareItems, Loot.RollEquipment(Data, Character.Level + 2, _s.World.Random), ref uuid, 2);
                }
            }
            // Four permanent tiles: whole-catalog packs whose goods arrive by mail.
            if (_s.World.Options.ShopBundles)
                foreach (int kind in new[] { BundleWeapons, BundleArmor, BundleAccessories, BundleCollector })
                    AddBundle(stock.RareItems, kind, ref uuid);
            return stock;
        }

        // ---- bundles ----------------------------------------------------------------------------

        /// <summary>
        /// Adds one mail-delivered bundle tile: the row shows a representative unique, the price is
        /// the sum of the whole pack at rare prices, and the goods (every catalogued + special item
        /// of that kind) are rolled at buy time so they match the level you pay them at.
        /// </summary>
        private void AddBundle(List<ShopOffer> offers, int kind, ref int uuid)
        {
            List<ItemData> pack = PackItems(kind, Character.Level);
            if (pack.Count == 0)
                return;

            // The tile's shown row: a generic item the host picked (the client names and pictures
            // rows by item id, so a shared plain row is the most generic it gets), else the pack's
            // flashiest unique. The row shows at the shelf level (like the other rare offers); the
            // mail pack is rolled at buy time for the level you actually pay at.
            ItemData representative;
            int genericId = _s.World.Options.ShopBundleItem;
            if (genericId != 0 && Data.ItemValues.ContainsKey(genericId))
            {
                representative = new ItemData
                {
                    ItemId = genericId,
                    PrefabId = Loot.RollPrefabFor(Data, genericId, Character.Level + 2, _s.World.Random),
                    Level = Character.Level + 2,
                    Count = 1,
                };
            }
            else
            {
                ItemData standout = pack.OrderByDescending(i => i.Enchant != 0 || i.Gem != 0 || i.Element != 0)
                    .ThenBy(i => i.ItemId)
                    .First();
                representative = new ItemData
                {
                    ItemId = standout.ItemId, PrefabId = standout.PrefabId,
                    Level = Character.Level + 2,
                    Gem = standout.Gem, Element = standout.Element, Enchant = standout.Enchant,
                    Count = 1,
                };
            }
            var offer = new ShopOffer
            {
                Uuid = uuid,
                Item = representative,
                Price = pack.Sum(i => BuyPrice(i) * 2),
                MoneyType = EMoneyType.eMoneyType_SK,
                Bundle = kind,
            };
            uuid++;
            offers.Add(offer);
        }

        /// <summary>Everything the catalog and the special rows hold for one kind, at the given level.</summary>
        private List<ItemData> PackItems(int kind, int level)
        {
            var items = new List<ItemData>();
            var seen = new HashSet<(int Item, int Prefab, int Enchant, int Gem, int Element)>();

            foreach (GameData.UniqueShopRow row in Data.UniqueShop.Values.OrderBy(r => r.Id))
            {
                if (kind != BundleCollector && !FitsBundle(Data.ItemEquipSlots.GetValueOrDefault(row.BaseId), kind))
                    continue;
                if (!Data.ItemValues.ContainsKey(row.BaseId))
                    continue;
                if (!seen.Add((row.BaseId, row.PrefabId, row.Enchant, row.Gem, row.Element)))
                    continue;
                items.Add(new ItemData
                {
                    ItemId = row.BaseId, PrefabId = row.PrefabId, Level = level,
                    Gem = row.Gem, Element = row.Element, Enchant = row.Enchant, Count = 1,
                });
            }

            foreach (GameData.ItemProps props in Data.SpecialItems.Values.OrderBy(i => i.Id))
            {
                if (kind != BundleCollector && !FitsBundle(props.EquipPoint, kind))
                    continue;
                if (!Data.ItemValues.ContainsKey(props.Id))
                    continue;
                if (!seen.Add((props.Id, 0, 0, 0, 0)))
                    continue;
                items.Add(new ItemData
                {
                    ItemId = props.Id,
                    PrefabId = Loot.RollWeaponPrefab(Data, level, _s.World.Random), // the specials are weapon-table rows
                    Level = level,
                    Count = 1,
                });
            }
            return items;
        }

        private static bool FitsBundle(int equipPoint, int kind) => kind switch
        {
            BundleWeapons => equipPoint is 7 or 8,
            BundleArmor => equipPoint is 1 or 3 or 4 or 6,
            BundleAccessories => equipPoint is 2 or 5,
            _ => true,
        };

        private void Add(List<ShopOffer> offers, ItemData item, ref int uuid, int priceFactor)
        {
            if (item == null || BuyPrice(item) <= 0)
                return;
            item.Serial = uuid;
            offers.Add(new ShopOffer { Uuid = uuid++, Item = item, Price = BuyPrice(item) * priceFactor, MoneyType = EMoneyType.eMoneyType_SK });
        }

        private static SShopItemInfo Info(ShopOffer offer)
        {
            var info = Proto.New<SShopItemInfo>();
            info.UUID = offer.Uuid;
            info.ID = offer.Item.ItemId;
            info.perfrab = offer.Item.PrefabId;
            info.level = (uint)offer.Item.Level;
            info.guid.serialID = offer.Uuid;
            info.enchant = offer.Item.Enchant;
            info.element = offer.Item.Element;
            info.gem = offer.Item.Gem;
            info.moneyType = offer.MoneyType;
            info.price = offer.Price;
            info.isVisible = true;
            return info;
        }

        /// <summary>
        /// Gato's screen opens once both lists arrived (PlayerDataManager.AddInitShopDataFlag
        /// counts to 2): the client asks for the normal list, then the rare one.
        /// </summary>
        public void SendStock(bool rare)
        {
            ShopStock stock = Stock();
            var list = new vectorShopItemInfos();
            IEnumerable<ShopOffer> offers = rare ? stock.RareItems : stock.Items;
            // A stock rolled while bundles were on keeps them until it refreshes; hide them if the
            // host turned the feature off in the meantime.
            if (rare && !_s.World.Options.ShopBundles)
                offers = offers.Where(o => o.Bundle == 0);
            foreach (ShopOffer offer in offers)
                list.Add(Info(offer));
            if (rare)
                _s.Send(GameResponses.SendShopRareitemTrackInfo(false, list, (int)_s.World.Clock.UtcNow.DayOfWeek));
            else
                _s.Send(GameResponses.SendShopIteminfo(false, list));
        }

        // ---- buying and selling ---------------------------------------------------------------------

        public void Buy(bool rare, SBuyitemInfo info)
        {
            ShopStock stock = Stock();
            List<ShopOffer> offers = rare ? stock.RareItems : stock.Items;
            ShopOffer offer = offers.FirstOrDefault(o => o.Uuid == info.UUID);
            if (offer == null)
            {
                Fail(EServerErrorType.eItemError_BuyCantFindItem);
                return;
            }
            if (offer.Bundle != 0)
            {
                // A bundle is not one item: the pack (rolled now, at this level) goes into a mail,
                // so the bag never has to hold it and a full bag cannot block the sale.
                if (!Pay(offer.Price, offer.MoneyType))
                {
                    Fail(EServerErrorType.eItemError_BuyNotEnoughMoneny);
                    return;
                }
                List<ItemData> pack = PackItems(offer.Bundle, Character.Level);
                offers.Remove(offer);
                _s.Mail.SendBundleMail(offer.Bundle, pack);
                _s.SaveAccount();
                _s.Log.LogInformation("   sold bundle {Bundle}: {Count} items mailed for {Price} karma", offer.Bundle, pack.Count, offer.Price);
                _s.SendMoney();
                _s.Send(GameResponses.BuyItemSuccAck(rare ? 1 : 0, info));
                return;
            }
            if (!_s.Inventory.CanAdd(offer.Item))
            {
                Fail(EServerErrorType.eItemError_BagFull);
                return;
            }
            if (!Pay(offer.Price, offer.MoneyType))
            {
                Fail(EServerErrorType.eItemError_BuyNotEnoughMoneny);
                return;
            }

            offers.Remove(offer);
            _s.Inventory.AddToBag(offer.Item);
            _s.SaveAccount();
            _s.Log.LogInformation("   bought {Item} (level {Level}) for {Price} karma", offer.Item.ItemId, offer.Item.Level, offer.Price);
            _s.SendMoney();
            _s.Send(GameResponses.BuyItemSuccAck(rare ? 1 : 0, info));
        }

        /// <summary>Food, potions and crafting materials from the elder's shop; prices are the client's (Props SellPrice).</summary>
        public void BuyConsumable(int consumeItemType, int itemId, int count)
        {
            GameData.ItemProps props = Data.Items.GetValueOrDefault(itemId);
            if (props == null || props.Type <= GameData.ItemType.TwoHanded || count < 1 || count > props.StackSize)
            {
                Fail(EServerErrorType.eItemError_BuyCantFindItem);
                return;
            }
            var item = new ItemData { ItemId = itemId, Level = Character.Level, Count = count };
            Data.EnsurePrefab(item); // the client's consumable shop shows these with icon index 1
            if (!_s.Inventory.CanAdd(item))
            {
                Fail(EServerErrorType.eItemError_BagFull);
                return;
            }
            int moneyType = props.UsesRealMoney ? EMoneyType.eMoneyType_FK : EMoneyType.eMoneyType_SK;
            if (!Pay(props.SellPrice * count, moneyType))
            {
                Fail(EServerErrorType.eItemError_BuyNotEnoughMoneny);
                return;
            }

            _s.Inventory.AddToBag(item);
            _s.SaveAccount();
            _s.Log.LogInformation("   bought {Count} x {Item}", count, props.Name);
            _s.SendMoney();
            _s.Send(GameResponses.BuyConsumeItemSuccAck(consumeItemType, itemId, count));
        }

        public void Sell(byte bagId, uint slot)
        {
            ItemData item = bagId == Views.BagBase ? Character.Bag.FirstOrDefault(i => i.Slot == slot) : null;
            if (item == null)
            {
                _s.Send(GameResponses.SaleItemFailed(Proto.Error(EServerErrorType.eItemError_CantSellItem)));
                return;
            }
            int value = SellValue(item);
            Character.Bag.Remove(item);
            _s.Account.Karma += value;
            _s.SaveAccount();
            _s.Log.LogInformation("   sold {Item} for {Value} karma", item.ItemId, value);
            _s.Send(GameResponses.NotifyItemDestroy(bagId, slot));
            _s.SendMoney();
        }

        /// <summary>
        /// Takes the price off the account. Crystals are the real-money currency; with nothing to
        /// buy them with, crystal prices are waived (like crystal revivals).
        /// </summary>
        private bool Pay(int price, int moneyType)
        {
            if (moneyType == EMoneyType.eMoneyType_FK)
                return true;
            if (_s.Account.Karma < price)
                return false;
            _s.Account.Karma -= price;
            return true;
        }

        private void Fail(int error)
        {
            _s.Log.LogInformation("   purchase rejected ({Error})", error);
            _s.Send(GameResponses.BuyItemFailed(Proto.Error(error)));
        }
    }
}
