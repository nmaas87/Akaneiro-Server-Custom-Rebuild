using System;
using System.Collections.Generic;
using Akaneiro.Server.Data;
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
