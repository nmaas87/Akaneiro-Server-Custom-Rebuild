namespace Akaneiro.Server
{
    /// <summary>Settings from the "Akaneiro" section of appsettings.json.</summary>
    public sealed class ServerOptions
    {
        /// <summary>Port of the stand-in for the game's old login service at mw.angry-red.com. The client uses plain HTTP on 80.</summary>
        public int HttpPort { get; set; } = 80;

        /// <summary>
        /// Address the web service listens on: "127.0.0.1" serves only this computer, "0.0.0.0" every
        /// network interface (needed when other computers in the LAN play on this server).
        /// </summary>
        public string HttpListenAddress { get; set; } = "127.0.0.1";

        /// <summary>TCP port of the game server.</summary>
        public int GamePort { get; set; } = 7001;

        /// <summary>
        /// Host the client is told to connect to after the Steam login: this computer's address as
        /// the players see it (a LAN address like 192.168.0.10 when other computers play here).
        /// </summary>
        public string PublicHost { get; set; } = "127.0.0.1";

        /// <summary>Directory for save data (accounts, characters). Relative paths are resolved against the content root.</summary>
        public string DataDir { get; set; } = "data";

        /// <summary>Folder with the client's data tables (Resources/Localization/EN): the repository's client-source, or the client project's Assets folder.</summary>
        public string ClientAssetsDir { get; set; } = "";

        /// <summary>
        /// Folder holding the April 16 2013 client's own content tables (same layout as
        /// ClientAssetsDir: Resources/Localization/EN). If it is not present the April 2013
        /// dialect still runs, but on the modern content.
        /// </summary>
        public string Apr2013AssetsDir { get; set; } = "client-source-2013";

        /// <summary>Log every packet (name only) in both directions.</summary>
        public bool LogPackets { get; set; } = true;

        /// <summary>
        /// Write every logged-in account to disk again after this many seconds, so a server crash
        /// costs at most this much play (saves are already event-driven; this is the safety net).
        /// 0 or less turns the periodic save off and leaves only the event saves.
        /// </summary>
        public int AutoSaveSeconds { get; set; } = 180;

        /// <summary>
        /// Average packets per second one connection may send (game logic and ping frames alike;
        /// server responses do not count). A hacked client that floods the socket is disconnected
        /// once its burst is spent. 0 or less turns the flood guard off.
        /// </summary>
        public int PacketPerSecond { get; set; } = 100;

        /// <summary>Headroom the budget starts with: short bursts (scene load, heavy fight) stay
        /// allowed as long as the average over time stays under Akaneiro:PacketPerSecond.</summary>
        public int PacketBurst { get; set; } = 250;

        /// <summary>
        /// Optional "who may play" list: one Steam ID or e-mail per line (# comments allowed).
        /// While the file does not exist everyone may log in; once it exists only the listed
        /// accounts can - web login accounts by their e-mail, because the client only sends their
        /// numeric uid. An existing but empty file lets nobody in. The file is picked up again
        /// when it changes; point it anywhere, empty disables nothing (absence of the file does).
        /// </summary>
        public string LoginAllowlist { get; set; } = "data/allowlist.txt";

        /// <summary>
        /// Serve the web login of the "client ver" build: POST /client.php?mod=login with email and
        /// password, answered from data/webusers.json. The Steam login does not use this; enabling it
        /// only adds the second login path (see Http/WebLoginService.cs).
        /// </summary>
        public bool WebLogin { get; set; } = false;
    }
}
