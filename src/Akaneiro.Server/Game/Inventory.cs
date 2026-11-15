using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Bag, equipment and stash of the playing character. The client's inventory screen
    /// (InventoryCtrl, PlayerDataManager) sends the requests and only changes its own slots when
    /// the server answers: NotifyItemSwap swaps two slots (and rebuilds the player model when the
    /// equip bag takes part), NotifyItemUpdate replaces one, NotifyItemDestroy empties one.
    /// </summary>
    public sealed class Inventory
    {
        /// <summary>The inventory screen shows bag slots 0..19 (CharInfo_InventoryManager: slot &lt; 21, 1-based).</summary>
        public const int BagSlots = 20;

        /// <summary>Stash slots per tab (Stash.GetCurSoltIdx: tab * 12 + slot).</summary>
        public const int StashSlotsPerTab = 12;

        // Equipment parts (the equip bag's slots).
        public const int PartMainHand = 6;
        public const int PartOffHand = 7;

        private readonly Session _s;

        public Inventory(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private GameData Data => _s.Data;

        /// <summary>The equipment part an item goes into, as the client decides it (InventoryCtrl.GetSlotFromType).</summary>
        public static int PartOf(int itemType) => itemType switch
        {
            GameData.ItemType.Head => 0,
            GameData.ItemType.Neck => 1,
            GameData.ItemType.Chest => 2,
            GameData.ItemType.Cloak => 3,
            GameData.ItemType.Ring => 4,
            GameData.ItemType.Legs => 8,
            GameData.ItemType.OneHanded or GameData.ItemType.TwoHanded => PartMainHand,
            _ => -1,
        };

        public GameData.ItemProps Props(ItemData item) =>
            Data.Items.TryGetValue(item.ItemId, out GameData.ItemProps props) ? props : null;

        private List<ItemData> Bag(byte bagId) => bagId switch
        {
            Views.BagEquip => Character.Equipment,
            Views.BagBase => Character.Bag,
            Views.BagStash => Character.Stash,
            _ => null,
        };

        private int Capacity(byte bagId) => bagId switch
        {
            Views.BagEquip => 9,
            Views.BagBase => BagSlots,
            Views.BagStash => Character.StashTabs * StashSlotsPerTab,
            _ => 0,
        };

        private static ItemData At(List<ItemData> bag, uint slot) => bag.FirstOrDefault(i => i.Slot == slot);

        // ---- equipment ------------------------------------------------------------------------

        /// <summary>Moves a bag item onto an equipment part; what was worn there goes into its bag slot.</summary>
        public void Equip(byte part, byte bagId, uint slot)
        {
            part = ChooseHand(part, bagId, slot);
            int error = TryEquip(part, bagId, slot);
            if (error != EServerErrorType.eSuccess)
            {
                _s.Log.LogInformation("   equip rejected ({Error}): part {Part}, bag {Bag} slot {Slot}", error, part, bagId, slot);
                _s.Send(GameResponses.EquipErrorFailed(Proto.Error(error)));
                return;
            }
            _s.Send(GameResponses.NotifyItemSwap(bagId, slot, Views.BagEquip, part));
            GearChanged();
        }

        /// <summary>
        /// The client sends every one-handed weapon to the off hand once the main hand holds one
        /// (InventoryCtrl.EquipItem), so the main weapon could never be changed. With both hands
        /// full, the new weapon replaces the one worn longer instead; equipping twice replaces both.
        /// The client puts the weapon where NotifyItemSwap says.
        /// </summary>
        private byte ChooseHand(byte part, byte bagId, uint slot)
        {
            if (part != PartOffHand)
                return part;
            ItemData item = bagId == Views.BagEquip ? null : At(Bag(bagId) ?? new List<ItemData>(), slot);
            ItemData main = At(Character.Equipment, PartMainHand);
            ItemData off = At(Character.Equipment, PartOffHand);
            if (item == null || main == null || off == null || Props(item)?.Type != GameData.ItemType.OneHanded)
                return part;
            // Equipment keeps the order things were put on: the earlier one has been worn longer.
            return Character.Equipment.IndexOf(main) < Character.Equipment.IndexOf(off) ? (byte)PartMainHand : part;
        }

        private int TryEquip(byte part, byte bagId, uint slot)
        {
            List<ItemData> bag = Bag(bagId);
            ItemData item = bag == null || bagId == Views.BagEquip ? null : At(bag, slot);
            GameData.ItemProps props = item == null ? null : Props(item);
            if (props == null)
                return EServerErrorType.eItemError_NoExist;

            int wanted = PartOf(props.Type);
            bool fits = part == wanted || (props.Type == GameData.ItemType.OneHanded && part == PartOffHand);
            if (!fits)
                return EServerErrorType.eItemError_CantBeEquiped;
            // The inventory marks items above the character's level red (InventorySlot: info_Level > CurLV).
            if (Math.Max(props.LevelRequirement, item.Level) > Character.Level)
                return EServerErrorType.eItemError_PlayerLvlNotEnough;

            // A two-handed weapon needs a free off hand (the client unequips it first); an off-hand
            // weapon cannot go next to a two-handed one.
            ItemData offHand = At(Character.Equipment, PartOffHand);
            ItemData mainHand = At(Character.Equipment, PartMainHand);
            if (props.Type == GameData.ItemType.TwoHanded && offHand != null)
                return EServerErrorType.eItemError_CantBeEquiped;
            if (part == PartOffHand && mainHand != null && Props(mainHand)?.Type == GameData.ItemType.TwoHanded)
                return EServerErrorType.eItemError_CantBeEquiped;

            ItemData worn = At(Character.Equipment, part);
            bag.Remove(item);
            if (worn != null)
            {
                Character.Equipment.Remove(worn);
                worn.Slot = (int)slot;
                bag.Add(worn);
            }
            item.Slot = part;
            Character.Equipment.Add(item);
            _s.Log.LogInformation("   equipped {Item} on part {Part}", props.Name, part);
            return EServerErrorType.eSuccess;
        }

        /// <summary>Takes the item off an equipment part into a bag slot.</summary>
        public void Unequip(byte part, byte bagId, uint slot)
        {
            int error = TryUnequip(part, bagId, slot);
            if (error != EServerErrorType.eSuccess)
            {
                _s.Log.LogInformation("   unequip rejected ({Error}): part {Part}, bag {Bag} slot {Slot}", error, part, bagId, slot);
                _s.Send(GameResponses.UnEquipErrorFailed(Proto.Error(error)));
                return;
            }
            _s.Send(GameResponses.NotifyItemSwap(Views.BagEquip, part, bagId, slot));
            GearChanged();
        }

        private int TryUnequip(byte part, byte bagId, uint slot)
        {
            ItemData worn = At(Character.Equipment, part);
            if (worn == null)
                return EServerErrorType.eItemError_NoExist;
            List<ItemData> bag = Bag(bagId);
            if (bag == null || bagId == Views.BagEquip || slot >= Capacity(bagId))
                return EServerErrorType.eItemError_CantBeUnEquiped;
            if (At(bag, slot) != null)
                return EServerErrorType.eItemError_BagFull; // the client always picks an empty slot

            Character.Equipment.Remove(worn);
            worn.Slot = (int)slot;
            bag.Add(worn);
            _s.Log.LogInformation("   unequipped part {Part} into bag {Bag} slot {Slot}", part, bagId, slot);
            return EServerErrorType.eSuccess;
        }

        /// <summary>Gear changes what the character's attacks and armor are worth.</summary>
        private void GearChanged()
        {
            _s.SaveAccount();
            _s.Battle.SendAttributes();
        }

        /// <summary>The stash offers up to five tabs (StashManager: MaxIDX 5).</summary>
        public const int MaxStashTabs = 5;

        /// <summary>
        /// Unlocks the next stash tab. Its price (stash.basic: 10, 20, 30, 40) is in crystals, the
        /// real-money currency, which costs nothing here.
        /// </summary>
        public void BuyStashTab()
        {
            if (Character.StashTabs >= MaxStashTabs)
            {
                _s.Send(GameResponses.buyStashAck(Character.StashTabs, Proto.Error(EServerErrorType.eUnknownError)));
                return;
            }
            Character.StashTabs++;
            _s.SaveAccount();
            _s.Log.LogInformation("   stash has {Tabs} tabs now", Character.StashTabs);
            _s.Send(GameResponses.buyStashAck(Character.StashTabs, Proto.Error(EServerErrorType.eSuccess)));
        }

        // ---- moving, deleting ---------------------------------------------------------------------

        /// <summary>Moves an item between bag and stash slots, swapping with what is there.</summary>
        public void Swap(byte srcBag, uint srcSlot, byte dstBag, uint dstSlot)
        {
            if (srcBag == Views.BagEquip && dstBag != Views.BagEquip)
            {
                Unequip((byte)srcSlot, dstBag, dstSlot);
                return;
            }
            if (dstBag == Views.BagEquip && srcBag != Views.BagEquip)
            {
                Equip((byte)dstSlot, srcBag, srcSlot);
                return;
            }

            List<ItemData> from = Bag(srcBag), to = Bag(dstBag);
            ItemData moving = from == null ? null : At(from, srcSlot);
            if (moving == null || to == null || srcBag == Views.BagEquip || dstSlot >= Capacity(dstBag)
                || (srcBag == dstBag && srcSlot == dstSlot))
            {
                _s.Send(GameResponses.SwapItemFailed(_s.Err((int)EServerErrorType.eItemError_CantBeSwapped)));
                return;
            }

            ItemData other = At(to, dstSlot);

            // Dropped onto the same stackable: the stacks join as far as there is room.
            int stackSize = Props(moving)?.StackSize ?? 1;
            if (other != null && stackSize > 1 && SameKind(moving, other) && other.Count < stackSize)
            {
                int moved = Math.Min(moving.Count, stackSize - other.Count);
                other.Count += moved;
                moving.Count -= moved;
                _s.Send(GameResponses.NotifyItemUpdate(dstBag, Views.Item(other)));
                if (moving.Count <= 0)
                {
                    from.Remove(moving);
                    _s.Send(GameResponses.NotifyItemDestroy(srcBag, srcSlot));
                }
                else
                {
                    _s.Send(GameResponses.NotifyItemUpdate(srcBag, Views.Item(moving)));
                }
                _s.SaveAccount();
                _s.Log.LogInformation("   stacked {Count} x {Item} onto bag {Bag} slot {Slot}", moved, moving.ItemId, dstBag, dstSlot);
                return;
            }

            from.Remove(moving);
            if (other != null)
            {
                to.Remove(other);
                other.Slot = (int)srcSlot;
                from.Add(other);
            }
            moving.Slot = (int)dstSlot;
            to.Add(moving);
            _s.SaveAccount();
            _s.Send(GameResponses.NotifyItemSwap(srcBag, srcSlot, dstBag, dstSlot));
        }

        /// <summary>
        /// Throws an item away. The April build only ever sends DelItem from its Transmute button
        /// (instant sell: the tooltip already showed the client's half-value formula), so for that
        /// era the deletion pays karma shards; other eras keep DelItem as a plain discard.
        /// </summary>
        public void Delete(byte bagId, uint slot)
        {
            List<ItemData> bag = Bag(bagId);
            ItemData item = bag == null ? null : At(bag, slot);
            if (item == null)
            {
                _s.Send(GameResponses.DelItemFailed(_s.Err((int)EServerErrorType.eItemError_NoExist)));
                return;
            }
            int paid = _s.Dialect == ClientDialect.Apr2013 ? _s.Shop.TransmuteValue(item) : 0;
            bag.Remove(item);
            if (paid > 0)
                _s.Account.Karma += paid;
            _s.Log.LogInformation(paid > 0
                ? "   transmuted {Item} into {Value} karma shards (bag {Bag} slot {Slot})"
                : "   deleted item {Item} from bag {Bag} slot {Slot}", item.ItemId, paid, bagId, slot);
            if (bagId == Views.BagEquip)
                GearChanged();
            else
                _s.SaveAccount();
            _s.Send(GameResponses.NotifyItemDestroy(bagId, slot));
            if (paid > 0)
                _s.SendMoney();
        }

        /// <summary>Takes <paramref name="count"/> of a stack away; the slot empties when nothing is left.</summary>
        public void Consume(byte bagId, ItemData item, int count = 1)
        {
            item.Count -= count;
            if (item.Count > 0)
            {
                _s.Send(GameResponses.NotifyItemUpdate(bagId, Views.Item(item)));
                return;
            }
            Bag(bagId).Remove(item);
            _s.Send(GameResponses.NotifyItemDestroy(bagId, (uint)item.Slot));
        }

        // ---- adding ------------------------------------------------------------------------------

        /// <summary>
        /// Puts an item into the bag: onto a matching stack while it has room (Accumulation), else
        /// into the first free slot. Tells the client about every slot that changed. Returns false
        /// (and changes nothing) when it does not fit.
        /// </summary>
        public bool AddToBag(ItemData item)
        {
            if (!CanAdd(item))
                return false;

            int left = Math.Max(1, item.Count);
            int stackSize = Props(item)?.StackSize ?? 1;
            if (stackSize > 1)
            {
                foreach (ItemData stack in Character.Bag.Where(i => SameKind(i, item) && i.Count < stackSize).OrderBy(i => i.Slot))
                {
                    int moved = Math.Min(left, stackSize - stack.Count);
                    stack.Count += moved;
                    left -= moved;
                    _s.Send(GameResponses.NotifyItemUpdate(Views.BagBase, Views.Item(stack)));
                    if (left == 0)
                        return true;
                }
            }
            while (left > 0)
            {
                var copy = Clone(item, FreeSlot(), Math.Min(left, stackSize));
                left -= copy.Count;
                Character.Bag.Add(copy);
                _s.Send(GameResponses.NotifyItemUpdate(Views.BagBase, Views.Item(copy)));
            }
            return true;
        }

        /// <summary>Whether <see cref="AddToBag"/> would succeed.</summary>
        public bool CanAdd(ItemData item)
        {
            int stackSize = Props(item)?.StackSize ?? 1;
            int room = Character.Bag.Where(i => SameKind(i, item)).Sum(i => Math.Max(0, stackSize - i.Count))
                       + (BagSlots - Character.Bag.Count(i => i.Slot < BagSlots)) * stackSize;
            return room >= Math.Max(1, item.Count);
        }

        public int FreeSlot()
        {
            for (int slot = 0; slot < BagSlots; slot++)
            {
                if (Character.Bag.All(i => i.Slot != slot))
                    return slot;
            }
            return -1;
        }

        /// <summary>
        /// Stackables (food, potions, materials) stack whatever level they dropped at and whatever
        /// model number they came with (shop, day reward and loot use different ones).
        /// </summary>
        private static bool SameKind(ItemData a, ItemData b) =>
            a.ItemId == b.ItemId && a.Gem == b.Gem && a.Enchant == b.Enchant && a.Element == b.Element;

        private static ItemData Clone(ItemData item, int slot, int count) => new ItemData
        {
            Slot = slot,
            ItemId = item.ItemId,
            PrefabId = item.PrefabId,
            Level = item.Level,
            Gem = item.Gem,
            Enchant = item.Enchant,
            Element = item.Element,
            Count = count,
            Serial = item.Serial,
        };

        // ---- using consumables --------------------------------------------------------------------

        private static readonly Regex RecoverAmount = new Regex(@"(\d+)\s+(Health|Energy)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Eats or drinks a consumable. Its buff (ConsumableItem.BuffID -> Ability.Status) shows the
        /// icon and effect on the client; the recovery itself is the server's: potions heal the
        /// amount named in their description at once, and the attribute change carries it to the client.
        /// </summary>
        public void Use(byte bagId, uint slot)
        {
            List<ItemData> bag = Bag(bagId);
            ItemData item = bag == null ? null : At(bag, slot);
            if (item == null || !Data.ConsumableEffects.TryGetValue(item.ItemId, out GameData.ConsumableRow consumable))
            {
                _s.Log.LogInformation("   use item rejected: bag {Bag} slot {Slot}", bagId, slot);
                return;
            }

            GameData.BuffRow buff = Data.Buffs.GetValueOrDefault(consumable.BuffId);
            var changes = new Dictionary<int, int>();
            Match amount = RecoverAmount.Match(buff?.Description ?? "");
            if (amount.Success && !_s.IsDead)
            {
                _s.ApplyRegeneration();
                PlayerStats stats = _s.Stats;
                int value = int.Parse(amount.Groups[1].Value);
                if (amount.Groups[2].Value.Equals("Health", StringComparison.OrdinalIgnoreCase))
                {
                    int healed = Math.Min(value, stats.MaxHp - _s.CurrentHp);
                    _s.CurrentHp += healed;
                    changes[EAttributeType.ATTR_CurHP] = healed;
                }
                else
                {
                    int restored = Math.Min(value, stats.MaxMp - _s.CurrentMp);
                    _s.CurrentMp += restored;
                    changes[EAttributeType.ATTR_CurMP] = restored;
                }
            }

            _s.Log.LogInformation("   used {Item} (buff {Buff})", consumable.Name, buff?.Name ?? "-");
            Consume(bagId, item);
            _s.SaveAccount();
            if (buff != null)
                _s.Battle.ApplyBuff(buff, changes);
            if (buff != null && buff.Duration > 0 && !amount.Success)
                _s.Battle.SendAttributes(); // food and drink change the numbers the client shows
        }
    }
}
