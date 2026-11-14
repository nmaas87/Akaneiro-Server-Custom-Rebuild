using System;
using System.Collections.Generic;
using Akaneiro.Server.Data;
using Akaneiro.Server.Net;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// State of one connected client. Incoming packets are routed to the Game or Battle handler
    /// depending on their method id (both protocols share the same connection).
    /// </summary>
    public sealed class Session
    {
        /// <summary>The client hardcodes object id 1 for its own avatar (see CS_SceneInfo.On_UseFightSkillResult).</summary>
        public const int PlayerObjectId = 1;

        private readonly IPacketSink _connection;
        private readonly GameHandler _game;
        private readonly BattleHandler _battle;

        /// <summary>The battle handler, for the few game-protocol requests that affect the fight.</summary>
        public BattleHandler Battle => _battle;

        /// <summary>Scene triggers (gates, barriers, ambushes) for the player's position.</summary>
        public TriggerRunner Triggers { get; }

        /// <summary>Bag, equipment and stash of the character.</summary>
        public Inventory Inventory { get; }

        /// <summary>The village vendors.</summary>
        public Shop Shop { get; }

        /// <summary>Abilities and masteries.</summary>
        public Trainer Trainer { get; }
        public KillChain KillChain { get; }

        /// <summary>The village well.</summary>
        public Well Well { get; }

        /// <summary>Rented spirit helpers.</summary>
        public Spirits Spirits { get; }

        /// <summary>The craftsman's upgrades.</summary>
        public Crafting Crafting { get; }

        /// <summary>The character's mailbox.</summary>
        public MailService Mail { get; }

        public World World { get; }
        public ILogger Log { get; }

        public AccountData Account { get; set; }
        public CharacterData Character { get; set; }

        /// <summary>Name of the character last selected in this session (reconnect handshake text).</summary>
        public string LastCharacterName { get; set; } = "";

        /// <summary>The account string this session is reconnecting against (GameHandler.UserLogin
        /// found a live owner and asked; answered by GameHandler.UserReconnect).</summary>
        public string PendingReconnect { get; set; }

        /// <summary>Set when a newer session took this account away: the stale session must not
        /// save (its copy predates the takeover) and its connection is closed.</summary>
        public bool Superseded { get; private set; }

        /// <summary>The account key this session claimed ownership of, if any.</summary>
        public string AccountKey { get; private set; }

        /// <summary>Claim the account for this session (all callers hold <see cref="World.Sync"/>).</summary>
        public void ClaimAccount(string account)
        {
            AccountKey = account;
            World.ClaimAccount(account, this);
        }

        /// <summary>End this session's connection (its read loop ends, OnDisconnected runs).</summary>
        public void Disconnect()
        {
            _aiTimer?.Dispose();
            _aiTimer = null;
            _connection.Close();
        }

        /// <summary>Scene requested via EnterScene; becomes active on MapLoadOK.</summary>
        public string SceneName { get; set; } = "";
        public Vector3 Position { get; set; }

        /// <summary>Live values for the current session; reset when a scene is entered.</summary>
        public int CurrentHp { get; set; }
        public int CurrentMp { get; set; }
        public DateTime LastRegen { get; set; }

        /// <summary>Revivals used in the current mission; each one costs more karma than the last.</summary>
        public int Revivals { get; set; }

        public bool IsDead => Character != null && CurrentHp <= 0;

        /// <summary>
        /// Monsters cannot hurt the player until this moment: the revive shockwave's own status
        /// (Ability.Status 40018 "Revival Invulnerability" 7 s, 40019 with a scroll 12 s). The
        /// client shows the buff icon from the same packet and stops showing it at its table
        /// duration; the server mirrors it so a monster mid-swing cannot kill the risen player.
        /// </summary>
        public DateTime InvulnerableUntil = DateTime.MinValue;

        /// <summary>Food and drink buffs that are running (they change <see cref="Stats"/>).</summary>
        public List<ActiveBuff> ActiveBuffs { get; } = new List<ActiveBuff>();

        /// <summary>Per map visit: whether the friend reward request was answered, a day reward screen is up, and its first request was skipped (GameHandler.GetFriendReward).</summary>
        public bool FriendRewardAnswered { get; set; }
        public bool DayRewardOnScreen { get; set; }
        public bool FriendRewardSkippedOnce { get; set; }

        /// <summary>The character's current numbers: talents, gear, masteries and running buffs.</summary>
        public PlayerStats Stats
        {
            get
            {
                // Computing reads text tables; keep the result until something it depends on changes.
                DateTime now = World.Clock.UtcNow;
                int key = StatsKey(now);
                if (_stats == null || key != _statsKey)
                {
                    _stats = PlayerStats.Compute(Character, World.Data, ActiveBuffs, now);
                    _statsKey = key;
                }
                return _stats;
            }
        }

        private PlayerStats _stats;
        private int _statsKey;

        private int StatsKey(DateTime now)
        {
            var hash = new HashCode();
            hash.Add(Character);
            hash.Add(Character.Level);
            hash.Add(Character.Style);
            hash.Add(Character.TalentProwess);
            hash.Add(Character.TalentFortitude);
            hash.Add(Character.TalentCunning);
            foreach (ItemData item in Character.Equipment)
            {
                hash.Add(item.Slot);
                hash.Add(item.ItemId);
                hash.Add(item.PrefabId);
                hash.Add(item.Level);
                hash.Add(item.Gem);
                hash.Add(item.Enchant);
                hash.Add(item.Element);
            }
            foreach (var (type, level) in Character.Masteries)
            {
                hash.Add(type);
                hash.Add(level);
            }
            long unix = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
            foreach (SpiritRental spirit in Character.Spirits)
            {
                hash.Add(spirit.PetId);
                hash.Add(spirit.Chosen && unix < spirit.BeginUnix + spirit.Seconds);
            }
            foreach (ActiveBuff buff in ActiveBuffs)
            {
                hash.Add(buff.Buff.Id);
                hash.Add(buff.Until > now);
            }
            return hash.ToHashCode();
        }

        /// <summary>
        /// Mirrors the client's own regeneration (PlayerAttributionManager.RegenHPAndMP) so both
        /// sides agree on how much health and energy the character has.
        /// </summary>
        public void ApplyRegeneration()
        {
            if (Character == null)
                return;

            var now = World.Clock.UtcNow;
            int ticks = (int)((now - LastRegen).TotalSeconds * Views.RegenTicksPerSecond);
            if (ticks <= 0)
                return;

            LastRegen = LastRegen.AddSeconds(ticks / Views.RegenTicksPerSecond);
            PlayerStats stats = Stats;
            if (CurrentHp > 0)
                CurrentHp = Math.Min(stats.MaxHp, CurrentHp + ticks * stats.HpRegen);
            CurrentMp = Math.Min(stats.MaxMp, CurrentMp + ticks * stats.MpRegen);
        }

        /// <summary>Ability id -> time the ability becomes usable again.</summary>
        public Dictionary<int, DateTime> AbilityCooldowns { get; } = new Dictionary<int, DateTime>();

        /// <summary>Monsters of the scene the player is in (single player: one instance per session).</summary>
        public SceneInstance Scene { get; set; }

        /// <summary>The mission the player accepted, with its objectives.</summary>
        public MissionProgress Mission { get; set; }

        private readonly MonsterAi _ai;

        /// <summary>Monster behaviour of the current scene.</summary>
        public MonsterAi MonsterAi => _ai;
        private IDisposable _aiTimer;

        /// <summary>Starts the monster behaviour for the scene the player just entered.</summary>
        public void StartMonsterAi()
        {
            _aiTimer ??= World.Clock.Every(MonsterAi.TickInterval, () =>
            {
                lock (World.Sync)
                {
                    try
                    {
                        // The client regenerates continuously from the recovery attributes we
                        // publish; mirror that every tick so server health never lags its bar.
                        ApplyRegeneration();
                        _battle.ExpireBuffs();
                        _ai.Tick();
                        Trainer.FinishTraining();
                        Spirits.ExpireRentals();
                    }
                    catch (Exception ex)
                    {
                        Log.LogError(ex, "Monster AI failed");
                    }
                }
            });
        }

        public Session(World world, IPacketSink connection, ILogger log)
        {
            World = world;
            _connection = connection;
            Log = log;
            LastRegen = world.Clock.UtcNow;
            _game = new GameHandler(this);
            _battle = new BattleHandler(this);
            _ai = new MonsterAi(this);
            Triggers = new TriggerRunner(this);
            Inventory = new Inventory(this);
            Shop = new Shop(this);
            Trainer = new Trainer(this);
            KillChain = new KillChain(this);
            Well = new Well(this);
            Spirits = new Spirits(this);
            Crafting = new Crafting(this);
            Mail = new MailService(this);
        }

        public void HandlePacket(byte[] body)
        {
            if (body.Length < 4)
                return;

            int methodId = BitConverter.ToInt32(body, 0);
            lock (World.Sync)
            {
                try
                {
                    if (GameRequestDispatcher.Names.TryGetValue(methodId, out string name))
                    {
                        LogPacket("<-", name);
                        GameRequestDispatcher.Dispatch(new Packet(body), _game);
                    }
                    else if (BattleRequestDispatcher.Names.TryGetValue(methodId, out name))
                    {
                        LogPacket("<-", name);
                        BattleRequestDispatcher.Dispatch(new Packet(body), _battle);
                    }
                    else
                    {
                        Log.LogWarning("<- unknown method id 0x{Id:x8} ({Length} bytes)", methodId, body.Length);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Error while handling method id 0x{Id:x8}", methodId);
                }
            }
        }

        public void Send(Packet packet)
        {
            if (World.Options.LogPackets)
            {
                byte[] bytes = packet.GetByteStream();
                int methodId = BitConverter.ToInt32(bytes, 6);
                string name = GameResponses.Names.TryGetValue(methodId, out string n) ? n
                    : BattleResponses.Names.TryGetValue(methodId, out n) ? n
                    : $"0x{methodId:x8}";
                LogPacket("->", name);
            }
            _connection.Send(packet);
        }

        public void SaveAccount()
        {
            // A session whose account was taken over holds an outdated copy; saving it would
            // roll back everything the new session did.
            if (Superseded)
                return;
            if (Account != null)
                World.Accounts.Save(Account);
        }

        /// <summary>Shows the account's karma and crystals in the client's money bar.</summary>
        public void SendMoney()
        {
            var money = Proto.New<SMoneyInfo>();
            money.Karma = Account.Karma;
            money.FK = Account.Crystals;
            Send(GameResponses.UpdateMoneyInfo(money));
        }

        public void OnDisconnected()
        {
            _aiTimer?.Dispose();
            _aiTimer = null;
            lock (World.Sync)
            {
                SaveAccount();
                if (AccountKey != null)
                    World.ReleaseAccount(AccountKey, this);
            }
        }

        /// <summary>A newer session took this account: persist what this one has, then go away
        /// without saving again (all callers hold <see cref="World.Sync"/>).</summary>
        public void Supersede()
        {
            SaveAccount(); // last state of the old session goes to disk first
            Superseded = true;
            Disconnect();
        }

        private void LogPacket(string direction, string name)
        {
            if (World.Options.LogPackets)
                Log.LogInformation("{Direction} {Name}", direction, name);
        }
    }
}
