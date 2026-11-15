namespace Akaneiro.Server.Protocol
{
    /// <summary>
    /// Which cut of the protocol a client connection speaks. Decided once at web login (see
    /// Http/WebLoginService.cs: the "other" form field of the client.php login POST is the marker)
    /// and carried on the gameCode; the game connection then runs every send through the matching
    /// dialect filter (Zh2013Dialect / Apr2013Dialect) or sends the 2014 wire unchanged.
    /// </summary>
    public enum ClientDialect
    {
        /// <summary>2014 protocol (Steam client, 2014 web builds): no translation.</summary>
        Modern,

        /// <summary>January 2013 protocol cut - the Chinese fat client. Sends no "other" field at all.</summary>
        Zh2013,

        /// <summary>
        /// April 2013 protocol cut - the English download client (Win04162, "win04162.zip").
        /// Posts an empty "other" field: it knows the well, redeem gifts and the shop visibility
        /// flags, but not mail, char slots, rename/gender, cooldowns or the stash bag.
        /// </summary>
        Apr2013,
    }
}
