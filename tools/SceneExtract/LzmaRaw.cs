namespace SceneExtract;

/// <summary>
/// Raw LZMA1 decoder (properties byte + 4-byte dictionary size, no size field, no end marker needed),
/// as Unity 3.x/4.x uses it inside "UnityWeb" asset bundles. A direct port of the public-domain
/// LZMA reference decoder (LzmaSpec.cpp, Igor Pavlov).
/// </summary>
internal static class LzmaRaw
{
    private const int NumBitModelTotalBits = 11;
    private const int NumMoveBits = 5;
    private const int BitModelTotal = 1 << NumBitModelTotalBits;
    private const uint TopValue = 1u << 24;

    private const int NumPosBitsMax = 4;
    private const int NumStates = 12;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int StartPosModelIndex = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1); // 128
    private const int MatchMinLen = 2;

    public static byte[] Decode(byte[] src, int offset, long uncompressedSize)
    {
        byte d0 = src[offset];
        if (d0 >= 9 * 5 * 5)
            throw new System.IO.InvalidDataException("incorrect LZMA properties");
        int lc = d0 % 9;
        int d = d0 / 9;
        int pb = d / 5;
        int lp = d % 5;
        if (lc + lp > 4)
            throw new System.IO.InvalidDataException("LZMA lc+lp > 4");
        uint dictSize = (uint)src[offset + 1] | ((uint)src[offset + 2] << 8) | ((uint)src[offset + 3] << 16) | ((uint)src[offset + 4] << 24);
        if (dictSize < (1 << 12))
            dictSize = 1 << 12;

        // Unity's raw stream: 1 properties byte + 4-byte dictionary size + 8-byte (u64 LE)
        // decompressed size, then the range coder (which starts with its 0x00 init byte).
        long streamSize = BitConverter.ToInt64(src, offset + 5);
        if (streamSize >= 0 && streamSize != uncompressedSize)
            throw new System.IO.InvalidDataException($"LZMA stream size {streamSize} != expected {uncompressedSize}");
        var rc = new RangeDecoder(src, offset + 13);
        if (!rc.Init())
            throw new System.IO.InvalidDataException("bad LZMA range coder start");

        byte[] output = new byte[uncompressedSize];
        var window = new OutWindow(dictSize, output);
        ushort[] litProbs = Init((int)(0x300u << (lc + lp)));
        ushort[] posSlot = Init(NumLenToPosStates << 6);
        ushort[] align = Init(1 << NumAlignBits);
        ushort[] posDecoders = Init(1 + NumFullDistances - EndPosModelIndex);
        ushort[] isMatch = Init(NumStates << NumPosBitsMax);
        ushort[] isRep = Init(NumStates);
        ushort[] isRepG0 = Init(NumStates);
        ushort[] isRepG1 = Init(NumStates);
        ushort[] isRepG2 = Init(NumStates);
        ushort[] isRep0Long = Init(NumStates << NumPosBitsMax);
        var lenDecoder = new LenDecoder();
        var repLenDecoder = new LenDecoder();

        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        uint state = 0;
        long unpackSize = uncompressedSize;

        while (true)
        {
            if (unpackSize == 0)
                return rc.IsFinishedOk ? output : throw new System.IO.InvalidDataException("LZMA stream not finished");

            uint posState = window.TotalPos & ((1u << pb) - 1);

            if (rc.DecodeBit(isMatch, (int)((state << NumPosBitsMax) + posState)) == 0)
            {
                DecodeLiteral(rc, window, litProbs, state, rep0, lc, lp);
                state = state < 4 ? 0u : state < 10 ? state - 3 : state - 6;
                unpackSize--;
                continue;
            }

            uint len;
            if (rc.DecodeBit(isRep, (int)state) != 0)
            {
                if (window.IsEmpty)
                    throw new System.IO.InvalidDataException("LZMA repeat match with empty window");
                if (rc.DecodeBit(isRepG0, (int)state) == 0)
                {
                    if (rc.DecodeBit(isRep0Long, (int)((state << NumPosBitsMax) + posState)) == 0)
                    {
                        state = state < 7 ? 9u : 11u;
                        window.PutByte(window.GetByte(rep0 + 1));
                        unpackSize--;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (rc.DecodeBit(isRepG1, (int)state) == 0)
                        dist = rep1;
                    else
                    {
                        if (rc.DecodeBit(isRepG2, (int)state) == 0)
                            dist = rep2;
                        else
                        {
                            dist = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = dist;
                }
                len = repLenDecoder.Decode(rc, posState);
                state = state < 7 ? 8u : 11u;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = lenDecoder.Decode(rc, posState);
                state = state < 7 ? 7u : 10u;
                rep0 = DecodeDistance(rc, len, posSlot, align, posDecoders);
                if (rep0 == 0xFFFFFFFF)
                    throw new System.IO.InvalidDataException("unexpected LZMA end marker");
                if (rep0 >= dictSize || !window.CheckDistance(rep0))
                    throw new System.IO.InvalidDataException("bad LZMA distance");
            }

            len += MatchMinLen;
            if (unpackSize < len)
                throw new System.IO.InvalidDataException("LZMA match runs past the expected output size");
            window.CopyMatch(rep0 + 1, len);
            unpackSize -= len;
        }
    }

    private static ushort[] Init(int count)
    {
        var probs = new ushort[count];
        for (int i = 0; i < count; i++)
            probs[i] = BitModelTotal / 2;
        return probs;
    }

    private static void DecodeLiteral(RangeDecoder rc, OutWindow window, ushort[] litProbs, uint state, uint rep0, int lc, int lp)
    {
        uint prevByte = window.IsEmpty ? 0u : window.GetByte(1);
        uint symbol = 1;
        uint litState = ((window.TotalPos & ((1u << lp) - 1)) << lc) + (prevByte >> (8 - lc));
        int probsBase = 0x300 * (int)litState;

        if (state >= 7)
        {
            uint matchByte = window.GetByte(rep0 + 1);
            while (true)
            {
                uint matchBit = (matchByte >> 7) & 1;
                matchByte <<= 1;
                uint bit = (uint)rc.DecodeBit(litProbs, probsBase + (int)(((1 + matchBit) << 8) + symbol));
                symbol = (symbol << 1) | bit;
                if (matchBit != bit)
                    break;
                if (symbol >= 0x100)
                    break;
            }
        }
        while (symbol < 0x100)
            symbol = (symbol << 1) | (uint)rc.DecodeBit(litProbs, probsBase + (int)symbol);
        window.PutByte((byte)(symbol - 0x100));
    }

    private static uint DecodeDistance(RangeDecoder rc, uint len, ushort[] posSlot, ushort[] align, ushort[] posDecoders)
    {
        uint lenState = len;
        if (lenState > NumLenToPosStates - 1)
            lenState = NumLenToPosStates - 1;

        uint posSlotIndex = BitTreeDecode(rc, posSlot, (int)(lenState << 6), 6);
        if (posSlotIndex < StartPosModelIndex)
            return posSlotIndex;

        uint numDirectBits = (posSlotIndex >> 1) - 1;
        uint dist = (2u | (posSlotIndex & 1)) << (int)numDirectBits;
        if (posSlotIndex < EndPosModelIndex)
            dist += BitTreeReverseDecode(rc, posDecoders, (int)(dist - posSlotIndex), (int)numDirectBits);
        else
        {
            dist += rc.DecodeDirectBits((int)numDirectBits - NumAlignBits) << NumAlignBits;
            dist += BitTreeReverseDecode(rc, align, 0, NumAlignBits);
        }
        return dist;
    }

    private static uint BitTreeDecode(RangeDecoder rc, ushort[] probs, int offset, int numBits)
    {
        uint m = 1;
        for (int i = 0; i < numBits; i++)
            m = (m << 1) | (uint)rc.DecodeBit(probs, offset + (int)m);
        return m - (1u << numBits);
    }

    private static uint BitTreeReverseDecode(RangeDecoder rc, ushort[] probs, int offset, int numBits)
    {
        uint m = 1, symbol = 0;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = (uint)rc.DecodeBit(probs, offset + (int)m);
            m = (m << 1) | bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    private sealed class LenDecoder
    {
        private readonly ushort[] _choice = Init(2); // [0]=Choice, [1]=Choice2
        private readonly ushort[] _low = Init(16 * 16);
        private readonly ushort[] _mid = Init(16 * 16);
        private readonly ushort[] _high = Init(1 << 8);

        public uint Decode(RangeDecoder rc, uint posState)
        {
            if (rc.DecodeBit(_choice, 0) == 0)
                return BitTreeDecode(rc, _low, (int)(posState << 4), 3);
            if (rc.DecodeBit(_choice, 1) == 0)
                return 8 + BitTreeDecode(rc, _mid, (int)(posState << 4), 3);
            return 16 + BitTreeDecode(rc, _high, 0, 8);
        }
    }

    private sealed class RangeDecoder
    {
        private readonly byte[] _src;
        private int _pos;
        private uint _range = 0xFFFFFFFF;
        private uint _code;

        public RangeDecoder(byte[] src, int pos)
        {
            _src = src;
            _pos = pos;
        }

        public bool Corrupted { get; private set; }

        private uint ReadByte() => _pos < _src.Length ? _src[_pos++] : 0u;

        public bool Init()
        {
            byte b = (byte)ReadByte();
            _code = 0;
            for (int i = 0; i < 4; i++)
                _code = (_code << 8) | ReadByte();
            if (b != 0 || _code == _range)
                Corrupted = true;
            return b == 0;
        }

        public bool IsFinishedOk => _code == 0;

        private void Normalize()
        {
            if (_range < TopValue)
            {
                _range <<= 8;
                _code = (_code << 8) | ReadByte();
            }
        }

        public uint DecodeDirectBits(int numBits)
        {
            uint res = 0;
            do
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0u - (_code >> 31);
                _code += _range & t;
                if (_code == _range)
                    Corrupted = true;
                Normalize();
                res <<= 1;
                res += t + 1;
            }
            while (--numBits > 0);
            return res;
        }

        public int DecodeBit(ushort[] probs, int index)
        {
            uint v = probs[index];
            uint bound = (_range >> NumBitModelTotalBits) * v;
            int symbol;
            if (_code < bound)
            {
                v += (uint)((BitModelTotal - v) >> NumMoveBits);
                _range = bound;
                symbol = 0;
            }
            else
            {
                v -= v >> NumMoveBits;
                _code -= bound;
                _range -= bound;
                symbol = 1;
            }
            probs[index] = (ushort)v;
            Normalize();
            return symbol;
        }
    }

    private sealed class OutWindow
    {
        private readonly byte[] _buf;
        private uint _pos;
        private readonly uint _size;
        private bool _isFull;
        private readonly byte[] _out;
        private int _outPos;

        public uint TotalPos;

        public OutWindow(uint dictSize, byte[] output)
        {
            _buf = new byte[dictSize];
            _size = dictSize;
            _out = output;
        }

        public void PutByte(byte b)
        {
            _out[_outPos++] = b;
            _buf[_pos++] = b;
            if (_pos == _size)
            {
                _pos = 0;
                _isFull = true;
            }
            TotalPos++;
        }

        public byte GetByte(uint dist) => _buf[dist <= _pos ? _pos - dist : _size - dist + _pos];

        public void CopyMatch(uint dist, uint len)
        {
            for (; len > 0; len--)
                PutByte(GetByte(dist));
        }

        public bool CheckDistance(uint dist) => dist <= _pos || _isFull;

        public bool IsEmpty => _pos == 0 && !_isFull;
    }
}
