using System.Text;

namespace SceneExtract;

/// <summary>
/// Reader for Unity 3.x/4.x "UnityWeb" asset bundles (.unity3d / .assetbundle): a small big-endian
/// header, one raw LZMA stream, and a big-endian directory of serialized files at the start of the
/// decompressed buffer. The April 2013 Akaneiro client maps scenes, monster/prop prefabs and
/// per-map "Relation" tables to files this way.
/// </summary>
public sealed class UnityCfsFile
{
    public sealed record Entry(string Name, byte[] Data);

    public string Path { get; }
    public List<Entry> Entries { get; } = new();

    public UnityCfsFile(string path)
    {
        Path = path;
        byte[] raw = File.ReadAllBytes(path);
        var r = new Reader(raw) { BigEndian = true };

        string magic = r.CString();
        if (magic != "UnityWeb")
            throw new NotSupportedException($"{path}: not a UnityWeb bundle (magic {magic})");
        r.Int32(); // container format version (3 for these bundles)
        r.CString(); // container format name ("3.x.x")
        r.CString(); // build version of the writing editor ("4.0.1f2")
        r.Int32(); // minimum streamed bytes
        int headerSize = r.Int32();
        r.Int32(); // levels
        int levelCount = r.Int32();
        r.Pos += 8L * (levelCount - 1);
        r.Int32(); // compressed size
        int uncompressedSize = r.Int32();
        r.Int32(); // complete file size
        r.Int32(); // file info (directory) size

        byte[] data = LzmaRaw.Decode(raw, headerSize, uncompressedSize);

        r = new Reader(data) { BigEndian = true };
        int nodes = r.Int32();
        for (int i = 0; i < nodes; i++)
        {
            string name = r.CString();
            int offset = r.Int32();
            int size = r.Int32();
            byte[] slice = new byte[size];
            Array.Copy(data, offset, slice, 0, size);
            Entries.Add(new Entry(name, slice));
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        public long Pos;
        public bool BigEndian;

        public Reader(byte[] d) => _d = d;

        private ReadOnlySpan<byte> Take(int n)
        {
            var span = new ReadOnlySpan<byte>(_d, (int)Pos, n);
            Pos += n;
            if (BigEndian == BitConverter.IsLittleEndian)
            {
                var copy = span.ToArray();
                Array.Reverse(copy);
                return copy;
            }
            return span;
        }

        public int Int32() => BitConverter.ToInt32(Take(4));

        public string CString()
        {
            long start = Pos;
            while (_d[Pos] != 0) Pos++;
            string s = Encoding.UTF8.GetString(_d, (int)start, (int)(Pos - start));
            Pos++;
            return s;
        }
    }
}
