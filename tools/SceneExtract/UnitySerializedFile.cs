using System.Text;

namespace SceneExtract;

/// <summary>
/// Reader for Unity 4 editor serialized files (format version 9, as used by the .unity scenes and
/// .prefab files of the Akaneiro project). Editor files embed a type tree per class, so every
/// object can be decoded generically into nested dictionaries/lists without knowing its layout.
/// </summary>
public sealed class UnitySerializedFile
{
    public sealed class TypeNode
    {
        public string Type = "";
        public string Name = "";
        public int Size;
        public bool IsArray;
        public int MetaFlag;
        public List<TypeNode> Children = new();

        public bool Align => (MetaFlag & 0x4000) != 0;
    }

    public sealed class ObjectInfo
    {
        public long PathId;
        public long ByteStart;
        public int ByteSize;
        public int TypeId;
        public int ClassId;
    }

    public sealed class External
    {
        public string Guid = "";
        public string PathName = "";
    }

    public string Path { get; }
    public string UnityVersion { get; private set; } = "";
    public Dictionary<int, TypeNode> Types { get; } = new();
    public Dictionary<long, ObjectInfo> Objects { get; } = new();
    public List<External> Externals { get; } = new();

    private readonly byte[] _data;
    private long _dataOffset;

    public UnitySerializedFile(string path)
    {
        Path = path;
        _data = File.ReadAllBytes(path);
        ParseMetadata();
    }

    /// <summary>Reads a serialized file from memory (the files inside a UnityCfsFile bundle).</summary>
    public UnitySerializedFile(byte[] data, string displayName)
    {
        Path = displayName;
        _data = data;
        ParseMetadata();
    }

    private void ParseMetadata()
    {
        var r = new Reader(_data) { BigEndian = true };
        int metadataSize = r.Int32();
        int fileSize = r.Int32();
        int version = r.Int32();
        _dataOffset = r.UInt32();
        if (version != 9)
            throw new NotSupportedException($"{Path}: serialized file version {version} not supported (expected 9)");
        bool bigEndian = r.Byte() != 0;
        r.Pos += 3;
        r.BigEndian = bigEndian;

        UnityVersion = r.CString();
        r.Int32(); // target platform

        int typeCount = r.Int32();
        for (int i = 0; i < typeCount; i++)
        {
            int classId = r.Int32();
            Types[classId] = ReadTypeNode(r);
        }
        r.Int32(); // unknown, always 0 in v9

        int objectCount = r.Int32();
        for (int i = 0; i < objectCount; i++)
        {
            var info = new ObjectInfo
            {
                PathId = r.Int32(),
                ByteStart = r.UInt32() + _dataOffset,
                ByteSize = r.Int32(),
                TypeId = r.Int32(),
                ClassId = r.Int16(),
            };
            r.Int16(); // isDestroyed
            Objects[info.PathId] = info;
        }

        int externalCount = r.Int32();
        for (int i = 0; i < externalCount; i++)
        {
            r.CString(); // asset path (empty)
            byte[] guid = r.Bytes(16);
            r.Int32(); // type
            string pathName = r.CString();
            Externals.Add(new External { Guid = GuidToString(guid), PathName = pathName });
        }
    }

    private static TypeNode ReadTypeNode(Reader r)
    {
        var node = new TypeNode
        {
            Type = r.CString(),
            Name = r.CString(),
            Size = r.Int32(),
        };
        r.Int32(); // index
        node.IsArray = r.Int32() != 0;
        r.Int32(); // version
        node.MetaFlag = r.Int32();
        int childCount = r.Int32();
        for (int i = 0; i < childCount; i++)
            node.Children.Add(ReadTypeNode(r));
        return node;
    }

    // Unity stores the GUID nibble-swapped relative to the hex string in .meta files.
    private static string GuidToString(byte[] g)
    {
        var sb = new StringBuilder(32);
        foreach (byte b in g)
        {
            sb.Append("0123456789abcdef"[b & 0xF]);
            sb.Append("0123456789abcdef"[b >> 4]);
        }
        return sb.ToString();
    }

    /// <summary>The object's serialized bytes, for built player files that carry no type trees.</summary>
    public byte[] Raw(long pathId)
    {
        ObjectInfo info = Objects[pathId];
        return _data.AsSpan((int)info.ByteStart, info.ByteSize).ToArray();
    }

    /// <summary>Decodes an object into Dictionary&lt;string, object&gt; / List&lt;object&gt; / primitives.</summary>
    public Dictionary<string, object> Read(long pathId)
    {
        ObjectInfo info = Objects[pathId];
        TypeNode type = Types[info.TypeId];
        var r = new Reader(_data) { Pos = info.ByteStart };
        var result = (Dictionary<string, object>)ReadValue(r, type, info.ByteStart);
        return result;
    }

    private object ReadValue(Reader r, TypeNode node, long origin)
    {
        object value;
        if (node.IsArray)
        {
            int count = r.Int32();
            TypeNode elem = node.Children[1];
            if (elem.Children.Count == 0 && elem.Size == 1 && node.Children[1].Type is "char" or "UInt8" or "SInt8")
            {
                value = r.Bytes(count);
            }
            else
            {
                var list = new List<object>(count);
                for (int i = 0; i < count; i++)
                    list.Add(ReadValue(r, elem, origin));
                value = list;
            }
        }
        else if (node.Children.Count == 0)
        {
            value = node.Type switch
            {
                "bool" => r.Byte() != 0,
                "SInt8" => (object)(sbyte)r.Byte(),
                "UInt8" or "char" => r.Byte(),
                "SInt16" or "short" => r.Int16(),
                "UInt16" or "unsigned short" => (ushort)r.Int16(),
                "int" or "SInt32" => r.Int32(),
                "UInt32" or "unsigned int" => r.UInt32(),
                "float" => r.Float(),
                "double" => r.Double(),
                "SInt64" or "long long" => r.Int64(),
                "UInt64" or "unsigned long long" => (ulong)r.Int64(),
                _ => r.Bytes(node.Size),
            };
        }
        else if (node.Type == "string")
        {
            var bytes = (byte[])ReadValue(r, node.Children[0], origin);
            value = Encoding.UTF8.GetString(bytes);
        }
        else if (node.Children.Count == 1 && node.Children[0].IsArray)
        {
            // vector / map / set: unwrap the inner "Array" node.
            value = ReadValue(r, node.Children[0], origin);
        }
        else
        {
            var dict = new Dictionary<string, object>();
            foreach (TypeNode child in node.Children)
                dict[child.Name] = ReadValue(r, child, origin);
            value = dict;
        }

        if (node.Align)
            r.Pos = origin + ((r.Pos - origin + 3) & ~3L);
        return value;
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        public long Pos;
        public bool BigEndian;

        public Reader(byte[] d) => _d = d;

        public byte Byte() => _d[Pos++];

        public byte[] Bytes(int n)
        {
            var b = new byte[n];
            Array.Copy(_d, Pos, b, 0, n);
            Pos += n;
            return b;
        }

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

        public short Int16() => BitConverter.ToInt16(Take(2));
        public int Int32() => BitConverter.ToInt32(Take(4));
        public uint UInt32() => BitConverter.ToUInt32(Take(4));
        public long Int64() => BitConverter.ToInt64(Take(8));
        public float Float() => BitConverter.ToSingle(Take(4));
        public double Double() => BitConverter.ToDouble(Take(8));

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
