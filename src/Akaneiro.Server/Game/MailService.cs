using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// The mailbox. The client polls it (UI_Hud_OtherBTN_Manager asks GetPlayerMail every 60 s
    /// and again when the mail button is tapped) and shows the unread count on the HUD, so a
    /// stored mail needs no push packet. A mail whose <see cref="MailData.TimeoutUnix"/> is 0
    /// reads "Forever" (MailSystem.UpdateMailExpireTime).
    /// </summary>
    public sealed class MailService
    {
        public const string KickstarterSender = "Spicy Horse";
        public const string KickstarterTitle = "Kickstarter Exclusives";
        public const string KickstarterText =
            "The Order of Akane thanks you for your contribution through Kickstarter";

        public const string StarterPackTitle = "Starter Pack Gifts";
        public const string StarterPackText =
            "The Order of Akane thanks you for your contribution through Steam Early Access";

        public const int StarterPackKarma = 25000;
        public const int StarterPackCrystal = 60;

        /// <summary>
        /// The Kickstarter unique items. The uniqueItem.rare rows (9000/9005/9010/9015) are
        /// catalog entries, not items: the client resolves every SItemInfo by its item id in
        /// Props.General (ItemDeployInfo.SetTypeID), so we must send the row's TypeID with the
        /// row's prefab/gem/enchant/level - 2010 cloak, 1002 axe, 3001 ring, 2001 helm. The
        /// marketing names ("Clever Huntsman's", "Akane Signet", ...) come from the enchant
        /// prefix combined with the type name; unknown ids render as null refs in the mailbox
        /// and as iconless "unknown" items in the bag.
        /// </summary>
        private static readonly (int ItemId, int PrefabId, int Level, int Gem, int Enchant)[] KickstarterRewards =
        {
            (2010, 101, 4, 905, 800),  // Little Red's Hooded Cloak: regen below 8% HP
            (1002, 101, 6, 1005, 800), // The Clever Huntsman's Axe: speed on full energy
            (3001, 101, 8, 1105, 800), // Engraved Akane Signet Ring: raises the lowest stat
            (2001, 1002, 10, 805, 821) // Wolf's Granny Cap (GWOLF): stealth
        };

        /// <summary>
        /// The Steam Early Access starter pack: nine foods (3 each, every one a Props.General
        /// consumable row), the 20-scroll revive pack, and the two Steam gift items - Steam
        /// Necklace and Raging Steam Blast Katana - from uniqueItem.rare rows 9500/9501, again
        /// as TypeID+Prefab+Gem+Enchant+Element+Level rather than the (unresolvable) row numbers.
        /// The foods' and the scroll's "prefab" is the client's icon index 1, not a model variant:
        /// index 0 is the world model, which carries no icon texture, so the mail panel would show
        /// a blank tile (ItemType.IsModelIndex).
        /// </summary>
        private static readonly (int Id, int Prefab, int Level, int Gem, int Enchant, int Element, int Count)[] StarterPackRewards =
        {
            (20009, 1, 1, 0, 0, 0, 3), // Sea Urchin Sushi  (+POWER)
            (20010, 1, 1, 0, 0, 0, 3), // Kagami Mochi      (+SKILL)
            (20014, 1, 1, 0, 0, 0, 3), // Dorayaki          (+DEFENSE)
            (20015, 1, 1, 0, 0, 0, 3), // Fugu Tempura      (+20% move speed)
            (20016, 1, 1, 0, 0, 0, 3), // Sukiyaki          (+25% crit chance)
            (20017, 1, 1, 0, 0, 0, 3), // Shochu            (+80% monster XP)
            (20018, 1, 1, 0, 0, 0, 3), // Awamori           (+loot quality)
            (20019, 1, 1, 0, 0, 0, 3), // Hojicha Tea       (+energy regen)
            (20020, 1, 1, 0, 0, 0, 3), // Chrysanthemum Tea (+HP regen)
            (20103, 1, 1, 0, 0, 0, 1), // 20 Shinigami Scrolls
            (3002, 6, 1, 101, 0, 0, 1),    // Steam Necklace (9500: Necklace/ACC06/Steam Gem)
            (1001, 9, 5, 101, 103, 303, 1) // Raging Steam Blast Katana (9501: WPN09/Raging III/Blast III)
        };

        private readonly Session _s;

        public MailService(Session session) => _s = session;

        private CharacterData Character => _s.Character;

        // ---- granting ---------------------------------------------------------------------------

        /// <summary>
        /// Puts the "Kickstarter Exclusives" thank-you mail (with the four Kickstarter items and no
        /// expiry) into the character's mailbox, once per character. Granted at character creation
        /// only, like the starter pack letter.
        /// </summary>
        public bool GrantKickstarterMail(CharacterData character)
        {
            if (character.KickstarterMailGranted)
                return false;

            var mail = NewMail(character, KickstarterSender, KickstarterTitle, KickstarterText);
            foreach (var (itemId, prefabId, level, gem, enchant) in KickstarterRewards)
            {
                // Slot is the item's index inside the mail; the client addresses attachments by it.
                mail.Items.Add(new ItemData
                {
                    Slot = mail.Items.Count,
                    ItemId = itemId,
                    PrefabId = prefabId,
                    Level = level,
                    Gem = gem,
                    Enchant = enchant,
                    Count = 1,
                });
            }
            character.Mail.Add(mail);
            character.KickstarterMailGranted = true;
            _s.Log.LogInformation("   mailed '{Title}' with {Count} items to #{Id} '{Name}'",
                mail.Title, mail.Items.Count, character.Id, character.Name);
            return true;
        }

        /// <summary>
        /// Puts the "Starter Pack Gifts" mail (karma, crystals, foods and the two Steam gift
        /// items, no expiry) into the character's mailbox. Granted at character creation only:
        /// characters that existed before this feature never receive it.
        /// </summary>
        public bool GrantStarterPackMail(CharacterData character)
        {
            if (character.StarterPackMailGranted)
                return false;

            var mail = NewMail(character, KickstarterSender, StarterPackTitle, StarterPackText);
            mail.Karma = StarterPackKarma;
            mail.Crystal = StarterPackCrystal;
            foreach (var (id, prefab, level, gem, enchant, element, count) in StarterPackRewards)
            {
                mail.Items.Add(new ItemData
                {
                    Slot = mail.Items.Count,
                    ItemId = id,
                    PrefabId = prefab,
                    Level = level,
                    Gem = gem,
                    Enchant = enchant,
                    Element = element,
                    Count = count,
                });
            }
            character.Mail.Add(mail);
            character.StarterPackMailGranted = true;
            _s.Log.LogInformation("   mailed '{Title}' with {Count} items to #{Id} '{Name}'",
                mail.Title, mail.Items.Count, character.Id, character.Name);
            return true;
        }

        /// <summary>
        /// The "Complete Rare Catalog" letter: every named unique in the original catalog
        /// (uniqueItem.rare, including the rows its shop never listed) plus the pledge and event
        /// specials, each with its catalogued parts, at the level its row names. One letter per
        /// character, granted at creation like the welcome letters. The bag never has to hold it:
        /// it just sits in the mailbox until it is claimed.
        /// </summary>
        public bool GrantCatalogMail(CharacterData character)
        {
            if (character.CatalogMailGranted)
                return false;

            var mail = NewMail(character, KickstarterSender, "The Complete Rare Catalog",
                "Every named unique the old catalog ever listed, and the pledged gear that never "
                + "made it onto a shelf - each with its own enchantments, at the level its entry "
                + "names. They are not for sale anywhere: consider them yours. - Goto");

            var seen = new HashSet<(int Item, int Prefab, int Enchant, int Gem, int Element)>();
            foreach (GameData.UniqueShopRow row in _s.World.Data.UniqueShop.Values.OrderBy(r => r.Id))
            {
                if (!seen.Add((row.BaseId, row.PrefabId, row.Enchant, row.Gem, row.Element)))
                    continue;
                mail.Items.Add(new ItemData
                {
                    Slot = mail.Items.Count,
                    ItemId = row.BaseId,
                    PrefabId = row.PrefabId,
                    Level = Math.Max(1, row.RequiredLevel),
                    Gem = row.Gem,
                    Element = row.Element,
                    Enchant = row.Enchant,
                    Count = 1,
                });
            }
            foreach (GameData.ItemProps props in _s.World.Data.SpecialItems.Values.OrderBy(i => i.Id))
            {
                if (!seen.Add((props.Id, 0, 0, 0, 0)))
                    continue;
                mail.Items.Add(new ItemData
                {
                    Slot = mail.Items.Count,
                    ItemId = props.Id,
                    Level = Math.Max(1, props.LevelRequirement),
                    PrefabId = Loot.PrefabFor(_s.World.Data, props.Id, Math.Max(1, props.LevelRequirement)),
                    Count = 1,
                });
            }

            character.Mail.Add(mail);
            character.CatalogMailGranted = true;
            _s.Log.LogInformation("   mailed '{Title}' with {Count} items to #{Id} '{Name}'",
                mail.Title, mail.Items.Count, character.Id, character.Name);
            return true;
        }

        private MailData NewMail(CharacterData character, string sender, string title, string content) =>
            new MailData
            {
                Id = _s.World.Accounts.NewMailId(),
                SenderName = sender,
                Title = title,
                Content = content,
                TimeUnix = new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds(),
                TimeoutUnix = 0, // "Forever"
                State = EMailState.eMailState_Unread,
            };

        // ---- answering the client -----------------------------------------------------------------

        /// <summary>The whole mailbox, oldest first (On_GetPlayerMailResult fills list and badge).</summary>
        public void SendMailbox()
        {
            var list = new vectorMailInfos();
            foreach (MailData mail in Character.Mail.OrderBy(m => m.Id))
                list.Add(View(mail));
            _s.Send(GameResponses.GetPlayerMailResult(list));
        }

        /// <summary>Opening a mail only marks it read; the client expects no answer to this.</summary>
        public void Open(int mailID)
        {
            MailData mail = Find(mailID);
            if (mail != null)
                mail.State = EMailState.eMailState_Read;
        }

        /// <summary>One attachment into the bag; <paramref name="itemSlotID"/> is the mail index.</summary>
        public void GetItem(int mailID, int itemSlotID)
        {
            MailData mail = Find(mailID);
            ItemData item = mail?.Items.FirstOrDefault(i => i.Slot == itemSlotID);
            if (item == null)
            {
                _s.Send(GameResponses.GetMailItemResult(
                    Proto.Error(EServerErrorType.eMailError_MailItemSlotError), mailID, itemSlotID));
                return;
            }
            if (!_s.Inventory.CanAdd(item))
            {
                _s.Send(GameResponses.GetMailItemResult(
                    Proto.Error(EServerErrorType.eItemError_BagFull), mailID, itemSlotID));
                return;
            }

            mail.Items.Remove(item);
            CompactSlots(mail);       // the client shifts its remaining icons down the same way
            _s.Inventory.AddToBag(item); // announces the changed bag slots itself
            _s.Send(GameResponses.GetMailItemResult(Proto.Error(EServerErrorType.eSuccess), mailID, itemSlotID));
            _s.Log.LogInformation("   mail #{Mail} item {Item} claimed into the bag", mailID, item.ItemId);
        }

        /// <summary>
        /// "Accept all": every attachment, and any money the mail carries. Items are claimed one
        /// by one until the bag is full: whatever does not fit (and the money) stays on the letter
        /// instead of being destroyed by an all-or-nothing guess at the bag's room.
        /// </summary>
        public void GetAll(int mailID)
        {
            MailData mail = Find(mailID);
            if (mail == null)
            {
                _s.Send(GameResponses.GetMailAllItemsResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailNotExist)));
                return;
            }

            int claimed = 0;
            foreach (ItemData item in mail.Items.ToList())
            {
                if (!_s.Inventory.AddToBag(item)) // changes nothing when it does not fit
                    break;
                mail.Items.Remove(item);
                claimed++;
            }
            CompactSlots(mail);

            if (mail.Items.Count > 0)
            {
                _s.Log.LogInformation("   mail #{Mail}: bag full after {Count} items, {Left} stay on the letter",
                    mailID, claimed, mail.Items.Count);
                _s.Send(GameResponses.GetMailAllItemsResult(mailID,
                    Proto.Error(EServerErrorType.eItemError_BagFull)));
                return;
            }

            if (mail.Karma > 0 || mail.Crystal > 0)
            {
                // "Accept all" also banks the money, and the client is told about the wallet.
                _s.Account.Karma += mail.Karma;
                _s.Account.Crystals += mail.Crystal;
                ClaimMoney(mail);
                _s.SendMoney();
            }
            _s.Send(GameResponses.GetMailAllItemsResult(mailID, Proto.Error(EServerErrorType.eSuccess)));
        }

        public void GetKarma(int mailID)
        {
            MailData mail = Find(mailID);
            if (mail == null)
            {
                _s.Send(GameResponses.GetMailKarmaResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailNotExist)));
                return;
            }
            if (mail.Karma <= 0)
            {
                _s.Send(GameResponses.GetMailKarmaResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailParamError)));
                return;
            }
            _s.Account.Karma += mail.Karma;
            ClaimMoney(mail);
            _s.Send(GameResponses.GetMailKarmaResult(mailID, Proto.Error(EServerErrorType.eSuccess)));
            _s.SendMoney();
        }

        public void GetCrystal(int mailID)
        {
            MailData mail = Find(mailID);
            if (mail == null)
            {
                _s.Send(GameResponses.GetMailCrystalResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailNotExist)));
                return;
            }
            if (mail.Crystal <= 0)
            {
                _s.Send(GameResponses.GetMailCrystalResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailParamError)));
                return;
            }
            _s.Account.Crystals += mail.Crystal;
            ClaimMoney(mail);
            _s.Send(GameResponses.GetMailCrystalResult(mailID, Proto.Error(EServerErrorType.eSuccess)));
            _s.SendMoney();
        }

        public void Delete(int mailID)
        {
            bool removed = Character.Mail.RemoveAll(m => m.Id == mailID) > 0;
            var ids = new vectorInt();
            if (removed)
                ids.Add(mailID);
            _s.Send(GameResponses.DeleteMailResult(
                Proto.Error(removed ? EServerErrorType.eSuccess : EServerErrorType.eMailError_MailNotExist),
                ids));
        }

        // ---- internals ---------------------------------------------------------------------------

        private MailData Find(int mailID) => Character.Mail.FirstOrDefault(m => m.Id == mailID);

        /// <summary>A money attachment is spent on taking it; the letter itself stays until deleted.</summary>
        private static void ClaimMoney(MailData mail)
        {
            mail.Karma = 0;
            mail.Crystal = 0;
        }

        /// <summary>Mail attachment indexes run 0..n; after one is claimed the client renumbers the
        /// rest, so the stored list follows the same rule.</summary>
        private static void CompactSlots(MailData mail)
        {
            int slot = 0;
            foreach (ItemData item in mail.Items)
                item.Slot = slot++;
        }

        /// <summary>A stored mail as the client's SMailInfo.</summary>
        private static SMailInfo View(MailData mail)
        {
            var info = Proto.New<SMailInfo>();
            info.id = mail.Id;
            info.type = new EMailType(EMailType.eMailType_System);
            info.state = new EMailState(mail.State);
            info.senderName = mail.SenderName;
            info.recvName = "";
            info.title = mail.Title;
            info.content = mail.Content;
            info.karma = mail.Karma;
            info.crystal = mail.Crystal;
            info.time = mail.TimeUnix;
            info.timeout = mail.TimeoutUnix; // 0 => the client prints "Forever"
            foreach (ItemData item in mail.Items)
                info.itemVec.Add(Views.Item(item));
            info.reason = new EMailReason(EMailReason.eMailReason_OK);
            return info;
        }
    }
}
