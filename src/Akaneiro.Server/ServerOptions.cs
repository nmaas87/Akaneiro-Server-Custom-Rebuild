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

        /// <summary>Log every packet (name only) in both directions.</summary>
        public bool LogPackets { get; set; } = true;

        /// <summary>
        /// Serve the web login of the "client ver" build: POST /client.php?mod=login with email and
        /// password, answered from data/webusers.json. The Steam login does not use this; enabling it
        /// only adds the second login path (see Http/WebLoginService.cs).
        /// </summary>
        public bool WebLogin { get; set; } = false;
    }
}
