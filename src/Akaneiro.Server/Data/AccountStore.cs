using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Akaneiro.Server.Data
{
    /// <summary>
    /// Stores accounts as one JSON file each (data/accounts/&lt;account&gt;.json).
    /// All access happens under <see cref="Game.World.Sync"/>, so no locking is done here.
    /// </summary>
    public sealed class AccountStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly string _accountsDir;
        private readonly string _statePath;
        private readonly ServerState _state;

        public AccountStore(string dataDir)
        {
            _accountsDir = Path.Combine(dataDir, "accounts");
            _statePath = Path.Combine(dataDir, "state.json");
            Directory.CreateDirectory(_accountsDir);
            _state = File.Exists(_statePath)
                ? JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_statePath))
                : new ServerState();
        }

        public AccountData LoadOrCreate(string account)
        {
            string path = AccountPath(account);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AccountData>(File.ReadAllText(path));

            var data = new AccountData
            {
                Id = _state.NextAccountId++,
                Account = account,
                CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            SaveState();
            Save(data);
            return data;
        }

        public void Save(AccountData account)
        {
            WriteAtomic(AccountPath(account.Account), JsonSerializer.Serialize(account, Json));
        }

        public int NewCharacterId()
        {
            int id = _state.NextCharacterId++;
            SaveState();
            return id;
        }

        public bool IsNameTaken(string name)
        {
            foreach (string file in Directory.EnumerateFiles(_accountsDir, "*.json"))
            {
                var acc = JsonSerializer.Deserialize<AccountData>(File.ReadAllText(file));
                if (acc.Characters.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        public int NewMailId()
        {
            int id = _state.NextMailId++;
            SaveState();
            return id;
        }

        private void SaveState() => WriteAtomic(_statePath, JsonSerializer.Serialize(_state, Json));

        private string AccountPath(string account)
        {
            var sb = new StringBuilder();
            foreach (char c in account)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return Path.Combine(_accountsDir, sb + ".json");
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
