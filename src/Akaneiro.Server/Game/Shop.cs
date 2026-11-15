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
    /// level and replaces every 12 hours; the elder's shop sells food, potions and materials from
    /// the client's own tables (BuyConsumeItem). Everything can be sold back.
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

        private readonly Session _s;

        public Shop(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private GameData Data => _s.Data;

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

        /// <summary>
        /// What the April client's Transmute button expects to be paid: its tooltip already shows
        /// GetItemValue(TRANSMUTE) = level * (1 + element + enchant + gem bonus) * (item value *
        /// 0.2f), computed from the value columns of its own tables (see GameData.EnchantValues).
        /// Materials have no item value there, so their transmute shows and pays 0.
        /// </summary>
        public int TransmuteValue(ItemData item)
        {
            int kind = (item.ItemId / 1000) switch { 1 => 0, 2 => 1, 3 => 2, _ => -1 };
            if (kind < 0 || !Data.ItemValues.TryGetValue(item.ItemId, out float baseValue) || baseValue <= 0)
                return 0;

            float bonus = 1f;
            if (item.Element != 0 && Data.ElementValues.TryGetValue((kind, item.Element), out float ele))
                bonus += ele;
            if (item.Enchant != 0 && Data.EnchantValues.TryGetValue((kind, item.Enchant), out float enc))
                bonus += enc;
            if (item.Gem != 0 && Data.GemEffectValues.TryGetValue(item.Gem, out float gem))
                bonus += gem;
            return (int)(item.Level * bonus * (baseValue * 0.2f));
        }

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
            // The rare vendor's items are a little ahead of the character and cost twice as much.
            for (int i = 0; i < RareOffers * 4 && stock.RareItems.Count < RareOffers; i++)
                Add(stock.RareItems, Loot.RollEquipment(Data, Character.Level + 2, _s.World.Random), ref uuid, 2);
            return stock;
        }

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
            foreach (ShopOffer offer in rare ? stock.RareItems : stock.Items)
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
            // 2013-model scrolls go to the account charge counter, no bag slot needed.
            bool charges = _s.Dialect == ClientDialect.Apr2013 && itemId == Views.ReviveScrollItem;
            if (!charges && !_s.Inventory.CanAdd(item))
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
                _s.Send(GameResponses.SaleItemFailed(_s.Err((int)EServerErrorType.eItemError_CantSellItem)));
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
