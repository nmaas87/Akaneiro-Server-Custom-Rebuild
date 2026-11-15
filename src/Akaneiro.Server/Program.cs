using System;
using System.IO;
using System.Net;
using Akaneiro.Server;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Http;
using Akaneiro.Server.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Akaneiro"));
ServerOptions options = builder.Configuration.GetSection("Akaneiro").Get<ServerOptions>() ?? new ServerOptions();

string contentRoot = builder.Environment.ContentRootPath;
string dataDir = Path.GetFullPath(Path.Combine(contentRoot, options.DataDir));
string assetsDir = Path.GetFullPath(Path.Combine(contentRoot, options.ClientAssetsDir));
if (!Directory.Exists(assetsDir))
    throw new DirectoryNotFoundException($"Client assets not found at '{assetsDir}' (setting Akaneiro:ClientAssetsDir)");

builder.Services.AddSingleton(new AccountStore(dataDir));
builder.Services.AddSingleton(new WebUserStore(dataDir));
string gamedataDir = Path.Combine(AppContext.BaseDirectory, "GameData");
builder.Services.AddSingleton(new GameData(assetsDir, gamedataDir));

// The April 2013 build shipped its own tables and maps; when they are in the repo (they are
// extracted from the Win04162 client) sessions on that dialect get their own GameData, with
// everything the 2013 bundle never had read from the modern content.
string aprAssets = Path.GetFullPath(Path.Combine(contentRoot, options.Apr2013AssetsDir));
string aprTables = Path.Combine(assetsDir, "Resources", "Localization", "EN");
GameData apr2013 = File.Exists(Path.Combine(aprAssets, "Resources", "Localization", "EN", "Level.level.EN.txt"))
    ? new GameData(aprAssets, Path.Combine(AppContext.BaseDirectory, "GameData2013"), aprTables, gamedataDir)
    : null;
// The optional "who may play" file; empty setting = the gate is off entirely (null path).
string allowlist = string.IsNullOrWhiteSpace(options.LoginAllowlist)
    ? null
    : Path.GetFullPath(Path.Combine(contentRoot, options.LoginAllowlist));
builder.Services.AddSingleton(new LoginAllowlist(allowlist));
builder.Services.AddSingleton<World>();
builder.Services.AddHostedService<GameListener>();

// The Steam build talks plain HTTP to mw.angry-red.com, which the hosts file points to this server.
if (!IPAddress.TryParse(options.HttpListenAddress, out IPAddress httpAddress))
    throw new ArgumentException($"'{options.HttpListenAddress}' is not an IP address (setting Akaneiro:HttpListenAddress)");
builder.WebHost.ConfigureKestrel(k => k.Listen(httpAddress, options.HttpPort));

var app = builder.Build();
app.Services.GetRequiredService<World>().Data2013 = apr2013;
SteamWebService.Map(app);
if (options.WebLogin)
    WebLoginService.Map(app);

app.Logger.LogInformation("Akaneiro server: web service on {Address}:{Http}, game port {Game} (clients connect to {Public}), data in {Data}, client assets {Assets}{WebLogin}{Allowlist}",
    httpAddress, options.HttpPort, options.GamePort, options.PublicHost, dataDir,
    apr2013 == null ? assetsDir : $"{assetsDir} (+ April 2013 content from {aprAssets})",
    options.WebLogin ? ", web login (client.php + /register.html) enabled" : "",
    allowlist == null ? ""
        : File.Exists(allowlist) ? $", allow list ON ({allowlist})"
        : $", allow list armed but file {allowlist} not present: everyone may log in");
app.Run();
