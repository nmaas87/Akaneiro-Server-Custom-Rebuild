using System.Collections.Generic;

namespace Akaneiro.Server.Data
{
    /// <summary>Persistent account (one per Steam ID / login name).</summary>
    public sealed class AccountData
    {
        public int Id { get; set; }
        public string Account { get; set; } = "";
        public int Karma { get; set; }
        public int Crystals { get; set; }
        public int CharSlots { get; set; } = 4;

        /// <summary>Days in a row the daily reward was collected, and the (UTC) day it was last collected (yyyymmdd).</summary>
        public int DayRewardStreak { get; set; }
        public int DayRewardLastDate { get; set; }

        /// <summary>When the village well started filling again (unix seconds); 0 = never used.</summary>
        public long WellBeginUnix { get; set; }
        public long CreatedUnix { get; set; }
        public int LastCharacterId { get; set; }
        public List<CharacterData> Characters { get; set; } = new List<CharacterData>();
    }

    /// <summary>Persistent character.</summary>
    public sealed class CharacterData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Sex { get; set; }
        public int Style { get; set; }
        public Dictionary<int, int> AvatarTraits { get; set; } = new Dictionary<int, int>();
        public int Level { get; set; } = 1;
        public ulong Exp { get; set; }
        public bool TutorialDone { get; set; }
        public long CreatedUnix { get; set; }

        /// <summary>Set once the discipline starter gear was handed out (also covers characters created before it existed).</summary>
        public bool StarterGearGranted { get; set; }

        /// <summary>Set once the three discipline starting abilities were handed out.</summary>
        public bool StarterAbilitiesGranted { get; set; }

        /// <summary>Equipped items; <see cref="ItemData.Slot"/> is the equipment part (0 = head, 1 = neck, 2 = chest, 3 = cloak, 4 = ring, 6/7 = weapons, 8 = legs).</summary>
        public List<ItemData> Equipment { get; set; } = new List<ItemData>();

        /// <summary>Items in the inventory bag; <see cref="ItemData.Slot"/> is the bag slot.</summary>
        public List<ItemData> Bag { get; set; } = new List<ItemData>();

        /// <summary>Items in the village stash; <see cref="ItemData.Slot"/> counts across all tabs.</summary>
        public List<ItemData> Stash { get; set; } = new List<ItemData>();

        /// <summary>Stash tabs bought so far (the first one is free).</summary>
        public int StashTabs { get; set; } = 1;

        /// <summary>Learned abilities (ability ids from the client's Ability table).</summary>
        public List<AbilityData> Abilities { get; set; } = new List<AbilityData>();

        /// <summary>Ability shortcut slots. Group 0 = slots 1-3, group 1 = slots 4-6.</summary>
        public List<ShortcutData> Shortcuts { get; set; } = new List<ShortcutData>();

        /// <summary>Missions the character has finished, by mission id.</summary>
        public List<int> CompletedMissions { get; set; } = new List<int>();

        /// <summary>Mission areas bought with karma by the 2013-era clients (unLockAreaReq);
        /// the 2014 progression never fills this (it chains maps through completed missions).</summary>
        public List<int> UnlockedAreas { get; set; } = new List<int>();

        /// <summary>Experience of each spirit kind (PetPrice id / 100), kept across rentals.</summary>
        public Dictionary<int, ulong> SpiritExp { get; set; } = new Dictionary<int, ulong>();

        /// <summary>When each mission map (base id) can be played again, in unix seconds.</summary>
        public Dictionary<int, long> MissionReadyUnix { get; set; } = new Dictionary<int, long>();

        /// <summary>When each mission map was finished, for the growing cooldown (unix seconds, last day only).</summary>
        public Dictionary<int, List<long>> MissionRuns { get; set; } = new Dictionary<int, List<long>>();

        /// <summary>
        /// Talent points put into the disciplines on level up (one per level from level 2; the
        /// discipline picked at creation counts on top and is not part of these).
        /// </summary>
        public int TalentProwess { get; set; }
        public int TalentFortitude { get; set; }
        public int TalentCunning { get; set; }

        /// <summary>Weapon and armor masteries: EMasteryType (1, 2, 4, ... 32) -> level.</summary>
        public Dictionary<int, int> Masteries { get; set; } = new Dictionary<int, int>();

        /// <summary>Set once the two masteries of the character's discipline were handed out.</summary>
        public bool StarterMasteriesGranted { get; set; }

        /// <summary>Set once the "Kickstarter Exclusives" thank-you mail was put into the mailbox.</summary>
        public bool KickstarterMailGranted { get; set; }

        /// <summary>Set once the "Complete Rare Catalog" letter (every unique + special) was sent.</summary>
        public bool CatalogMailGranted { get; set; }

        /// <summary>
        /// Set once the "Starter Pack Gifts" mail was put into the mailbox at creation. Unlike the
        /// Kickstarter letter this is granted on creation only - characters made before it existed
        /// never receive it.
        /// </summary>
        public bool StarterPackMailGranted { get; set; }

        /// <summary>The mastery level being trained (EquipMastery id) and when it is done; null when none.</summary>
        public MasteryTraining Training { get; set; }

        /// <summary>Spirit helpers rented from the spirit trainer.</summary>
        public List<SpiritRental> Spirits { get; set; } = new List<SpiritRental>();

        /// <summary>What the village vendor offers until the stock changes (every 12 hours).</summary>
        public ShopStock Shop { get; set; }

        /// <summary>Mails in the character's mailbox, oldest first.</summary>
        public List<MailData> Mail { get; set; } = new List<MailData>();
    }

    /// <summary>
    /// One mail in a mailbox. <see cref="TimeoutUnix"/> 0 means "Forever" (the client's
    /// MailSystem shows exactly that for a timeout of 0). Attached items keep their index in
    /// <see cref="Items"/> in <see cref="ItemData.Slot"/>; the client addresses them by that
    /// index when claiming (GetMailItem) and renumbers the rest itself.
    /// </summary>
    public sealed class MailData
    {
        public int Id { get; set; }
        public string SenderName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public int Karma { get; set; }
        public int Crystal { get; set; }
        public long TimeUnix { get; set; }
        public long TimeoutUnix { get; set; }

        /// <summary>0 = unread, 1 = read (EMailState).</summary>
        public int State { get; set; }

        public List<ItemData> Items { get; set; } = new List<ItemData>();
    }

    public sealed class SpiritRental
    {
        /// <summary>PetPrice id of the spirit's level (e.g. 1001 = Inugami level 1).</summary>
        public int PetId { get; set; }
        public long BeginUnix { get; set; }
        public int Seconds { get; set; }
        public bool Chosen { get; set; }
    }

    public sealed class MasteryTraining
    {
        public int MasteryId { get; set; }
        public int MasteryType { get; set; }
        public long ReadyUnix { get; set; }
    }

    public sealed class ShopStock
    {
        /// <summary>Unix time the stock was rolled; it is replaced 12 hours later.</summary>
        public long RolledUnix { get; set; }

        /// <summary>Character level the stock was rolled for.</summary>
        public int Level { get; set; }

        public List<ShopOffer> Items { get; set; } = new List<ShopOffer>();
        public List<ShopOffer> RareItems { get; set; } = new List<ShopOffer>();
    }

    public sealed class ShopOffer
    {
        public int Uuid { get; set; }
        public ItemData Item { get; set; }
        public int Price { get; set; }

        /// <summary>1 = karma, 2 = crystals (EMoneyType).</summary>
        public int MoneyType { get; set; } = 1;
    }

    public sealed class AbilityData
    {
        public int Id { get; set; }
        public int Level { get; set; } = 1;
        public int Exp { get; set; }
    }

    public sealed class ShortcutData
    {
        public int Group { get; set; }
        public int Index { get; set; }
        public int AbilityId { get; set; }
    }

    /// <summary>
    /// An item instance. ItemId/PrefabId identify the item in the client's item tables
    /// (ItemDeployInfo.GetItemObject); Gem/Enchant/Element are 0 when the item has none.
    /// </summary>
    public sealed class ItemData
    {
        public int Slot { get; set; }
        public int ItemId { get; set; }
        public int PrefabId { get; set; }
        public int Level { get; set; } = 1;
        public int Gem { get; set; }
        public int Enchant { get; set; }
        public int Element { get; set; }
        public int Count { get; set; } = 1;
        public int Serial { get; set; }
    }

    /// <summary>Global counters, stored next to the accounts.</summary>
    public sealed class ServerState
    {
        public int NextAccountId { get; set; } = 1;
        public int NextCharacterId { get; set; } = 1;
        public int NextMailId { get; set; } = 1;
    }
}
