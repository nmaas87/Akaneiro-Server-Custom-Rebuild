namespace Akaneiro.Server.Net
{
    /// <summary>Where a session's outgoing packets go: the client's TCP connection, or a test.</summary>
    public interface IPacketSink
    {
        void Send(Packet packet);

        /// <summary>Closes the connection (the peer's read loop ends and the session disconnects).</summary>
        void Close();
    }
}
