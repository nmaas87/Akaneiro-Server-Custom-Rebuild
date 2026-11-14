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
builder.Services.AddSingleton(new GameData(assetsDir, Path.Combine(AppContext.BaseDirectory, "GameData")));
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
SteamWebService.Map(app);
if (options.WebLogin)
    WebLoginService.Map(app);

app.Logger.LogInformation("Akaneiro server: web service on {Address}:{Http}, game port {Game} (clients connect to {Public}), data in {Data}, client assets {Assets}{WebLogin}{Allowlist}",
    httpAddress, options.HttpPort, options.GamePort, options.PublicHost, dataDir, assetsDir,
    options.WebLogin ? ", web login (client.php + /register.html) enabled" : "",
    allowlist == null ? ""
        : File.Exists(allowlist) ? $", allow list ON ({allowlist})"
        : $", allow list armed but file {allowlist} not present: everyone may log in");
app.Run();
