using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Akaneiro.Server.Data
{
    /// <summary>One web-login account: email + password hash, and the numeric uid the client uses.</summary>
    public sealed class WebUserData
    {
        public string Email { get; set; } = "";
        public string Uid { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public long CreatedUnix { get; set; }

        public static string Hash(string password, string salt)
        {
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(salt + "\n" + password));
            return Convert.ToHexString(digest);
        }
    }

    /// <summary>
    /// Accounts for the web login (the "client ver" that talks to client.php instead of Steam).
    /// One JSON file (data/webusers.json). The Steam login never touches this store.
    /// All access happens under Game.World.Sync (HTTP handlers take the lock themselves).
    /// </summary>
    public sealed class WebUserStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly string _path;
        private List<WebUserData> _users;
        private int _nextUid = 10000;

        public WebUserStore(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "webusers.json");
            _users = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<WebUserData>>(File.ReadAllText(_path))
                : new List<WebUserData>();
            if (_users.Count > 0)
                _nextUid = Math.Max(_nextUid, _users.Max(u => int.TryParse(u.Uid, out int n) ? n : 0) + 1);
        }

        public IReadOnlyList<WebUserData> Users => _users;

        public WebUserData Find(string email) =>
            _users.FirstOrDefault(u => string.Equals(u.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));

        public bool Verify(string email, string password)
        {
            WebUserData user = Find(email);
            return user != null && user.PasswordHash == WebUserData.Hash(password, user.Salt);
        }

        /// <summary>Creates a web account; null if the email is taken.</summary>
        public WebUserData Create(string email, string password)
        {
            email = email.Trim();
            if (email.Length == 0 || password.Length == 0 || Find(email) != null)
                return null;

            var user = new WebUserData
            {
                Email = email,
                Uid = (_nextUid++).ToString(),
                Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)),
                CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            user.PasswordHash = WebUserData.Hash(password, user.Salt);
            _users.Add(user);
            Save();
            return user;
        }

        public bool IsWebUid(string uid) => FindUid(uid) != null;

        /// <summary>The e-mail behind a web-login uid, or null when it is not a web account
        /// (the login allowlist matches web logins on their address, not their uid).</summary>
        public string EmailOf(string uid) => FindUid(uid)?.Email;

        private WebUserData FindUid(string uid) => _users.FirstOrDefault(u => u.Uid == uid);

        /// <summary>gameCode issued by the web login: token -> uid + expiry.</summary>
        private readonly Dictionary<string, (string Uid, DateTimeOffset Until)> _tokens =
            new Dictionary<string, (string, DateTimeOffset)>();

        /// <summary>Hands out a gameCode for a TCP login (see Http/WebLoginService.cs).</summary>
        public string IssueToken(string uid, DateTimeOffset now, TimeSpan lifetime)
        {
            string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            _tokens[token] = (uid, now + lifetime);
            return token;
        }

        /// <summary>
        /// Checks a gameCode from the web login: known, unexpired, and issued to this uid. A Steam
        /// login never produces a token here, so the two paths can never be confused.
        /// </summary>
        public bool ValidateToken(string token, string uid, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
                return false;
            if (now > entry.Until)
            {
                _tokens.Remove(token);
                return false;
            }
            return entry.Uid == uid;
        }

        private void Save()
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
