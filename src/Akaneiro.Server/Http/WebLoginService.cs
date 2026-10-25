using System;
using System.Collections.Generic;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akaneiro.Server.Http
{
    /// <summary>
    /// Stand-in for the web account service of the "client ver" build
    /// (see client Scripts/EZGUI/ClientLogicCtrl.cs): the login screen POSTs an email and
    /// password to http://spicyworld.spicyhorse.com/client.php?mod=login; that name is sent to
    /// this server through the hosts file, just like mw.angry-red.com for the Steam login.
    ///
    /// The client parses the answer without a JSON parser: it splits by ',' and reads by
    /// position, so the fields below must keep their exact order and must not contain commas.
    /// The Steam login (mw.angry-red.com, SteamWebService) is a separate path and unaffected.
    ///
    /// With this feature on the server also answers GET /register.html with a small registration
    /// page (the RegisterPage string below), which posts to this same endpoint.
    /// </summary>
    public static class WebLoginService
    {
        /// <summary>How long an issued gameCode stays valid for TCP logins (reconnects included).</summary>
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

        public static void Map(WebApplication app)
        {
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebLogin");
            ServerOptions options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
            World world = app.Services.GetRequiredService<World>();

            // Plain string response, no static-file middleware or template engine involved.
            app.MapGet("/register.html", () => Results.Text(RegisterPage, "text/html; charset=utf-8"));

            app.MapPost("/client.php", (HttpRequest request) =>
            {
                string mod = request.Query["mod"].ToString();
                // The route is only mapped when Akaneiro:WebLogin is on; "register" is part of the
                // same feature (the client's register button only opens a page, so accounts are
                // created against this endpoint with the /register.html page, curl or a browser).
                if (!string.Equals("login", mod, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals("register", mod, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning("web login: unsupported mod '{Mod}'", mod);
                    return Plain("{\"uid\":\"0\",\"message\":\"error\"}");
                }

                string email = request.Form["email"].ToString();
                string password = request.Form["password"].ToString();
                string game = request.Form["game"].ToString();
                string version = request.Form["other"].ToString();

                if (!string.Equals(game, "akaneiro", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(game, "akaneiro_test", StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning("web login: game '{Game}' is not served here", game);
                    return Plain("{\"uid\":\"0\",\"message\":\"error\"}");
                }

                // The HTTP part has no game state to touch; the lock keeps the user file and the
                // token table consistent with TCP logins, which run under the same lock.
                lock (world.Sync)
                {
                    if (string.Equals("register", mod, StringComparison.OrdinalIgnoreCase))
                    {
                        // The client's register button only opens a web page; this endpoint exists
                        // so accounts can be created over HTTP as well (curl/browser).
                        WebUserData created = world.WebUsers.Create(email, password);
                        log.LogInformation("web register {Email}: {Result}", email, created != null ? "created" : "rejected");
                        return Plain(created != null ? "{\"uid\":\"" + created.Uid + "\",\"message\":\"success\"}"
                                                    : "{\"uid\":\"0\",\"message\":\"error\"}");
                    }

                    if (!world.WebUsers.Verify(email, password))
                    {
                        log.LogInformation("web login {Email}: wrong account or password", email);
                        return Plain("{\"uid\":\"0\",\"message\":\"error\"}");
                    }

                    WebUserData user = world.WebUsers.Find(email);
                    string token = world.WebUsers.IssueToken(user.Uid, DateTimeOffset.UtcNow, TokenLifetime);
                    string server = $"{options.PublicHost}:{options.GamePort}";
                    log.LogInformation("web login {Email} (uid {Uid}) -> {Server} (client version {Version})", email, user.Uid, server, version);

                    // {"uid":..,"message":..,"ip":..,"gameCode":..,"key":..} - read by position:
                    // 0 uid, 1 message, 2 ip:port, 3 gameCode (the TCP login's token), 4 key (payment, unused).
                    return Plain("{\"uid\":\"" + user.Uid + "\",\"message\":\"success\",\"ip\":\"" + server +
                                 "\",\"gameCode\":\"" + token + "\",\"key\":\"0\"}");
                }
            });
        }

        private static IResult Plain(string body) => Results.Text(body, "text/plain");

        /// <summary>
        /// The registration page at GET /register.html. Self-contained HTML + a few lines of plain
        /// JS: it POSTs email/password to client.php?mod=register (the same endpoint the standalone
        /// client uses) and shows the answer. Hardcoded so the server needs no wwwroot folder.
        /// </summary>
        private const string RegisterPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Akaneiro - Create an account</title>
<style>
  body { background:#161014; color:#e8dcc8; font-family:Georgia, "Times New Roman", serif;
         display:flex; align-items:center; justify-content:center; min-height:100vh; margin:0; }
  main { background:#1f1820; border:1px solid #4a3b2f; border-radius:8px; padding:32px 36px;
         width:340px; box-shadow:0 6px 24px rgba(0,0,0,.5); }
  h1 { font-size:1.3rem; margin:0 0 4px; color:#f2b134; }
  p.sub { margin:0 0 20px; font-size:.85rem; color:#9c8b77; }
  label { display:block; font-size:.8rem; margin:12px 0 4px; color:#c8b49a; }
  input { width:100%; box-sizing:border-box; padding:8px 10px; border-radius:4px;
          border:1px solid #4a3b2f; background:#12100f; color:#e8dcc8; font-size:.95rem; }
  button { margin-top:20px; width:100%; padding:10px; border:0; border-radius:4px;
           background:#b3372b; color:#fff; font-size:1rem; cursor:pointer; }
  button:hover { background:#d04435; }
  #result { margin-top:16px; font-size:.85rem; min-height:1.2em; }
  #result.ok { color:#7fbf6a; }
  #result.err { color:#e06c5f; }
  footer { margin-top:18px; font-size:.75rem; color:#7a6a58; }
</style>
</head>
<body>
<main>
  <h1>Akaneiro: Demon Hunters</h1>
  <p class="sub">Create a private-server account (web login)</p>
  <form id="f">
    <label for="email">Email</label>
    <input id="email" name="email" type="email" required autocomplete="username">
    <label for="password">Password</label>
    <input id="password" name="password" type="password" required minlength="4" autocomplete="new-password">
    <input type="hidden" name="game" value="akaneiro">
    <button type="submit">Register</button>
  </form>
  <div id="result" role="status"></div>
  <footer>Accounts are stored on this server only. Log in with the standalone (client ver) build.</footer>
</main>
<script>
// client.php answers {"uid":"1234","message":"success"} or {"uid":"0","message":"error"}.
document.getElementById('f').addEventListener('submit', async (e) => {
  e.preventDefault();
  const result = document.getElementById('result');
  result.className = '';
  result.textContent = 'Registering...';
  const body = new URLSearchParams(new FormData(e.target));
  try {
    const res = await fetch('client.php?mod=register', { method: 'POST', body });
    const text = await res.text();
    const m = /"message"\s*:\s*"(\w+)"/.exec(text);
    const uid = /"uid"\s*:\s*"(\d+)"/.exec(text);
    if (m && m[1] === 'success') {
      result.className = 'ok';
      result.textContent = 'Account created (uid ' + (uid ? uid[1] : '?') + '). You can now log in.';
    } else {
      result.className = 'err';
      result.textContent = 'Registration failed - email already taken?';
    }
  } catch (err) {
    result.className = 'err';
    result.textContent = 'Server not reachable.';
  }
});
</script>
</body>
</html>
""";
    }
}
