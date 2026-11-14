using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Akaneiro.Server.Data
{
    /// <summary>
    /// The optional "who may play" list (Akaneiro:LoginAllowlist): one Steam ID or e-mail
    /// address per line, "#" starts a comment, blank lines are ignored. While the file does not
    /// exist everybody may log in; the moment it exists, only the listed accounts can. A web
    /// login ("client ver") only ever presents its numeric uid, so the gate also compares the
    /// e-mail that belongs to that uid - putting the address on the list is what lets the fat
    /// client player in. An existing but empty file lets nobody in.
    /// The file is re-read whenever it changes, so editing the list works without a restart.
    /// </summary>
    public sealed class LoginAllowlist
    {
        private readonly string _path;
        private DateTime _stamp = DateTime.MinValue;
        private long _length = -1;
        private bool _exists;
        private HashSet<string> _entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The configured file; null (the default in tests without one) means no gate.</summary>
        public LoginAllowlist(string path) => _path = path;

        public bool Enabled => _path != null;

        /// <summary>May this login through the gate? <paramref name="email"/> is the web account's
        /// e-mail for a web-login uid, null for a plain Steam login.</summary>
        public bool Allows(string account, string email)
        {
            if (_path == null)
                return true;
            ReloadIfChanged();
            if (!_exists)
                return true;
            return _entries.Contains(account ?? "")
                || (email != null && _entries.Contains(email));
        }

        private void ReloadIfChanged()
        {
            DateTime stamp = DateTime.MinValue;
            long length = -1;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists)
                {
                    info.Refresh();
                    stamp = info.LastWriteTimeUtc;
                    length = info.Length;
                }
            }
            catch (IOException)
            {
                // treat as absent
            }
            if (stamp == _stamp && length == _length)
                return;
            _stamp = stamp;
            _length = length;
            if (length >= 0)
            {
                _exists = true;
                _entries = new HashSet<string>(
                    File.ReadAllLines(_path)
                        .Select(line => line.Split('#')[0].Trim())
                        .Where(line => line.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _exists = false;
                _entries.Clear();
            }
        }
    }
}
