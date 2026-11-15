using System.Numerics;

namespace SceneExtract;

/// <summary>
/// A UnityWeb bundle (scene or prefab container) with all its serialized files parsed, plus
/// PPtr resolution across those files. In built bundles a MonoBehaviour's m_Script (and other
/// references) live in the sibling ".sharedAssets" serialized file of the same container, which
/// is referenced by index, not by GUID like in editor files. Script names come from the MonoScript
/// objects themselves - bundles carry no .meta files.
/// </summary>
public sealed class AprBundle
{
    public sealed record MonoBehaviourInfo(string Script, string GameObjectName, Vector3 WorldPosition,
        Dictionary<string, object> Fields, long PathId, long GameObjectPathId, UnitySerializedFile File);

    public string Path { get; }
    public string Name { get; }
    private readonly Dictionary<string, UnitySerializedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(UnitySerializedFile File, long PathId), Matrix4x4> _worldCache = new();
    private readonly string _bundlesDir;

    private static readonly Dictionary<string, AprBundle> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a bundle once per process; PPtr fallbacks may pull in other bundles of the same folder.</summary>
    public static AprBundle Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!Cache.TryGetValue(full, out var bundle))
            Cache[full] = bundle = new AprBundle(full);
        return bundle;
    }

    private AprBundle(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _bundlesDir = System.IO.Path.GetDirectoryName(path)!;

        var cfs = new UnityCfsFile(path);
        foreach (UnityCfsFile.Entry entry in cfs.Entries)
        {
            try
            {
                _files[entry.Name] = new UnitySerializedFile(entry.Data, path + "/" + entry.Name);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                Console.Error.WriteLine($"{Name}: entry {entry.Name} is not a readable serialized file, skipped");
            }
        }
    }

    public IEnumerable<UnitySerializedFile> Files => _files.Values;

    /// <summary>The scene file of a map bundle (everything but the shared assets stream).</summary>
    public UnitySerializedFile SceneFile()
    {
        var candidate = _files.FirstOrDefault(f => !f.Key.EndsWith(".sharedAssets", StringComparison.OrdinalIgnoreCase));
        return candidate.Value ?? _files.Values.First();
    }

    /// <summary>Resolves a PPtr (m_FileID/m_PathID dictionary) to the file holding the object.</summary>
    public UnitySerializedFile ResolveFile(UnitySerializedFile origin, Dictionary<string, object> pptr)
    {
        int fileId = Convert.ToInt32(pptr["m_FileID"]);
        if (fileId == 0)
            return origin;
        if (fileId < 0 || fileId > origin.Externals.Count)
            return null;
        string target = origin.Externals[fileId - 1].PathName;
        if (string.IsNullOrEmpty(target))
            return null;

        // A sibling serialized file of this container (the common case: MonoScripts and meshes
        // live in "<bundle>.sharedAssets").
        foreach (string key in _files.Keys)
            if (key.Equals(target, StringComparison.OrdinalIgnoreCase) || key.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase)
                || target.EndsWith(key, StringComparison.OrdinalIgnoreCase) || key.EndsWith(target, StringComparison.OrdinalIgnoreCase))
                return _files[key];

        // A dependency on another bundle in the same folder: loaded on demand.
        foreach (string suffix in new[] { "", ".unity3d", ".assetbundle" })
        {
            string candidatePath = System.IO.Path.Combine(_bundlesDir, target + suffix);
            if (!File.Exists(candidatePath))
                continue;
            try
            {
                AprBundle other = Load(candidatePath);
                foreach (UnitySerializedFile f in other._files.Values)
                    if (f.Path.EndsWith(target, StringComparison.OrdinalIgnoreCase) || other._files.Count == 1)
                        return f;
                return other._files.Values.First();
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException || ex is IOException)
            {
                return null;
            }
        }
        return null;
    }

    private static Dictionary<string, object> AsPptr(object pptr) => pptr as Dictionary<string, object>;

    /// <summary>Reads the object a PPtr points at (null when the reference dangles).</summary>
    public Dictionary<string, object> ReadObject(UnitySerializedFile origin, object pptr)
    {
        var p = AsPptr(pptr);
        if (p == null)
            return null;
        UnitySerializedFile file = ResolveFile(origin, p);
        if (file == null)
            return null;
        long pathId = Convert.ToInt64(p["m_PathID"]);
        if (pathId == 0 || !file.Objects.ContainsKey(pathId))
            return null;
        try
        {
            return file.Read(pathId);
        }
        catch (Exception ex) when (ex is InvalidCastException || ex is KeyNotFoundException || ex is IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The script class name behind an m_Script PPtr (MonoScript object inside the container).</summary>
    public string ScriptOf(UnitySerializedFile origin, object scriptPptr)
    {
        var script = ReadObject(origin, scriptPptr);
        if (script == null)
            return "<unknown>";
        string name = script.GetValueOrDefault("m_ClassName") as string;
        if (string.IsNullOrEmpty(name))
            name = script.GetValueOrDefault("m_Name") as string;
        return string.IsNullOrEmpty(name) ? "<unknown>" : name;
    }

    public IEnumerable<MonoBehaviourInfo> MonoBehaviours(UnitySerializedFile file)
    {
        foreach (var info in file.Objects.Values.Where(o => o.ClassId == 114))
        {
            Dictionary<string, object> fields;
            try { fields = file.Read(info.PathId); }
            catch (Exception ex) when (ex is InvalidCastException || ex is KeyNotFoundException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                continue;
            }

            string script = ScriptOf(file, fields["m_Script"]);
            long goId = PathIdOf(fields["m_GameObject"]);
            string goName = "";
            Vector3 pos = default;
            if (goId != 0 && file.Objects.ContainsKey(goId))
            {
                var go = file.Read(goId);
                goName = (string)go["m_Name"];
                long transformId = FindComponent(go, 4);
                if (transformId != 0)
                    pos = World(file, transformId).Translation;
            }
            yield return new MonoBehaviourInfo(script, goName, pos, fields, info.PathId, goId, file);
        }
    }

    public IEnumerable<MonoBehaviourInfo> AllMonoBehaviours() => _files.Values.SelectMany(MonoBehaviours);

    /// <summary>Seconds of an AnimationClip PPtr (m_StopTime, else its last keyframe; 0 when unresolvable).</summary>
    public float ClipSeconds(UnitySerializedFile origin, object pptr)
    {
        var clip = ReadObject(origin, pptr);
        if (clip == null || !clip.ContainsKey("m_Name"))
            return 0;
        if (clip.GetValueOrDefault("m_AnimationClipSettings") is Dictionary<string, object> settings
            && Convert.ToSingle(settings.GetValueOrDefault("m_StopTime", 0f)) > 0)
            return Convert.ToSingle(settings["m_StopTime"]) - Convert.ToSingle(settings.GetValueOrDefault("m_StartTime", 0f));
        return MaxKeyTime(clip);
    }

    /// <summary>Time of a clip's first animation event (0 when none or unresolvable).</summary>
    public float ClipFirstEvent(UnitySerializedFile origin, object pptr)
    {
        var clip = ReadObject(origin, pptr);
        if (clip?.GetValueOrDefault("m_Events") is not List<object> events)
            return 0;
        return events.OfType<Dictionary<string, object>>()
            .Select(e => Convert.ToSingle(e.GetValueOrDefault("time", 0f))).DefaultIfEmpty(0f).Min();
    }

    public string ClipName(UnitySerializedFile origin, object pptr)
    {
        var clip = ReadObject(origin, pptr);
        return clip?.GetValueOrDefault("m_Name") as string ?? "";
    }

    /// <summary>
    /// Float field of the prefab a PPtr points at: the first MonoBehaviour component of that object's
    /// GameObject carrying the field (e.g. a projectile prefab's speed).
    /// </summary>
    public float PrefabFloat(UnitySerializedFile origin, object pptr, string field)
    {
        var p = AsPptr(pptr);
        if (p == null)
            return 0;
        UnitySerializedFile file = ResolveFile(origin, p);
        if (file == null)
            return 0;
        long pathId = Convert.ToInt64(p["m_PathID"]);
        if (pathId == 0 || !file.Objects.ContainsKey(pathId))
            return 0;

        long gameObjectId = GameObjectOf(file, pathId);
        foreach (var mb in MonoBehaviours(file))
            if (gameObjectId != 0 && mb.GameObjectPathId == gameObjectId && mb.Fields.ContainsKey(field))
                return Convert.ToSingle(mb.Fields[field]);
        // the referenced object itself may be the component with the field
        if (file.Objects[pathId].ClassId == 114)
        {
            var fields = file.Read(pathId);
            if (fields.ContainsKey(field))
                return Convert.ToSingle(fields[field]);
        }
        return 0;
    }

    /// <summary>The GameObject behind a PPtr target (object, Transform or component).</summary>
    private static long GameObjectOf(UnitySerializedFile file, long pathId)
    {
        int cls = file.Objects[pathId].ClassId;
        return cls switch
        {
            1 => pathId,
            4 or 114 => PathIdOf(file.Read(pathId).GetValueOrDefault("m_GameObject")),
            _ => 0,
        };
    }

    /// <summary>Fields of every MonoBehaviour on the GameObject a PPtr points at (prefab lookup).</summary>
    public IEnumerable<Dictionary<string, object>> PrefabFields(UnitySerializedFile origin, object pptr)
    {
        var p = AsPptr(pptr);
        if (p == null)
            yield break;
        UnitySerializedFile file = ResolveFile(origin, p);
        if (file == null)
            yield break;
        long pathId = Convert.ToInt64(p["m_PathID"]);
        if (pathId == 0 || !file.Objects.ContainsKey(pathId))
            yield break;

        long gameObjectId = GameObjectOf(file, pathId);
        if (gameObjectId != 0)
            foreach (var mb in MonoBehaviours(file))
                if (mb.GameObjectPathId == gameObjectId)
                    yield return mb.Fields;
        else if (file.Objects[pathId].ClassId == 114)
            yield return file.Read(pathId);
    }

    private static float MaxKeyTime(object node) => node switch
    {
        Dictionary<string, object> d => Math.Max(d.TryGetValue("time", out object t) && t is float f ? f : 0f, d.Values.Select(MaxKeyTime).DefaultIfEmpty(0f).Max()),
        List<object> list => list.Select(MaxKeyTime).DefaultIfEmpty(0f).Max(),
        _ => 0f,
    };

    public static long PathIdOf(object pptr)
    {
        var p = pptr as Dictionary<string, object>;
        return p == null ? 0 : Convert.ToInt64(p["m_PathID"]);
    }

    private static long FindComponent(Dictionary<string, object> go, int classId)
    {
        foreach (var entry in (List<object>)go["m_Component"])
        {
            var pair = (Dictionary<string, object>)entry;
            if (Convert.ToInt32(pair["first"]) == classId)
                return PathIdOf(pair["second"]);
        }
        return 0;
    }

    private Matrix4x4 World(UnitySerializedFile file, long transformId)
    {
        if (_worldCache.TryGetValue((file, transformId), out var cached))
            return cached;

        var t = file.Read(transformId);
        var q = (Dictionary<string, object>)t["m_LocalRotation"];
        var p = (Dictionary<string, object>)t["m_LocalPosition"];
        var s = (Dictionary<string, object>)t["m_LocalScale"];
        float F(Dictionary<string, object> d, string k) => Convert.ToSingle(d[k]);
        var local =
            Matrix4x4.CreateScale(F(s, "x"), F(s, "y"), F(s, "z")) *
            Matrix4x4.CreateFromQuaternion(new Quaternion(F(q, "x"), F(q, "y"), F(q, "z"), F(q, "w"))) *
            Matrix4x4.CreateTranslation(F(p, "x"), F(p, "y"), F(p, "z"));

        long father = PathIdOf(t["m_Father"]);
        var world = father != 0 && file.Objects.ContainsKey(father) ? local * World(file, father) : local;
        _worldCache[(file, transformId)] = world;
        return world;
    }
}
