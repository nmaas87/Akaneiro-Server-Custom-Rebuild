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

            app.MapPost("/client.php", (HttpRequest request) =>
            {
                string mod = request.Query["mod"].ToString();
                // The route is only mapped when Akaneiro:WebLogin is on; "register" is part of the
                // same feature (the client's register button only opens a page, so accounts are
                // created against this endpoint with curl or a browser).
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
    }
}
