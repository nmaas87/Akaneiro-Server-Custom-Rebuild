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

        private readonly Session _s;

        public MailService(Session session) => _s = session;

        private CharacterData Character => _s.Character;

        // ---- granting ---------------------------------------------------------------------------

        /// <summary>
        /// Puts the "Kickstarter Exclusives" thank-you mail (with the four Kickstarter items and no
        /// expiry) into the character's mailbox, once per character. Called at creation and again on
        /// selection so characters made before this existed still receive it.
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

        /// <summary>"Accept all": every attachment, and any money the mail carries.</summary>
        public void GetAll(int mailID)
        {
            MailData mail = Find(mailID);
            if (mail == null)
            {
                _s.Send(GameResponses.GetMailAllItemsResult(mailID,
                    Proto.Error(EServerErrorType.eMailError_MailNotExist)));
                return;
            }
            if (mail.Items.Any(i => !_s.Inventory.CanAdd(i)))
            {
                _s.Send(GameResponses.GetMailAllItemsResult(mailID,
                    Proto.Error(EServerErrorType.eItemError_BagFull)));
                return;
            }

            foreach (ItemData item in mail.Items)
                _s.Inventory.AddToBag(item);
            mail.Items.Clear();
            ClaimMoney(mail);
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
