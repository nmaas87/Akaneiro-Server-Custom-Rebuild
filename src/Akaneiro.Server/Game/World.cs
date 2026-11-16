using System;
using System.Collections.Generic;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Options;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Shared server state. All game logic runs while holding <see cref="Sync"/>, so handlers
    /// never race each other (the server is meant for a handful of local players at most).
    /// </summary>
    public sealed class World
    {
        public object Sync { get; } = new object();
        public ServerOptions Options { get; }
        public AccountStore Accounts { get; }
        public WebUserStore WebUsers { get; }

        /// <summary>The optional "who may play" gate (Akaneiro:LoginAllowlist). Null path = no
        /// gate; that is the default here, Program wires the configured file in.</summary>
        public LoginAllowlist Allowlist { get; }
        public GameData Data { get; }

        /// <summary>
        /// The April 2013 client era's own content (its item tables, level curves, missions),
        /// null when the era's files are not installed. Program injects it.
        /// </summary>
        public GameData Data2013 { get; set; }

        /// <summary>The content set a session on the given client dialect must be served.</summary>
        public GameData DataFor(ClientDialect dialect) =>
            dialect == ClientDialect.Apr2013 && Data2013 != null ? Data2013 : Data;

        /// <summary>The tutorial's scene ("Welcome, Red Hunter").</summary>
        public const string TutorialScene = "Hub_Village_Tutorial";

        /// <summary>
        /// The content a session's scene (re)build must read. DataFor, with one exception: the
        /// January 2013 Chinese build plays on the 2014 content, but its tutorial is still the
        /// old one - TutorialLogic.UpdateTutrialLogicByHunt closes the village doors at kill
        /// counts 2/10/21 and nothing repopulates the wolves, so the 2014 layout's 17 can never
        /// open the last door and the tutorial cannot be finished (the identical failure the
        /// April build had). Its tutorial therefore spawns from the 2013 wolf waves (29, same
        /// spawn points, same prefabs) exactly like April's session; every other scene stays
        /// 2014 content. Needs the April era's files (Data2013), which any server that offers
        /// the 2013 clients has.
        /// </summary>
        public GameData SceneDataFor(ClientDialect dialect, string sceneName) =>
            dialect == ClientDialect.Zh2013 && sceneName == TutorialScene && Data2013 != null
                ? Data2013 : DataFor(dialect);

        /// <summary>Time source of all game logic (cooldowns, monster behaviour, ability objects).</summary>
        public IClock Clock { get; init; } = SystemClock.Instance;

        /// <summary>Dice of all game logic (damage, loot, monster attacks); only used under <see cref="Sync"/>.</summary>
        public Random Random { get; init; } = new Random();

        public World(IOptions<ServerOptions> options, AccountStore accounts, GameData data,
            WebUserStore webUsers = null, LoginAllowlist allowlist = null)
        {
            Options = options.Value;
            Accounts = accounts;
            WebUsers = webUsers ?? new WebUserStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "akaneiro-webusers-" + Guid.NewGuid().ToString("N")));
            Allowlist = allowlist ?? new LoginAllowlist(null);
            Data = data;
        }

        // ---- one live session per account ----------------------------------------------------
        // While a session owns its account string, a second login for the same account runs the
        // UserReconnect handshake against it instead of racing it on the save file. All access
        // happens under <see cref="Sync"/>.

        private readonly Dictionary<string, Session> _accountOwners = new Dictionary<string, Session>();

        /// <summary>The session currently holding this account, or null.</summary>
        public Session OwnerOf(string account) =>
            _accountOwners.TryGetValue(account, out Session session) ? session : null;

        public void ClaimAccount(string account, Session session) => _accountOwners[account] = session;

        /// <summary>Drops ownership, but only if <paramref name="session"/> still holds it.</summary>
        public void ReleaseAccount(string account, Session session)
        {
            if (OwnerOf(account) == session)
                _accountOwners.Remove(account);
        }
    }
}
