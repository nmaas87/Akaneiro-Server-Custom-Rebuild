using System;

namespace Akaneiro.Server.Net
{
    /// <summary>
    /// Token bucket for the frames one connection sends: it starts with <c>burst</c> tokens and
    /// refills at <c>perSecond</c>. A hacked client flooding the socket with move, loot or shop
    /// requests runs the bucket empty within seconds and the connection is closed; the real
    /// client is single threaded (its 10 s ping plus a handful of packets per combat action) and
    /// never gets near any sane limit.
    /// </summary>
    public sealed class PacketBudget
    {
        /// <summary>The steady rate and burst size this bucket was built with (for log lines).</summary>
        public double FramesPerSecond { get; }
        public double Burst { get; }

        private readonly double _perSecond;
        private readonly double _burst;
        private readonly Func<DateTime> _now;
        private double _tokens;
        private DateTime _lastRefill;

        public PacketBudget(double perSecond, double burst, Func<DateTime> now)
        {
            _perSecond = perSecond;
            _burst = burst;
            FramesPerSecond = perSecond;
            Burst = burst;
            _tokens = burst;
            _now = now;
            _lastRefill = now();
        }

        /// <summary>Takes one frame's worth; false means this connection has spent its budget.</summary>
        public bool TrySpend()
        {
            DateTime now = _now();
            if (now > _lastRefill)
            {
                _tokens = Math.Min(_burst, _tokens + (now - _lastRefill).TotalSeconds * _perSecond);
                _lastRefill = now;
            }
            if (_tokens < 1)
                return false;
            _tokens -= 1;
            return true;
        }
    }
}
