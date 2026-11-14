using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Akaneiro.Server.Game;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Net
{
    /// <summary>
    /// One client TCP connection. Wire format (little endian), see client TcpSessionFactory:
    ///   int32 bodySize | int16 packetType | body[bodySize]
    /// packetType 0 = game logic (body starts with the int32 method id), 1 = ping, 2 = pong.
    /// The client pings every 10 s with a 4 byte timestamp and expects it echoed back in a pong.
    /// </summary>
    public sealed class Connection : IPacketSink
    {
        private const short PacketGameLogic = 0;
        private const short PacketPing = 1;
        private const short PacketPong = 2;
        private const int MaxBodySize = 1024 * 1024;

        /// <summary>The client pings every 10 s; a connection that sent nothing for this long is
        /// dead (Wi-Fi drop, suspended machine) and gets closed, so its session stops holding the
        /// account against a reconnect.</summary>
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

        private static int _nextId;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ILogger _log;
        private readonly object _sendLock = new object();

        public int Id { get; } = Interlocked.Increment(ref _nextId);
        public string RemoteEndPoint { get; }

        public Connection(TcpClient client, ILogger log)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            _log = log;
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        }

        public async Task RunAsync(Session session, CancellationToken ct)
        {
            var header = new byte[6];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // The client pings every 10 s: anything older than IdleTimeout is a dead
                    // connection whose read would otherwise block forever.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);

                    if (!await ReadExactlyAsync(header, idle.Token))
                    {
                        session.EndReason = ct.IsCancellationRequested ? "server stopping" : "client closed";
                        break;
                    }

                    int bodySize = BitConverter.ToInt32(header, 0);
                    short type = BitConverter.ToInt16(header, 4);
                    if (bodySize < 0 || bodySize > MaxBodySize)
                    {
                        session.EndReason = "invalid frame size";
                        _log.LogWarning("Connection {Id}: invalid body size {Size}, closing", Id, bodySize);
                        break;
                    }

                    var body = new byte[bodySize];
                    if (!await ReadExactlyAsync(body, ct))
                        break;

                    switch (type)
                    {
                        case PacketGameLogic:
                            session.HandlePacket(body);
                            break;
                        case PacketPing:
                            SendRaw(PacketPong, body);
                            break;
                        case PacketPong:
                            break;
                        default:
                            _log.LogWarning("Connection {Id}: unknown packet type {Type}", Id, type);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The read got nothing for IdleTimeout: a dead connection (Wi-Fi drop, suspended
                // machine). Its session would otherwise hold the account against a reconnect.
                session.EndReason = "idle timeout";
                _log.LogInformation("Connection {Id}: idle for {Timeout}, closing", Id, IdleTimeout);
            }
            catch (OperationCanceledException)
            {
                session.EndReason = "server stopping";
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException)
            {
                // connection dropped
            }
            finally
            {
                _client.Close();
            }
        }

        /// <summary>Sends a packet built by the generated protocol code (already contains the 6 byte header).</summary>
        public void Send(Packet packet) => SendBytes(packet.GetByteStream());

        public void Close()
        {
            try { _client.Close(); } catch (ObjectDisposedException) { }
        }

        private void SendRaw(short type, byte[] body)
        {
            var bytes = new byte[6 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(bytes, 0);
            BitConverter.GetBytes(type).CopyTo(bytes, 4);
            body.CopyTo(bytes, 6);
            SendBytes(bytes);
        }

        private void SendBytes(byte[] bytes)
        {
            try
            {
                lock (_sendLock)
                    _stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                _log.LogDebug("Connection {Id}: send failed: {Message}", Id, ex.Message);
            }
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0)
                    return false;
                read += n;
            }
            return true;
        }
    }
}
