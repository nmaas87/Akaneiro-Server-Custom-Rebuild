// SceneExtract: reads Akaneiro's Unity 4 scene files without Unity and extracts server-relevant data.
//
//   dotnet run --project tools/SceneExtract -- scripts <assetsDir> <scene.unity>
//       Lists every MonoBehaviour: script name, GameObject name, world position.
//   dotnet run --project tools/SceneExtract -- dump <assetsDir> <scene.unity> <ScriptName>
//       Dumps all fields of MonoBehaviours using the given script as JSON.

using System.Numerics;
using System.Text.Json;
using SceneExtract;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].StartsWith("apr-"))
            return AprCommands.Run(args);

        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: SceneExtract scripts|dump <assetsDir> <scene.unity> [ScriptName]");
            return 1;
        }

        var scripts = ScriptGuids(args[1]);

        if (args[0] == "scenes")
        {
            // args[2] = output json; args[3..] = scene files
            var result = new SortedDictionary<string, object>();
            foreach (string file in args.Skip(3))
            {
                var s = new Scene(new UnitySerializedFile(file), scripts);
                var player = s.MonoBehaviours().FirstOrDefault(m => m.Script == "CreatePlayerForLD");
                if (player == null)
                {
                    Console.Error.WriteLine($"{file}: no CreatePlayerForLD, skipped");
                    continue;
                }
                var p = player.WorldPosition;
                // Items the tutorial waits for the player to pick up (ActiveCollect), like the pelt
                // after the basket; only the server can drop them.
                var collect = s.MonoBehaviours().Where(m => m.Script == "ActiveCollect").Select(m => new
                {
                    ItemId = Convert.ToInt32(m.Fields.GetValueOrDefault("itemID", 0)),
                    Count = Convert.ToInt32(m.Fields.GetValueOrDefault("Count", 1)),
                }).Where(c => c.ItemId != 0).ToList();
                // Village NPCs (vendors, trainers, bounty board, well, stash): the built scene bundles
                // leave them out, the client creates them from the prefabs of "npc info.prefab" on
                // npcEnter. That list has no DIALOG (1012) prefab; statues and the dying tutorial
                // villager stay part of the level.
                const int DialogNpc = 1012;
                var npcs = s.MonoBehaviours().Where(m => m.Script == "ShopNpc" || m.Script == "NPC_Well").Select(m => new
                {
                    NpcId = Convert.ToInt32(m.Fields.GetValueOrDefault("npcType", 0)),
                    Name = m.GameObjectName,
                    Position = new[] { m.WorldPosition.X, m.WorldPosition.Y, m.WorldPosition.Z },
                }).Where(n => n.NpcId != DialogNpc).OrderBy(n => n.NpcId).ToList();
                result[System.IO.Path.GetFileNameWithoutExtension(file)] = new { Spawn = new[] { p.X, p.Y, p.Z }, CollectTargets = collect, Npcs = npcs };
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{result.Count} scenes written to {args[2]}");
            return 0;
        }

        if (args[0] == "clips")
        {
            // args[2..] = .anim files: length (last key) and animation events of each clip
            foreach (string file in args.Skip(2))
            {
                var f = new UnitySerializedFile(file);
                foreach (long id in f.Objects.Keys)
                {
                    Dictionary<string, object> clip;
                    try { clip = f.Read(id); } catch (InvalidCastException) { continue; }
                    if (!clip.ContainsKey("m_Events"))
                        continue;
                    var events = (clip["m_Events"] as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>()
                        .Select(e => $"{e.GetValueOrDefault("functionName")}@{e.GetValueOrDefault("time")}");
                    Console.WriteLine($"{System.IO.Path.GetFileName(file)}: length {Scene.KeyLength(clip):0.000} events [{string.Join(", ", events)}]");
                }
            }
            return 0;
        }

        if (args[0] == "find")
        {
            // args[2] = script name; lists every .prefab/.unity file that references it
            string guid = scripts.First(kv => kv.Value == args[2]).Key;
            foreach (string file in Directory.EnumerateFiles(args[1], "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".prefab") || f.EndsWith(".unity")))
            {
                try
                {
                    if (new UnitySerializedFile(file).Externals.Any(e => e.Guid == guid))
                        Console.WriteLine(file);
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException)
                {
                    // text-serialized or foreign files
                }
            }
            return 0;
        }

        if (args[0] == "missions")
        {
            // args[2] = output json, args[3] = folder with the mission prefabs (Scenes/Areas)
            var missions = new SortedDictionary<int, object>();
            foreach (string file in Directory.EnumerateFiles(args[3], "*.prefab", SearchOption.AllDirectories)
                         .Where(f => f.Replace('\\', '/').Contains("/Mission/")))
            {
                if (!int.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), out int missionId))
                    continue; // e.g. "5111_Old"

                var s = new Scene(new UnitySerializedFile(file), scripts);
                var mission = s.MonoBehaviours().FirstOrDefault(m => m.Fields.ContainsKey("branchArray"));
                if (mission == null)
                    continue;

                // The mission definition is kept as-is; the server reads the parts it needs.
                missions[missionId] = mission.Fields
                    .Where(f => !f.Key.StartsWith("m_"))
                    .ToDictionary(f => f.Key, f => f.Value);
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(missions, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{missions.Count} missions written to {args[2]}");
            return 0;
        }

        if (args[0] == "built-missions")
        {
            // args[2] = output json, args[3] = a built player file (Steam build: Akaneiro_Data/sharedassets0.assets)
            var missions = BuiltMissions.Read(new UnitySerializedFile(args[3]));
            File.WriteAllText(args[2], JsonSerializer.Serialize(missions, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{missions.Count} missions written to {args[2]}");
            return 0;
        }

        if (args[0] == "monsters")
        {
            // args[2] = output json, args[3] = folder with enemy prefabs
            var monsters = new SortedDictionary<int, object>();
            var monsterAssets = AssetGuids(args[1]);
            foreach (string file in Directory.EnumerateFiles(args[3], "*.prefab", SearchOption.AllDirectories))
            {
                var s = new Scene(new UnitySerializedFile(file), scripts);
                // The monster script differs per enemy (Wolf, Oni, ...), so it is recognised by its fields.
                var npc = s.MonoBehaviours().FirstOrDefault(m => m.Fields.ContainsKey("TypeID") && m.Fields.ContainsKey("maxHealth"));
                if (npc == null)
                    continue;

                var loot = s.MonoBehaviours().FirstOrDefault(m => m.Script == "LootDrop");

                int typeId = Convert.ToInt32(npc.Fields["TypeID"]);
                if (typeId == 0 || monsters.ContainsKey(typeId))
                    continue;

                monsters[typeId] = new
                {
                    TypeId = typeId,
                    Name = npc.Fields.GetValueOrDefault("NpcName", "") as string,
                    Prefab = System.IO.Path.GetFileNameWithoutExtension(file),
                    Script = npc.Script,
                    MaxHealth = Convert.ToInt32(npc.Fields.GetValueOrDefault("maxHealth", 0)),
                    Level = Convert.ToInt32(npc.Fields.GetValueOrDefault("EnemyLevel", 1)),
                    LootLevel = Convert.ToInt32(npc.Fields.GetValueOrDefault("LootLevel", 1)),
                    Xp = Convert.ToInt32(npc.Fields.GetValueOrDefault("XP", 0)),
                    KarmaMin = Convert.ToInt32(npc.Fields.GetValueOrDefault("MinKarmaValue", 0)),
                    KarmaMax = Convert.ToInt32(npc.Fields.GetValueOrDefault("MaxKarmaValue", 0)),
                    IsBoss = Convert.ToInt32(npc.Fields.GetValueOrDefault("IsBoss", 0)) != 0,
                    // "Wanted" monsters pay a bounty on top (NpcBase.IsWanted, RewardWantedExp/Sk).
                    IsWanted = Convert.ToInt32(npc.Fields.GetValueOrDefault("IsWanted", 0)) != 0,
                    WantedXp = Convert.ToInt32(npc.Fields.GetValueOrDefault("RewardWantedExp", 0)),
                    WantedKarma = Convert.ToInt32(npc.Fields.GetValueOrDefault("RewardWantedSk", 0)),
                    VisionRadius = Convert.ToSingle(npc.Fields.GetValueOrDefault("VisionRadius", 0f)),
                    AlertDistance = Convert.ToSingle(npc.Fields.GetValueOrDefault("AlertDistance", 0f)),
                    WanderSpeed = Convert.ToSingle(npc.Fields.GetValueOrDefault("SpeedWanderState", 0f)),
                    InitState = Convert.ToInt32(npc.Fields.GetValueOrDefault("InitState", 0)),
                    AttackSpeed = AttackState(npc, "SpeedAttackState"),
                    AttackDamage = FirstAttack(npc, "AttackDamage"),
                    AttackRange = FirstAttack(npc, "MaxAttackDistance"),
                    Attacks = Attacks(npc),
                    // Ranged monsters (Lobber, ...) attack by throwing: their NPC_Toss ability
                    // (an NPC_ProjectileBase) flies a projectile at the player.
                    ThrowAbility = s.MonoBehaviours()
                        .Where(m => m.Script is "NPC_Toss" or "NPC_ProjectileBase")
                        .Select(m => Convert.ToInt32(m.Fields.GetValueOrDefault("id", 0)))
                        .FirstOrDefault(),
                    // When the ball leaves the hand (the cast clip's event, e.g. Oni_Range_Throw 1.03 s)
                    // and how fast it flies (the projectile prefab's speed).
                    ThrowRelease = s.MonoBehaviours().Where(m => m.Script is "NPC_Toss" or "NPC_ProjectileBase")
                        .Select(m => s.ClipFirstEvent(m.Fields.GetValueOrDefault("CastAnimation"), monsterAssets, args[1]))
                        .FirstOrDefault(),
                    ThrowCastLength = s.MonoBehaviours().Where(m => m.Script is "NPC_Toss" or "NPC_ProjectileBase")
                        .Select(m => s.ClipSeconds(m.Fields.GetValueOrDefault("CastAnimation"), monsterAssets, args[1]))
                        .FirstOrDefault(),
                    ThrowSpeed = s.MonoBehaviours().Where(m => m.Script is "NPC_Toss" or "NPC_ProjectileBase")
                        .Select(m => s.PrefabFloat(m.Fields.GetValueOrDefault("ProjectilePrefab"), "speed", monsterAssets, args[1]))
                        .FirstOrDefault(),
                    // How long its entrance and wake-up take (NpcBase export: clip length + AdditinalResetTime).
                    WakeUpSeconds = s.WakeUpSeconds(npc, monsterAssets, args[1]),
                    SpawnSeconds = s.SpawnSeconds(npc, monsterAssets, args[1]),
                    CanDropLoot = loot != null && Convert.ToInt32(loot.Fields.GetValueOrDefault("CanDropLoot", 0)) != 0,
                    // How many pieces of equipment drop, and how many consumables, each as chances.
                    EquipmentDrops = DropList(loot, "RandomDropItemList", "ItemNumber"),
                    ConsumableDrops = DropList(loot, "ConsumableDropList", "DropAmount"),
                    Materials = s.Materials(loot, monsterAssets, args[1]),
                    // Items this monster always (or by chance) drops, like the Harbinger's katana.
                    UniqueDrops = UniqueDrops(loot),
                };
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(monsters.Values, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{monsters.Count} monsters written to {args[2]}");
            return 0;
        }

        if (args[0] == "triggers")
        {
            // args[2] = output json, args[3..] = scene files
            var perScene = new SortedDictionary<string, object>();
            var triggerAssets = AssetGuids(args[1]);
            foreach (string file in args.Skip(3))
            {
                var s = new Scene(new UnitySerializedFile(file), scripts);
                perScene[System.IO.Path.GetFileNameWithoutExtension(file)] = s.Triggers(triggerAssets, args[1]).ToList();
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(perScene, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"triggers of {perScene.Count} scenes written to {args[2]}");
            return 0;
        }

        if (args[0] == "breakables")
        {
            // args[2] = output json, args[3..] = scene files
            var perScene = new SortedDictionary<string, object>();
            var breakableAssets = AssetGuids(args[1]);
            foreach (string file in args.Skip(3))
            {
                var s = new Scene(new UnitySerializedFile(file), scripts);
                perScene[System.IO.Path.GetFileNameWithoutExtension(file)] = s.Breakables(breakableAssets, args[1]).ToList();
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(perScene, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"breakables of {perScene.Count} scenes written to {args[2]}");
            return 0;
        }

        if (args[0] == "spawners")
        {
            // args[2] = output json, args[3..] = scene files
            var assets = AssetGuids(args[1]);
            var perScene = new SortedDictionary<string, object>();
            foreach (string file in args.Skip(3))
            {
                var s = new Scene(new UnitySerializedFile(file), scripts);
                perScene[System.IO.Path.GetFileNameWithoutExtension(file)] = s.Spawners(assets, args[1]).ToList();
            }
            File.WriteAllText(args[2], JsonSerializer.Serialize(perScene, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"spawners of {perScene.Count} scenes written to {args[2]}");
            return 0;
        }

        var scene = new Scene(new UnitySerializedFile(args[2]), scripts);

        switch (args[0])
        {
            case "scripts":
                foreach (var mb in scene.MonoBehaviours().OrderBy(m => m.Script).ThenBy(m => m.GameObjectName))
                    Console.WriteLine($"{mb.Script,-32} {mb.GameObjectName,-40} {Fmt(mb.WorldPosition)}");
                break;

            case "dump":
                var matches = scene.MonoBehaviours().Where(m => m.Script == args[3]).Select(m => new
                {
                    m.GameObjectName,
                    Position = new[] { m.WorldPosition.X, m.WorldPosition.Y, m.WorldPosition.Z },
                    Fields = m.Fields,
                });
                Console.WriteLine(JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                break;

            case "refs":
                // args[3] = script, args[4] = value of its "id" field: lists everything that points to it
                // (its GameObject, Transform or components), e.g. what switches a spawner on.
                var target = scene.MonoBehaviours().First(m => m.Script == args[3] && Convert.ToString(m.Fields.GetValueOrDefault("id")) == args[4]);
                var targetIds = scene.ObjectIdsOf(target.GameObjectPathId);
                foreach (var mb in scene.MonoBehaviours().Where(m => m.GameObjectPathId != target.GameObjectPathId))
                {
                    var paths = new List<string>();
                    FindRefs(mb.Fields, "", targetIds, paths);
                    if (paths.Count > 0)
                        Console.WriteLine($"{mb.Script,-28} {mb.GameObjectName,-40} id={mb.Fields.GetValueOrDefault("id")} {mb.Fields.GetValueOrDefault("unityID_")} via {string.Join(", ", paths)}");
                }
                break;

            case "externals":
                // Resolves the m_FileID of PPtrs into other assets (1-based index into the externals).
                var guids = AssetGuids(args[1]);
                var file = new UnitySerializedFile(args[2]);
                for (int i = 0; i < file.Externals.Count; i++)
                    Console.WriteLine($"{i + 1,3} {guids.GetValueOrDefault(file.Externals[i].Guid, file.Externals[i].PathName)}");
                break;

            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 1;
        }
        return 0;
    }

    /// <summary>Collects the field paths of all PPtrs inside <paramref name="value"/> that point to one of <paramref name="ids"/>.</summary>
    private static void FindRefs(object value, string path, HashSet<long> ids, List<string> found)
    {
        switch (value)
        {
            case Dictionary<string, object> d when d.ContainsKey("m_PathID") && d.ContainsKey("m_FileID"):
                if (ids.Contains(Convert.ToInt64(d["m_PathID"])) && Convert.ToInt32(d["m_FileID"]) == 0)
                    found.Add(path);
                break;
            case Dictionary<string, object> d:
                foreach (var kv in d)
                    FindRefs(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, ids, found);
                break;
            case List<object> list:
                for (int i = 0; i < list.Count; i++)
                    FindRefs(list[i], $"{path}[{i}]", ids, found);
                break;
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

    /// <summary>LootDrop.UniqueItemsPrefab: fixed items with their model and a drop chance in percent.</summary>
    internal static object[] UniqueDrops(Scene.MonoBehaviourInfo loot)
    {
        if (loot == null || loot.Fields.GetValueOrDefault("UniqueItemsPrefab") is not List<object> entries)
            return Array.Empty<object>();

        return entries.OfType<Dictionary<string, object>>().Select(e => (object)new
        {
            ItemId = Convert.ToInt32(e.GetValueOrDefault("ItemID", 0)),
            PrefabId = Convert.ToInt32(e.GetValueOrDefault("PerfabID", 0)),
            Chance = Convert.ToInt32(e.GetValueOrDefault("DropChance", 0)),
        }).ToArray();
    }

    /// <summary>A monster's loot rolls: how many items drop, with which chance.</summary>
    internal static object[] DropList(Scene.MonoBehaviourInfo loot, string field, string amountField)
    {
        if (loot == null || loot.Fields.GetValueOrDefault(field) is not List<object> entries)
            return Array.Empty<object>();

        return entries.OfType<Dictionary<string, object>>().Select(e => (object)new
        {
            Amount = Convert.ToInt32(e.GetValueOrDefault(amountField, 0)),
            Chance = Convert.ToInt32(e.GetValueOrDefault("DropChance", 0)),
        }).ToArray();
    }

    /// <summary>All attack variants of a monster (AttackState.AttackArray).</summary>
    private static object[] Attacks(Scene.MonoBehaviourInfo npc)
    {
        if (npc.Fields.GetValueOrDefault("AttackState") is not Dictionary<string, object> state ||
            state.GetValueOrDefault("AttackArray") is not List<object> attacks)
            return Array.Empty<object>();

        return attacks.OfType<Dictionary<string, object>>().Select(a => (object)new
        {
            Chance = Convert.ToInt32(a.GetValueOrDefault("AttackChance", 0)),
            AnimIndex = Convert.ToInt32(a.GetValueOrDefault("AnimIndexAttack", 0)),
            ResetTime = Convert.ToSingle(a.GetValueOrDefault("ResetTime", 0f)),
            Damage = Convert.ToSingle(a.GetValueOrDefault("AttackDamage", 0f)),
            HitChance = Convert.ToInt32(a.GetValueOrDefault("HitChance", 100)),
            MinDistance = Convert.ToSingle(a.GetValueOrDefault("MinAttackDistance", 0f)),
            MaxDistance = Convert.ToSingle(a.GetValueOrDefault("MaxAttackDistance", 0f)),
        }).ToArray();
    }

    /// <summary>Reads a field of the monster's melee attack state.</summary>
    private static float AttackState(Scene.MonoBehaviourInfo npc, string field)
    {
        return npc.Fields.GetValueOrDefault("AttackState") is Dictionary<string, object> state
               && state.TryGetValue(field, out object v) ? Convert.ToSingle(v) : 0f;
    }

    /// <summary>Reads a field of the monster's first attack (AttackState.AttackArray[0]).</summary>
    private static float FirstAttack(Scene.MonoBehaviourInfo npc, string field)
    {
        if (npc.Fields.GetValueOrDefault("AttackState") is not Dictionary<string, object> state ||
            state.GetValueOrDefault("AttackArray") is not List<object> attacks || attacks.Count == 0 ||
            attacks[0] is not Dictionary<string, object> attack)
            return 0f;
        return attack.TryGetValue(field, out object v) ? Convert.ToSingle(v) : 0f;
    }

    /// <summary>Maps script GUIDs (from .cs.meta / .js.meta files) to script class names.</summary>
    private static Dictionary<string, string> ScriptGuids(string assetsDir)
    {
        var map = new Dictionary<string, string>();
        foreach (string meta in Directory.EnumerateFiles(assetsDir, "*.meta", SearchOption.AllDirectories))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(meta); // "Foo.cs"
            if (!name.EndsWith(".cs") && !name.EndsWith(".js"))
                continue;
            foreach (string line in File.ReadLines(meta))
            {
                if (line.StartsWith("guid: "))
                {
                    map[line.Substring(6).Trim()] = System.IO.Path.GetFileNameWithoutExtension(name);
                    break;
                }
            }
        }
        return map;
    }

    /// <summary>Maps every asset GUID to its path, so PPtrs into other files can be resolved.</summary>
    public static Dictionary<string, string> AssetGuids(string assetsDir)
    {
        var map = new Dictionary<string, string>();
        foreach (string meta in Directory.EnumerateFiles(assetsDir, "*.meta", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(meta))
            {
                if (line.StartsWith("guid: "))
                {
                    map[line.Substring(6).Trim()] = meta.Substring(0, meta.Length - ".meta".Length);
                    break;
                }
            }
        }
        return map;
    }
}

internal sealed class Scene
{
    private const int ClassGameObject = 1;
    private const int ClassTransform = 4;
    private const int ClassMonoBehaviour = 114;

    public sealed record MonoBehaviourInfo(string Script, string GameObjectName, Vector3 WorldPosition,
        Dictionary<string, object> Fields, long PathId, long GameObjectPathId);

    private readonly UnitySerializedFile _file;
    private readonly Dictionary<string, string> _scriptGuids;
    private readonly Dictionary<long, Matrix4x4> _worldCache = new();

    public Scene(UnitySerializedFile file, Dictionary<string, string> scriptGuids)
    {
        _file = file;
        _scriptGuids = scriptGuids;
    }

    private List<MonoBehaviourInfo> _monoBehaviours;

    public IEnumerable<MonoBehaviourInfo> MonoBehaviours() => _monoBehaviours ??= ReadMonoBehaviours().ToList();

    private IEnumerable<MonoBehaviourInfo> ReadMonoBehaviours()
    {
        foreach (var info in _file.Objects.Values.Where(o => o.ClassId == ClassMonoBehaviour))
        {
            var fields = _file.Read(info.PathId);
            string script = ResolveScript(fields["m_Script"]);
            long goId = PathId(fields["m_GameObject"]);
            string goName = "";
            Vector3 pos = default;
            if (goId != 0 && _file.Objects.ContainsKey(goId))
            {
                var go = _file.Read(goId);
                goName = (string)go["m_Name"];
                long transformId = FindComponent(go, ClassTransform);
                if (transformId != 0)
                    pos = World(transformId).Translation;
            }
            yield return new MonoBehaviourInfo(script, goName, pos, fields, info.PathId, goId);
        }
    }

    public sealed record SpawnGroup(string MonsterPrefab, int Count, float SpawnDelay, float DelayPerNpc,
        int RepeatCount, int InitialState, List<float[]> SpawnPositions, int SpawnAnimIndex,
        float SpawnArea, bool Circle, float SpawnWidth, float SpawnHeight, float SpawnRotation, List<float[]> Waypoints,
        List<string> MonsterPrefabs, bool RandomPrefab);

    /// <summary>
    /// What a spawner sets off once KillCount of its monsters are dead (NpcSpawner.LinkedEvents):
    /// EnableSpawner / DisableSpawner (target = spawner id), AutoTrigger / ToggleTrigger (target = trigger id).
    /// </summary>
    public sealed record SpawnerLink(string Kind, int KillCount, float Delay, int TargetId);

    /// <summary>
    /// A spawner. Difficulty is the bit mask of NpcSpawner.DoExport: 1 | none<<1 | low<<2 | normal<<3 | hard<<4.
    /// EnabledByObject: an interactive object (basket, chest, switch) brings these monsters when used.
    /// </summary>
    public sealed record SpawnerInfo(int Id, string Name, float[] Position, int Difficulty, List<SpawnGroup> Groups, List<SpawnerLink> Links,
        bool EnabledByObject, bool Looping = false, int LoopCount = 0);

    /// <summary>
    /// An interactive object the server places (baskets, chests, switches): InteractiveObj and its
    /// subclasses, exported like InteractiveHandler.DoExport ("MapBreakable"). The client builds
    /// them from its own prefabs by TypeId when the server announces them (On_BreakableActorEnter).
    /// </summary>
    public sealed record BreakableInfo(string Name, string Script, int TypeId, float[] Position, float[] Rotation, float[] Scale,
        int Health, int Exp, int LootLevel, int KarmaMin, int KarmaMax, int AppearChance, bool DestroyOnDeath,
        bool Looping, int LoopCount, List<SpawnerLink> Links, List<TriggerResult> Animations, object Loot,
        List<DeathResultInfo> DeathResults);

    /// <summary>InteractiveObj.DeathResults: a buff (type 0) for the player, with its chance (0..1).</summary>
    public sealed record DeathResultInfo(int Type, int Id, float Chance);

    /// <summary>NpcBase "WakeUpTime": length of WakeupAnim plus AdditinalResetTime.WakeupTime (0 = no clip).</summary>
    public float ClipSeconds(object pptr, Dictionary<string, string> assetGuids, string assetsDir) => ClipLength(pptr, assetGuids, assetsDir);

    /// <summary>Time of a clip's first animation event (0 = none), e.g. the moment a throw lets go.</summary>
    public float ClipFirstEvent(object pptr, Dictionary<string, string> assetGuids, string assetsDir)
    {
        if (pptr is not Dictionary<string, object> p || (int)p["m_FileID"] <= 0)
            return 0;
        string relative = ResolveAssetPath(p, assetGuids, assetsDir);
        if (!relative.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            var clipFile = new UnitySerializedFile(System.IO.Path.Combine(assetsDir, relative));
            long clipId = Convert.ToInt64(p["m_PathID"]);
            if (!clipFile.Objects.ContainsKey(clipId) || clipFile.Read(clipId).GetValueOrDefault("m_Events") is not List<object> events)
                return 0;
            return events.OfType<Dictionary<string, object>>().Select(e => Convert.ToSingle(e.GetValueOrDefault("time", 0f))).DefaultIfEmpty(0f).Min();
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException || ex is IOException || ex is InvalidCastException)
        {
            return 0;
        }
    }

    /// <summary>A float field of the first component in a referenced prefab that has it (0 = none).</summary>
    public float PrefabFloat(object pptr, string field, Dictionary<string, string> assetGuids, string assetsDir)
    {
        if (pptr is not Dictionary<string, object> p || (int)p["m_FileID"] <= 0)
            return 0;
        string relative = ResolveAssetPath(p, assetGuids, assetsDir);
        if (!relative.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return 0;
        var prefab = new Scene(new UnitySerializedFile(System.IO.Path.Combine(assetsDir, relative)), _scriptGuids);
        var holder = prefab.MonoBehaviours().FirstOrDefault(m => m.Fields.ContainsKey(field));
        return holder == null ? 0 : Convert.ToSingle(holder.Fields[field]);
    }

    public float WakeUpSeconds(MonoBehaviourInfo npc, Dictionary<string, string> assetGuids, string assetsDir)
    {
        float clip = ClipLength(npc.Fields.GetValueOrDefault("WakeupAnim"), assetGuids, assetsDir);
        if (clip <= 0)
            return 0;
        var extra = npc.Fields.GetValueOrDefault("AdditinalResetTime") as Dictionary<string, object>;
        return clip + Convert.ToSingle(extra?.GetValueOrDefault("WakeupTime", 0f) ?? 0f);
    }

    /// <summary>NpcBase "SpawnAniTime": per entrance animation its length plus AdditinalResetTime.SpawnResetTimes[i].</summary>
    public List<float> SpawnSeconds(MonoBehaviourInfo npc, Dictionary<string, string> assetGuids, string assetsDir)
    {
        var result = new List<float>();
        if (npc.Fields.GetValueOrDefault("SpawnAnims") is not List<object> clips)
            return result;
        var extra = (npc.Fields.GetValueOrDefault("AdditinalResetTime") as Dictionary<string, object>)?.GetValueOrDefault("SpawnResetTimes") as List<object>;
        for (int i = 0; i < clips.Count; i++)
        {
            float add = extra != null && i < extra.Count ? Convert.ToSingle(extra[i]) : 0f;
            result.Add(ClipLength(clips[i], assetGuids, assetsDir) + add);
        }
        return result;
    }

    /// <summary>Length of an AnimationClip in seconds: its last keyframe (legacy clips have no stored length).</summary>
    private float ClipLength(object pptr, Dictionary<string, string> assetGuids, string assetsDir)
    {
        if (pptr is not Dictionary<string, object> p || (int)p["m_FileID"] <= 0)
            return 0;
        string relative = ResolveAssetPath(p, assetGuids, assetsDir);
        if (!relative.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            var clipFile = new UnitySerializedFile(System.IO.Path.Combine(assetsDir, relative));
            long clipId = Convert.ToInt64(p["m_PathID"]);
            if (!clipFile.Objects.ContainsKey(clipId))
                return 0;
            var clip = clipFile.Read(clipId);
            if (clip.GetValueOrDefault("m_AnimationClipSettings") is Dictionary<string, object> settings
                && Convert.ToSingle(settings.GetValueOrDefault("m_StopTime", 0f)) > 0)
                return Convert.ToSingle(settings["m_StopTime"]) - Convert.ToSingle(settings.GetValueOrDefault("m_StartTime", 0f));
            return MaxKeyTime(clip);
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException || ex is IOException)
        {
            return 0;
        }
    }

    public static float KeyLength(object clip) => MaxKeyTime(clip);

    private static float MaxKeyTime(object node) => node switch
    {
        Dictionary<string, object> d => Math.Max(d.TryGetValue("time", out object t) && t is float f ? f : 0f, d.Values.Select(MaxKeyTime).DefaultIfEmpty(0f).Max()),
        List<object> list => list.Select(MaxKeyTime).DefaultIfEmpty(0f).Max(),
        _ => 0f,
    };

    /// <summary>A LootDrop component, the same for monsters and objects (chests, baskets).</summary>
    public object LootOf(MonoBehaviourInfo loot, Dictionary<string, string> assetGuids, string assetsDir) => loot == null ? null : new
    {
        CanDropLoot = Convert.ToInt32(loot.Fields.GetValueOrDefault("CanDropLoot", 0)) != 0,
        EquipmentDrops = Program.DropList(loot, "RandomDropItemList", "ItemNumber"),
        ConsumableDrops = Program.DropList(loot, "ConsumableDropList", "DropAmount"),
        Materials = Materials(loot, assetGuids, assetsDir),
        UniqueDrops = Program.UniqueDrops(loot),
    };

    /// <summary>
    /// LootDrop.MaterialDropList: crafting materials (pelts, ores, ...) with their count and chance in
    /// percent. The entry points at the material's prefab, whose Item component names the item.
    /// </summary>
    public List<object> Materials(MonoBehaviourInfo loot, Dictionary<string, string> assetGuids, string assetsDir)
    {
        var result = new List<object>();
        if (loot == null || loot.Fields.GetValueOrDefault("MaterialDropList") is not List<object> entries)
            return result;
        foreach (var e in entries.OfType<Dictionary<string, object>>())
        {
            if (e.GetValueOrDefault("MaterialPrefab") is not Dictionary<string, object> pptr || (int)pptr["m_FileID"] <= 0)
                continue;
            string relative = ResolveAssetPath(pptr, assetGuids, assetsDir);
            if (!relative.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                continue;
            var item = new Scene(new UnitySerializedFile(System.IO.Path.Combine(assetsDir, relative)), _scriptGuids).MonoBehaviours()
                .FirstOrDefault(m => m.Fields.ContainsKey("TypeID") && m.Fields.ContainsKey("PrefabID"));
            if (item == null)
                continue;
            result.Add(new
            {
                ItemId = Convert.ToInt32(item.Fields["TypeID"]),
                PrefabId = Convert.ToInt32(item.Fields["PrefabID"]),
                Count = Convert.ToInt32(e.GetValueOrDefault("MaterialNumber", 1)),
                Chance = Convert.ToInt32(e.GetValueOrDefault("DropChance", 100)),
            });
        }
        return result;
    }

    public IEnumerable<BreakableInfo> Breakables(Dictionary<string, string> assetGuids, string assetsDir)
    {
        var componentIds = new Dictionary<long, int>();
        foreach (var mb in MonoBehaviours())
        {
            if (mb.Fields.TryGetValue("id", out object cid))
                componentIds[mb.PathId] = Convert.ToInt32(cid);
        }

        foreach (var obj in MonoBehaviours().Where(m => m.Fields.ContainsKey("TypeID") && m.Fields.ContainsKey("Health") && m.Fields.ContainsKey("IsUsed")))
        {
            int Int(string field) => Convert.ToInt32(obj.Fields.GetValueOrDefault(field, 0));
            var links = new List<SpawnerLink>();
            bool looping = false;
            int loopCount = 0;
            if (obj.Fields.GetValueOrDefault("LinkedEvents") is Dictionary<string, object> events)
            {
                looping = Convert.ToInt32(events.GetValueOrDefault("LoopingSpawner", 0)) != 0;
                loopCount = Convert.ToInt32(events.GetValueOrDefault("loopCount", 0));
                // DisableObjects / ToggleObjects: the client toggles plain colliders itself; triggers
                // among them are switched off or on by the server (MapBreakable DisableTrigger).
                foreach (var (list, kind) in new[] { ("EnableSpawnerList", "EnableSpawner"), ("DisableSpawnerList", "DisableSpawner"),
                             ("DisableObjects", "DisableTrigger"), ("ToggleObjects", "ToggleTrigger") })
                {
                    if (events.GetValueOrDefault(list) is not List<object> entries)
                        continue;
                    foreach (var entry in entries.OfType<Dictionary<string, object>>())
                    {
                        long spawnerRef = entry.GetValueOrDefault("Spawner") is Dictionary<string, object> pptr ? PathId(pptr) : 0;
                        int target = kind.EndsWith("Trigger") ? TriggerIdOfTransform(spawnerRef) : SpawnerIdOfTransform(spawnerRef);
                        if (target != 0)
                            links.Add(new SpawnerLink(kind, 0, Convert.ToSingle(entry.GetValueOrDefault("DelayTime", 0f)), target));
                    }
                }
            }

            // What breaking or using the object plays in the level (InteractiveHandler.AnimDataArrays):
            // a forest switch lowers its bridge. Addressed like trigger animations, by the
            // SelfAnimation id ("realID").
            var animations = new List<TriggerResult>();
            if (obj.Fields.GetValueOrDefault("AnimDataArrays") is List<object> animData)
            {
                foreach (var entry in animData.OfType<Dictionary<string, object>>())
                {
                    // A SelfAnimation may have id 0 (B3 of the Blind Rage bridge); the original sent that too.
                    if (!componentIds.TryGetValue(PathId(entry["AnimObj"]), out int realId))
                        continue;
                    animations.Add(new TriggerResult(4, realId + Int("TypeID"), realId,
                        ClipName(entry.GetValueOrDefault("anim"), assetGuids, assetsDir),
                        Convert.ToInt32(entry.GetValueOrDefault("IsLoopAnim", 0)) != 0,
                        Convert.ToSingle(entry.GetValueOrDefault("AnimDelayTime", 0f)),
                        Convert.ToInt32(entry.GetValueOrDefault("bCollision", 0)) != 0,
                        true, 0));
                }
            }

            var deathResults = (obj.Fields.GetValueOrDefault("DeathResults") as List<object> ?? new List<object>())
                .OfType<Dictionary<string, object>>()
                .Select(d => new DeathResultInfo(Convert.ToInt32(d.GetValueOrDefault("Type", 0)), Convert.ToInt32(d.GetValueOrDefault("ID", 0)),
                    Convert.ToSingle(d.GetValueOrDefault("Chance", 1f))))
                .Where(d => d.Id != 0)
                .ToList();

            var p = obj.WorldPosition;
            float[] rotation = { 0, 0, 0 }, scale = { 1, 1, 1 };
            if (obj.GameObjectPathId != 0 && _file.Objects.ContainsKey(obj.GameObjectPathId))
            {
                long transformId = FindComponent(_file.Read(obj.GameObjectPathId), ClassTransform);
                if (transformId != 0 && Matrix4x4.Decompose(World(transformId), out Vector3 s, out Quaternion q, out _))
                {
                    scale = new[] { s.X, s.Y, s.Z };
                    rotation = UnityEuler(q);
                }
            }

            yield return new BreakableInfo(obj.GameObjectName, obj.Script, Int("TypeID"), new[] { p.X, p.Y, p.Z }, rotation, scale,
                Math.Max(1, Int("Health")), Int("Exp"), Int("LootLevel"),
                (int)Convert.ToSingle(obj.Fields.GetValueOrDefault("MinKarmaValue", 0f)),
                (int)Convert.ToSingle(obj.Fields.GetValueOrDefault("MaxKarmaValue", 0f)),
                obj.Fields.ContainsKey("AppearChance") ? Int("AppearChance") : 100,
                Int("DestroyOnDeath") != 0, looping, loopCount, links, animations, LootOf(ComponentOf(obj, "LootDrop"), assetGuids, assetsDir), deathResults);
        }
    }

    /// <summary>World positions of a list of Transforms (spawn locations, wander waypoints).</summary>
    private List<float[]> Points(object transforms)
    {
        var points = new List<float[]>();
        if (transforms is not List<object> list)
            return points;
        foreach (object t in list)
        {
            long id = PathId(t);
            if (id == 0 || !_file.Objects.ContainsKey(id))
                continue;
            var p = World(id).Translation;
            points.Add(new[] { p.X, p.Y, p.Z });
        }
        return points;
    }

    /// <summary>Unity's Transform.eulerAngles (degrees, 0..360) of a rotation.</summary>
    private static float[] UnityEuler(Quaternion q)
    {
        static float Deg(double radians) => (float)(((radians * 180.0 / Math.PI) % 360 + 360) % 360);
        double sinX = Math.Clamp(2.0 * (q.W * q.X - q.Y * q.Z), -1.0, 1.0);
        double x = Math.Asin(sinX);
        double y = Math.Atan2(2.0 * (q.W * q.Y + q.X * q.Z), 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y));
        double z = Math.Atan2(2.0 * (q.W * q.Z + q.X * q.Y), 1.0 - 2.0 * (q.X * q.X + q.Z * q.Z));
        return new[] { Deg(x), Deg(y), Deg(z) };
    }

    /// <summary>
    /// Monster spawners of the scene: which monster prefab, how many, and where they appear.
    /// This is the data the original server needed to populate a level.
    /// </summary>
    public IEnumerable<SpawnerInfo> Spawners(Dictionary<string, string> assetGuids, string assetsDir)
    {
        // Spawners that a basket, chest or switch brings when used (InteractiveHandler.LinkedEvents).
        var enabledByObjects = new HashSet<int>();
        foreach (var handler in MonoBehaviours().Where(m => m.Script == "InteractiveHandler"))
        {
            if (handler.Fields.GetValueOrDefault("LinkedEvents") is Dictionary<string, object> events &&
                events.GetValueOrDefault("EnableSpawnerList") is List<object> entries)
            {
                foreach (var entry in entries.OfType<Dictionary<string, object>>())
                {
                    if (entry.GetValueOrDefault("Spawner") is Dictionary<string, object> pptr)
                        enabledByObjects.Add(SpawnerIdOfTransform(PathId(pptr)));
                }
            }
        }

        foreach (var spawner in MonoBehaviours().Where(m => m.Script == "NpcSpawner"))
        {
            var groups = new List<SpawnGroup>();
            if (spawner.Fields.TryGetValue("SpawnerList", out object list) && list is List<object> entries)
            {
                foreach (var entryObj in entries)
                {
                    var entry = (Dictionary<string, object>)entryObj;
                    // A group names one or more monster prefabs; with IsRandomSpawnNpc each monster picks one
                    // at random, otherwise they take turns (the original server read them as SpawnIDs).
                    var prefabPaths = new List<string>();
                    if (entry.TryGetValue("NpcPrefabArray", out object prefabs) && prefabs is List<object> prefabList)
                        prefabPaths.AddRange(prefabList.Select(p => ResolveAssetPath(p, assetGuids, assetsDir)).Where(p => !string.IsNullOrEmpty(p)));
                    string prefab = prefabPaths.FirstOrDefault() ?? "";

                    var positions = new List<float[]>();
                    if (entry.TryGetValue("SpawnLocation", out object locations) && locations is List<object> locationList)
                    {
                        foreach (object loc in locationList)
                        {
                            long id = PathId(loc);
                            if (id != 0 && _file.Objects.ContainsKey(id))
                            {
                                var p = World(id).Translation;
                                positions.Add(new[] { p.X, p.Y, p.Z });
                            }
                        }
                    }

                    groups.Add(new SpawnGroup(
                        prefab,
                        Convert.ToInt32(entry.GetValueOrDefault("SpawnNpcCount", 0)),
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnDelay", 0f)),
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnDelayPerNpc", 0f)),
                        Convert.ToInt32(entry.GetValueOrDefault("SpawnRepeatCount", 0)),
                        Convert.ToInt32(entry.GetValueOrDefault("InitialState", 0)),
                        positions,
                        Convert.ToInt32(entry.GetValueOrDefault("SpawnAnimIndex", 0)),
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnArea", 0f)),
                        Convert.ToInt32(entry.GetValueOrDefault("Bcircle", 1)) != 0,
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnWidth", 0f)),
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnHeight", 0f)),
                        Convert.ToSingle(entry.GetValueOrDefault("SpawnRotation", 0f)),
                        Points(entry.GetValueOrDefault("WanderWaypoint")),
                        prefabPaths,
                        Convert.ToInt32(entry.GetValueOrDefault("IsRandomSpawnNpc", 0)) != 0));
                }
            }

            var pos = spawner.WorldPosition;
            int spawnerId = Convert.ToInt32(spawner.Fields.GetValueOrDefault("id", 0));
            yield return new SpawnerInfo(spawnerId, spawner.GameObjectName, new[] { pos.X, pos.Y, pos.Z },
                Difficulty(spawner), groups, Links(spawner), enabledByObjects.Contains(spawnerId),
                spawner.Fields.GetValueOrDefault("LinkedEvents") is Dictionary<string, object> le && Convert.ToInt32(le.GetValueOrDefault("LoopingSpawner", 0)) != 0,
                spawner.Fields.GetValueOrDefault("LinkedEvents") is Dictionary<string, object> le2 ? Convert.ToInt32(le2.GetValueOrDefault("loopCount", 0)) : 0);
        }
    }

    private static int Difficulty(MonoBehaviourInfo spawner)
    {
        bool Flag(string name) => Convert.ToInt32(spawner.Fields.GetValueOrDefault(name, 0)) != 0;
        int difficulty = 1;
        if (Flag("bNone")) difficulty |= 1 << 1;
        if (Flag("bLow")) difficulty |= 1 << 2;
        if (Flag("bNormal")) difficulty |= 1 << 3;
        if (Flag("bHard")) difficulty |= 1 << 4;
        return difficulty;
    }

    /// <summary>NpcSpawner.LinkedEvents, with the referenced spawners and triggers resolved to their ids.</summary>
    private List<SpawnerLink> Links(MonoBehaviourInfo spawner)
    {
        var links = new List<SpawnerLink>();
        if (spawner.Fields.GetValueOrDefault("LinkedEvents") is not Dictionary<string, object> events)
            return links;

        void Add(string list, string kind, string targetField, Func<long, int> resolve)
        {
            if (events.GetValueOrDefault(list) is not List<object> entries)
                return;
            foreach (var entry in entries.OfType<Dictionary<string, object>>())
            {
                int target = entry.GetValueOrDefault(targetField) is Dictionary<string, object> pptr ? resolve(PathId(pptr)) : 0;
                if (target == 0)
                    continue; // the level designer left the slot empty
                links.Add(new SpawnerLink(kind,
                    Convert.ToInt32(entry.GetValueOrDefault("KillCondition", 0)),
                    Convert.ToSingle(entry.GetValueOrDefault("DelayTime", 0f)),
                    target));
            }
        }

        Add("EnableSpawnerList", "EnableSpawner", "Spawner", SpawnerIdOfTransform);
        Add("DisableSpawnerList", "DisableSpawner", "Spawner", SpawnerIdOfTransform);
        Add("AutoTriggerList", "AutoTrigger", "trigger", TriggerIdOfTransform);
        Add("ToggleTriggerList", "ToggleTrigger", "trigger", TriggerIdOfTransform);
        return links;
    }

    /// <summary>
    /// A trigger referenced by its Transform: Trigger_Unified is known by its unityID_, the older
    /// trigger scripts (TriggerBase) by their id, exactly like NpcSpawner.DoExport resolves them.
    /// </summary>
    private int TriggerIdOfTransform(long transformId)
    {
        if (transformId == 0 || !_file.Objects.ContainsKey(transformId))
            return 0;
        long gameObject = PathId(_file.Read(transformId)["m_GameObject"]);
        foreach (var behaviour in MonoBehaviours().Where(m => m.GameObjectPathId == gameObject))
        {
            if (behaviour.Script == "Trigger_Unified")
                return Convert.ToInt32(behaviour.Fields.GetValueOrDefault("unityID_", 0));
            if (behaviour.Fields.ContainsKey("TriggerNpcList") && behaviour.Fields.ContainsKey("id"))
                return Convert.ToInt32(behaviour.Fields["id"]);
        }
        return 0;
    }

    /// <summary>Resolves a PPtr into another asset file to that file's path (relative to the assets dir).</summary>
    private string ResolveAssetPath(object pptr, Dictionary<string, string> assetGuids, string assetsDir)
    {
        var p = (Dictionary<string, object>)pptr;
        int fileId = (int)p["m_FileID"];
        if (fileId <= 0 || fileId > _file.Externals.Count)
            return "";
        string guid = _file.Externals[fileId - 1].Guid;
        if (!assetGuids.TryGetValue(guid, out string path))
            return "<" + guid + ">";
        return System.IO.Path.GetRelativePath(assetsDir, path).Replace('\\', '/');
    }

    private const int ClassBoxCollider = 65;
    private const int ClassSphereCollider = 135;

    public sealed record TriggerResult(int Type, int ObjectId, int RealId, string AnimName, bool Loop,
        float Delay, bool Collision, bool Display, int SpawnerId, bool DisableSpawner = false,
        float[] TeleportPos = null, float FadeIn = 0, float FadeOut = 0, bool CameraFollow = false,
        int NpcId = 0, bool Random = false, List<int> Words = null, bool Inside = false);

    /// <summary>
    /// Speech bubbles (PF_Speech_Trigger.SpeechList / UnifiedEventData_Speech): which NPC type says
    /// which lines (indices into its word list), for how long, and whether only while the player
    /// stays inside the trigger.
    /// </summary>
    private static IEnumerable<TriggerResult> SpeechResults(Dictionary<string, object> fields)
    {
        if (fields.GetValueOrDefault("SpeechList") is not List<object> list)
            yield break;
        bool inside = Convert.ToInt32(fields.GetValueOrDefault("InsideArea", 0)) != 0;
        foreach (var s in list.OfType<Dictionary<string, object>>())
        {
            var words = (s.GetValueOrDefault("WordList") as List<object> ?? new List<object>()).Select(Convert.ToInt32).ToList();
            yield return new TriggerResult(5, 0, 0, "", Convert.ToInt32(s.GetValueOrDefault("bLoop", 0)) != 0,
                Convert.ToSingle(s.GetValueOrDefault("SpeakTime", 0f)), false, false, 0,
                NpcId: Convert.ToInt32(s.GetValueOrDefault("NpcID", 0)),
                Random: Convert.ToInt32(s.GetValueOrDefault("bRandom", 0)) != 0,
                Words: words, Inside: inside);
        }
    }

    public sealed record TriggerInfo(int Id, string Name, int UnifiedType, int TriggerType, bool TriggerOn,
        int MaxCount, float DelayTime, float[] Center, float[] Size, float RotationY, List<TriggerResult> Results,
        List<int> EnableTriggers = null);

    /// <summary>
    /// Trigger volumes of a scene with what they do. The server checks whether the player is inside
    /// and then plays the animations / spawns the monsters, like the original level data did.
    /// Result types follow Trigger_Unified.DoExport: 0 = spawn monsters, 4 = animation.
    /// </summary>
    public IEnumerable<TriggerInfo> Triggers(Dictionary<string, string> assetGuids, string assetsDir)
    {
        // Every SelfAnimation / NpcSpawner has an id of its own, which the results refer to.
        var componentIds = new Dictionary<long, int>();
        foreach (var mb in MonoBehaviours())
        {
            if (mb.Fields.TryGetValue("id", out object id))
                componentIds[mb.PathId] = Convert.ToInt32(id);
        }

        // The older trigger scripts all derive from TriggerBase (Trigger, PlayAnimTrigger,
        // ActiveCollisionTrigger, PF_Speech_Trigger, ...) and export the same way: spawners from
        // TriggerNpcList, animations from AnimDataArrays, and speech triggers switch spawners off
        // and arm other triggers.
        foreach (var trigger in MonoBehaviours().Where(m => m.Fields.ContainsKey("TriggerNpcList") && m.Fields.ContainsKey("TriggerOn")))
        {
            int id = Convert.ToInt32(trigger.Fields.GetValueOrDefault("id", 0));
            var classicResults = new List<TriggerResult>();
            if (trigger.Fields.GetValueOrDefault("TriggerNpcList") is List<object> npcList)
            {
                foreach (object entry in npcList)
                {
                    int spawnerId = SpawnerIdOfTransform(PathId(entry));
                    if (spawnerId != 0)
                        classicResults.Add(new TriggerResult(0, 0, 0, "", false, 0f, false, false, spawnerId));
                }
            }

            if (trigger.Fields.GetValueOrDefault("AnimDataArrays") is List<object> animData)
            {
                foreach (var entry in animData.OfType<Dictionary<string, object>>())
                {
                    if (!componentIds.TryGetValue(PathId(entry["AnimObj"]), out int realId))
                        continue; // id 0 is a valid SelfAnimation id
                    classicResults.Add(new TriggerResult(4, realId + id, realId,
                        trigger.Script == "ActiveCollisionTrigger" ? "" : ClipName(entry.GetValueOrDefault("anim"), assetGuids, assetsDir),
                        Convert.ToInt32(entry.GetValueOrDefault("IsLoopAnim", 0)) != 0,
                        Convert.ToSingle(entry.GetValueOrDefault("AnimDelayTime", 0f)),
                        Convert.ToInt32(entry.GetValueOrDefault("bCollision", 0)) != 0,
                        Convert.ToInt32(entry.GetValueOrDefault("bCanShow", 0)) != 0,
                        0));
                }
            }

            if (trigger.Fields.GetValueOrDefault("DisableSpawnerList") is List<object> disabledSpawners)
            {
                foreach (object entry in disabledSpawners)
                {
                    var spawner = MonoBehaviours().FirstOrDefault(m => m.PathId == PathId(entry));
                    if (spawner != null)
                        classicResults.Add(new TriggerResult(0, 0, 0, "", false, 0f, false, false,
                            Convert.ToInt32(spawner.Fields.GetValueOrDefault("id", 0)), DisableSpawner: true));
                }
            }

            classicResults.AddRange(SpeechResults(trigger.Fields));

            // TriggerTeleport: the older portals (cave and hut entrances), same result as Unified teleports.
            if (trigger.Script == "TriggerTeleport"
                && trigger.Fields.GetValueOrDefault("TeleportPos") is Dictionary<string, object> classicTarget
                && PathId(classicTarget) is long classicTargetId && classicTargetId != 0 && _file.Objects.ContainsKey(classicTargetId))
            {
                var t = World(classicTargetId).Translation;
                classicResults.Add(new TriggerResult(2, 0, 0, "", false, 0f, false, false, 0,
                    TeleportPos: new[] { t.X, t.Y, t.Z },
                    FadeIn: Convert.ToSingle(trigger.Fields.GetValueOrDefault("FadeInTime", 1f)),
                    FadeOut: Convert.ToSingle(trigger.Fields.GetValueOrDefault("FadeOutTime", 1f)),
                    CameraFollow: Convert.ToInt32(trigger.Fields.GetValueOrDefault("CameraFollowAtOnce", 1)) != 0));
            }

            var classicEnables = new List<int>();
            if (trigger.Fields.GetValueOrDefault("EnableTriggerList") is List<object> enabledTriggers)
            {
                foreach (object entry in enabledTriggers)
                {
                    var other = MonoBehaviours().FirstOrDefault(m => m.PathId == PathId(entry));
                    if (other != null)
                        classicEnables.Add(Convert.ToInt32(other.Fields.GetValueOrDefault("id", 0)));
                }
            }

            if (classicResults.Count == 0 && classicEnables.Count == 0)
                continue; // camera, sound and other client-only triggers

            (float[] classicCenter, float[] classicSize) = Volume(trigger);
            yield return new TriggerInfo(
                id,
                trigger.GameObjectName,
                classicResults.Any(r => r.Type == 2) ? 3 : classicResults.Any(r => r.Type == 4) ? 1 : classicResults.Any(r => r.Type == 0) ? 0 : 2, // teleport, anim, spawn, speech
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("TriggerType", 0)),
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("TriggerOn", 0)) != 0,
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("MaxTriggerCount", 1)),
                Convert.ToSingle(trigger.Fields.GetValueOrDefault("TriggerDelayTime", 0f)),
                classicCenter, classicSize, RotationY(trigger), classicResults, classicEnables);
        }

        foreach (var trigger in MonoBehaviours().Where(m => m.Script == "Trigger_Unified"))
        {
            int unityId = Convert.ToInt32(trigger.Fields.GetValueOrDefault("unityID_", 0));
            var results = new List<TriggerResult>();

            var anim = ComponentOf(trigger, "UnifiedEventData_Anim");
            if (anim != null && anim.Fields.GetValueOrDefault("animDataList_") is List<object> animData)
            {
                foreach (var entry in animData.OfType<Dictionary<string, object>>())
                {
                    long animObject = PathId(entry["AnimObj"]);
                    if (!componentIds.TryGetValue(animObject, out int realId))
                        continue;
                    results.Add(new TriggerResult(4, realId + unityId, realId,
                        ClipName(entry.GetValueOrDefault("anim"), assetGuids, assetsDir),
                        Convert.ToInt32(entry.GetValueOrDefault("IsLoopAnim", 0)) != 0,
                        Convert.ToSingle(entry.GetValueOrDefault("AnimDelayTime", 0f)),
                        Convert.ToInt32(entry.GetValueOrDefault("bCollision", 0)) != 0,
                        Convert.ToInt32(entry.GetValueOrDefault("bCanShow", 0)) != 0,
                        0));
                }
            }

            var spawn = ComponentOf(trigger, "UnifiedEventData_Spawn");
            if (spawn != null && spawn.Fields.GetValueOrDefault("spawnerList_") is List<object> spawners)
            {
                foreach (object entry in spawners)
                {
                    long spawnerTransform = PathId(entry);
                    int spawnerId = SpawnerIdOfTransform(spawnerTransform);
                    if (spawnerId != 0)
                        results.Add(new TriggerResult(0, 0, 0, "", false, 0f, false, false, spawnerId));
                }
            }

            // Portals: the server moves the player to the target (TelePortMoveResult).
            var teleport = ComponentOf(trigger, "UnifiedEventData_Teleport");
            if (teleport != null && teleport.Fields.GetValueOrDefault("TeleportPos") is Dictionary<string, object> targetRef && PathId(targetRef) is long target && target != 0)
            {
                var t = World(target).Translation;
                results.Add(new TriggerResult(2, 0, 0, "", false, 0f, false, false, 0,
                    TeleportPos: new[] { t.X, t.Y, t.Z },
                    FadeIn: Convert.ToSingle(teleport.Fields.GetValueOrDefault("FadeInTime", 1f)),
                    FadeOut: Convert.ToSingle(teleport.Fields.GetValueOrDefault("FadeOutTime", 1f)),
                    CameraFollow: Convert.ToInt32(teleport.Fields.GetValueOrDefault("CameraFollowAtOnce", 1)) != 0));
            }

            // Speech triggers can switch spawners off and arm other triggers (Trigger_Unified.ExportData_Speech).
            var enableTriggers = new List<int>();
            var speech = ComponentOf(trigger, "UnifiedEventData_Speech");
            if (speech != null)
            {
                results.AddRange(SpeechResults(speech.Fields));
                if (speech.Fields.GetValueOrDefault("DisableSpawnerList") is List<object> disabled)
                {
                    foreach (object entry in disabled)
                    {
                        var spawner = MonoBehaviours().FirstOrDefault(m => m.PathId == PathId(entry));
                        if (spawner != null)
                            results.Add(new TriggerResult(0, 0, 0, "", false, 0f, false, false,
                                Convert.ToInt32(spawner.Fields.GetValueOrDefault("id", 0)), DisableSpawner: true));
                    }
                }
                if (speech.Fields.GetValueOrDefault("EnableTriggerList") is List<object> enabled)
                {
                    foreach (object entry in enabled)
                    {
                        var other = MonoBehaviours().FirstOrDefault(m => m.PathId == PathId(entry));
                        if (other != null)
                            enableTriggers.Add(Convert.ToInt32(other.Fields.GetValueOrDefault("unityID_", 0)));
                    }
                }
            }

            (float[] center, float[] size) = Volume(trigger);
            yield return new TriggerInfo(
                unityId,
                trigger.GameObjectName,
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("UnifiedType", 0)),
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("TriggerType", 0)),
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("TriggerOn", 0)) != 0,
                Convert.ToInt32(trigger.Fields.GetValueOrDefault("MaxTriggerCount", 1)),
                Convert.ToSingle(trigger.Fields.GetValueOrDefault("TriggerDelayTime", 0f)),
                center, size, RotationY(trigger), results, enableTriggers);
        }
    }

    /// <summary>
    /// The name of an AnimationClip, which the client plays by name (SelfAnimation: animation[name]).
    /// Clips live in their own .anim asset; its m_Name is what the clip is called. Clips inside
    /// model files or unreadable assets give "" (show/hide only), which is always safe.
    /// </summary>
    private string ClipName(object pptr, Dictionary<string, string> assetGuids, string assetsDir)
    {
        if (pptr is not Dictionary<string, object> p || (int)p["m_FileID"] <= 0)
            return "";

        string relative = ResolveAssetPath(p, assetGuids, assetsDir);
        if (!relative.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            return "";

        string path = System.IO.Path.Combine(assetsDir, relative);
        if (ClipNameCache.TryGetValue(path, out string cached))
            return cached;

        string name = "";
        try
        {
            var clipFile = new UnitySerializedFile(path);
            long clipId = Convert.ToInt64(p["m_PathID"]);
            if (clipFile.Objects.ContainsKey(clipId) && clipFile.Read(clipId).TryGetValue("m_Name", out object n))
                name = n as string ?? "";
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException || ex is IOException)
        {
            // text-serialized or unreadable asset: fall back to show/hide only
        }
        return ClipNameCache[path] = name;
    }

    private static readonly Dictionary<string, string> ClipNameCache = new();

    /// <summary>A GameObject and all its components (anything a PPtr to "this object" may point at).</summary>
    public HashSet<long> ObjectIdsOf(long gameObjectId)
    {
        var ids = new HashSet<long> { gameObjectId };
        foreach (var entry in (List<object>)_file.Read(gameObjectId)["m_Component"])
            ids.Add(PathId(((Dictionary<string, object>)entry)["second"]));
        return ids;
    }

    /// <summary>Another MonoBehaviour on the same GameObject.</summary>
    private MonoBehaviourInfo ComponentOf(MonoBehaviourInfo behaviour, string script)
    {
        return MonoBehaviours().FirstOrDefault(m => m.Script == script && m.GameObjectPathId == behaviour.GameObjectPathId);
    }

    /// <summary>A spawner is referenced by its Transform; its NpcSpawner component carries the id.</summary>
    private int SpawnerIdOfTransform(long transformId)
    {
        if (transformId == 0 || !_file.Objects.ContainsKey(transformId))
            return 0;
        var transform = _file.Read(transformId);
        long gameObject = PathId(transform["m_GameObject"]);
        var spawner = MonoBehaviours().FirstOrDefault(m => m.Script == "NpcSpawner" && m.GameObjectPathId == gameObject);
        return spawner != null ? Convert.ToInt32(spawner.Fields.GetValueOrDefault("id", 0)) : 0;
    }

    /// <summary>World position and size of the trigger's box.</summary>
    private (float[] Center, float[] Size) Volume(MonoBehaviourInfo trigger)
    {
        var position = trigger.WorldPosition;
        var center = new[] { position.X, position.Y, position.Z };
        var size = new[] { 1f, 1f, 1f };

        if (trigger.GameObjectPathId == 0 || !_file.Objects.ContainsKey(trigger.GameObjectPathId))
            return (center, size);

        var go = _file.Read(trigger.GameObjectPathId);
        long colliderId = FindComponent(go, ClassBoxCollider);
        long sphereId = FindComponent(go, ClassSphereCollider);
        long transformId = FindComponent(go, ClassTransform);
        var scale = Matrix4x4.Decompose(World(transformId), out Vector3 s, out _, out _) ? s : Vector3.One;

        Dictionary<string, object> boxCenter;
        if (colliderId != 0 && _file.Objects.ContainsKey(colliderId))
        {
            var collider = _file.Read(colliderId);
            var boxSize = (Dictionary<string, object>)collider["m_Size"];
            boxCenter = (Dictionary<string, object>)collider["m_Center"];
            size = new[] { F(boxSize, "x") * s.X, F(boxSize, "y") * s.Y, F(boxSize, "z") * s.Z };
        }
        else if (sphereId != 0 && _file.Objects.ContainsKey(sphereId))
        {
            // A sphere is exported as the box around it (ActiveCollisionTrigger.DoExport).
            var sphere = _file.Read(sphereId);
            float diameter = 2 * (float)sphere["m_Radius"];
            boxCenter = (Dictionary<string, object>)sphere["m_Center"];
            size = new[] { diameter * s.X, diameter * s.Y, diameter * s.Z };
        }
        else
        {
            return (center, size);
        }

        // The collider's center is in the object's own space: turn and scale it with the object.
        var worldCenter = Vector3.Transform(new Vector3(F(boxCenter, "x"), F(boxCenter, "y"), F(boxCenter, "z")), World(transformId));
        center = new[] { worldCenter.X, worldCenter.Y, worldCenter.Z };
        return (center, size);
    }

    private float RotationY(MonoBehaviourInfo trigger)
    {
        if (trigger.GameObjectPathId == 0 || !_file.Objects.ContainsKey(trigger.GameObjectPathId))
            return 0f;
        var go = _file.Read(trigger.GameObjectPathId);
        long transformId = FindComponent(go, ClassTransform);
        if (transformId == 0)
            return 0f;
        if (!Matrix4x4.Decompose(World(transformId), out _, out Quaternion rotation, out _))
            return 0f;
        return (float)(Math.Atan2(2.0 * (rotation.W * rotation.Y + rotation.X * rotation.Z),
            1.0 - 2.0 * (rotation.Y * rotation.Y + rotation.X * rotation.X)) * 180.0 / Math.PI);
    }

    private string ResolveScript(object pptr)
    {
        var p = (Dictionary<string, object>)pptr;
        int fileId = (int)p["m_FileID"];
        if (fileId <= 0 || fileId > _file.Externals.Count)
            return "<local>";
        string guid = _file.Externals[fileId - 1].Guid;
        return _scriptGuids.TryGetValue(guid, out string name) ? name : "<" + guid + ">";
    }

    private static long PathId(object pptr)
    {
        var p = (Dictionary<string, object>)pptr;
        return Convert.ToInt64(p["m_PathID"]);
    }

    private static long FindComponent(Dictionary<string, object> go, int classId)
    {
        foreach (var entry in (List<object>)go["m_Component"])
        {
            var pair = (Dictionary<string, object>)entry;
            if ((int)pair["first"] == classId)
                return PathId(pair["second"]);
        }
        return 0;
    }

    private Matrix4x4 World(long transformId)
    {
        if (_worldCache.TryGetValue(transformId, out var cached))
            return cached;

        var t = _file.Read(transformId);
        var q = (Dictionary<string, object>)t["m_LocalRotation"];
        var p = (Dictionary<string, object>)t["m_LocalPosition"];
        var s = (Dictionary<string, object>)t["m_LocalScale"];
        var local =
            Matrix4x4.CreateScale(F(s, "x"), F(s, "y"), F(s, "z")) *
            Matrix4x4.CreateFromQuaternion(new Quaternion(F(q, "x"), F(q, "y"), F(q, "z"), F(q, "w"))) *
            Matrix4x4.CreateTranslation(F(p, "x"), F(p, "y"), F(p, "z"));

        long father = PathId(t["m_Father"]);
        var world = father != 0 && _file.Objects.ContainsKey(father) ? local * World(father) : local;
        _worldCache[transformId] = world;
        return world;
    }

    private static float F(Dictionary<string, object> d, string k) => (float)d[k];
}
