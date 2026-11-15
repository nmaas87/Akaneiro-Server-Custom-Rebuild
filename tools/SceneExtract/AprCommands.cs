using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SceneExtract;

/// <summary>
/// apr-* commands: extract GameData for the April 2013 fat client ("win04162") from its own
/// Unity 4 asset bundles. Unlike the 2014 pipeline (Program.cs) these inputs are built player
/// data: map scenes and prefabs ship inside "UnityWeb" bundles with type trees (so objects still
/// decode generically), while the player files (mainData & friends) carry no type trees.
///
/// The April maps contain no level-logic components: NpcSpawner / Trigger_* / InteractiveObj
/// placements and mission prefabs were 2013-server-side data and are absent from the client;
/// the spawner/trigger/mission commands verify that and emit empty per-map lists.
/// </summary>
internal static class AprCommands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: SceneExtract apr-maps|apr-dump|apr-relations|apr-regions <winDir> [extra]\n" +
                "       SceneExtract apr-scenes|apr-spawners|apr-triggers|apr-monsters|apr-missions|apr-breakables <winDir> <out.json>");
            return 1;
        }
        string winDir = args[1];
        string bundles = Path.Combine(winDir, "assetbundles");

        switch (args[0])
        {
            case "apr-maps": foreach (string m in Maps(bundles)) Console.WriteLine(m); return 0;
            case "apr-obj": return DumpObject(bundles, args);
            case "apr-dump": return Dump(bundles, args);
            case "apr-relations": return Relations(bundles);
            case "apr-regions": return Regions(winDir);
            case "apr-scenes": return Scenes(bundles, args[2]);
            case "apr-spawners": return Empties(bundles, args[2], "spawners", SpawnerMarkers);
            case "apr-triggers": return Empties(bundles, args[2], "triggers", TriggerMarkers);
            case "apr-monsters": return Monsters(bundles, args[2]);
            case "apr-missions": return Missions(bundles, args[2]);
            case "apr-breakables": return Breakables(bundles, args[2]);
            case "apr-threats": return Threats(bundles);
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 1;
        }
    }

    /// <summary>The 20 level bundles of the April client (18 area maps and the two village hubs).</summary>
    private static List<string> Maps(string bundles) =>
        Directory.EnumerateFiles(bundles, "*.unity3d")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^(A[1-6]_M[1-3]|Hub_Village(_Tutorial)?)\.unity3d$"))
            .OrderBy(f => f)
            .ToList();

    private static string MapName(string bundlePath) => Path.GetFileNameWithoutExtension(bundlePath);

    // --- inspection helpers -----------------------------------------------------------------

    private static int DumpObject(string bundles, string[] args)
    {
        string bundlePath = args[2].Contains('.') ? Path.Combine(bundles, args[2]) : Path.Combine(bundles, args[2] + ".unity3d");
        long pathId = long.Parse(args[3]);
        var bundle = AprBundle.Load(bundlePath);
        foreach (var file in bundle.Files)
            if (file.Objects.TryGetValue(pathId, out var info))
            {
                Console.WriteLine($"{file.Path.Split('/')[^1]}: classId={info.ClassId} typeId={info.TypeId}");
                try { Console.WriteLine(JsonSerializer.Serialize(file.Read(pathId), new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { Console.WriteLine($"unreadable: {ex.Message}"); }
            }
        return 0;
    }

    private static int Dump(string bundles, string[] args)
    {
        string bundlePath = args[2].Contains('.') ? Path.Combine(bundles, args[2]) : Path.Combine(bundles, args[2] + ".unity3d");
        var bundle = AprBundle.Load(bundlePath);
        foreach (var mb in bundle.AllMonoBehaviours().OrderBy(m => m.Script).ThenBy(m => m.GameObjectName))
        {
            if (args.Length > 3 && mb.Script != args[3])
                continue;
            Console.WriteLine($"{mb.Script,-34} {mb.GameObjectName,-40} ({mb.WorldPosition.X:0.00}, {mb.WorldPosition.Y:0.00}, {mb.WorldPosition.Z:0.00}) [{mb.File.Path.Split('/')[^1]}]");
            if (args.Length > 4)
                Console.WriteLine(JsonSerializer.Serialize(mb.Fields, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    /// <summary>
    /// Per-map StringHolder "Relation" tables: TypeId -> prefab bundle for the monsters and
    /// breakable props of that map (the 2013 server addressed content through these ids).
    /// </summary>
    private static int Threats(string bundles)
    {
        // The bounty panel's per-mission threat rows: _UI_CS_MapInfo prefabs carry levelList[]
        // with ID/threatUp/threatDown - the karma shard prices the panel shows on its + and -
        // buttons (buyThreatLevelByKarma).
        foreach (string bundlePath in Directory.EnumerateFiles(bundles, "*.*")
            .Where(f => f.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f))
        {
            AprBundle bundle;
            try { bundle = AprBundle.Load(bundlePath); }
            catch { continue; }
            foreach (var mb in bundle.AllMonoBehaviours())
            {
                if (!mb.Fields.TryGetValue("levelList", out object list) || list is not List<object> items)
                    continue;
                bool interesting = items.Any(i => i is Dictionary<string, object> d &&
                    (d.ContainsKey("threatUp") || d.ContainsKey("threatDown")));
                if (!interesting)
                    continue;
                Console.WriteLine($"# {mb.Script} on {mb.GameObjectName} [{Path.GetFileName(bundlePath)}]");
                Console.WriteLine(JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false }));
            }
        }
        return 0;
    }

    /// <summary>
    /// Per-map StringHolder "Relation" tables: TypeId -> prefab bundle for the monsters and
    /// breakable props of that map (the 2013 server addressed content through these ids).
    /// </summary>
    private static int Relations(string bundles)
    {
        var result = new SortedDictionary<string, Dictionary<string, string>>();
        foreach (string file in Directory.EnumerateFiles(bundles, "*Relation.assetbundle").OrderBy(f => f))
        {
            string map = Path.GetFileNameWithoutExtension(file); // "A1_M2Relation.assetbundle"
            map = map.EndsWith("Relation") ? map[..^"Relation".Length] : map;
            var content = RelationContent(file);
            if (content == null)
                continue;
            result[map] = ParseRelation(content);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    /// <summary>The StringHolder.content string array of a <map>Relation.assetbundle, or null.</summary>
    private static List<object> RelationContent(string relationBundle)
    {
        var bundle = AprBundle.Load(relationBundle);
        var holder = bundle.AllMonoBehaviours().FirstOrDefault(m => m.Fields.TryGetValue("content", out object c) && c is List<object>);
        return holder?.Fields["content"] as List<object>;
    }

    /// <summary>Sections of a Relation table: "MonsterType" / "BreakableType" followed by "id:name" entries.</summary>
    private static Dictionary<string, string> ParseRelation(List<object> content)
    {
        var map = new Dictionary<string, string>();
        string section = "";
        foreach (string entry in content.OfType<string>())
        {
            if (!entry.Contains(':'))
            {
                section = entry;
                continue;
            }
            map[section + ":" + entry.Split(':', 2)[0]] = entry.Split(':', 2)[1];
        }
        return map;
    }

    private static IEnumerable<KeyValuePair<string, string>> RelationSection(Dictionary<string, string> relation, string section) =>
        relation.Where(kv => kv.Key.StartsWith(section + ":")).Select(kv => new KeyValuePair<string, string>(kv.Key.Substring(section.Length + 1), kv.Value));

    // --- scenes.json ------------------------------------------------------------------------

    private static int Scenes(string bundles, string outPath)
    {
        var result = new SortedDictionary<string, object>();
        foreach (string file in Maps(bundles))
        {
            var bundle = AprBundle.Load(file);
            var scene = bundle.SceneFile();
            var mbs = bundle.MonoBehaviours(scene).ToList();

            // The April scenes have no CreatePlayerForLD marker; the player character object
            // (with PreLoadPlayer on it) sits at the spot the server spawns the player.
            var player = mbs.FirstOrDefault(m => m.Script == "PreLoadPlayer")
                ?? mbs.FirstOrDefault(m => m.GameObjectName.StartsWith("CH_Aka", StringComparison.OrdinalIgnoreCase));
            if (player == null)
            {
                Console.Error.WriteLine($"{file}: no player object found, skipped");
                continue;
            }

            // Village NPCs. The April ShopNpc prefabs serialize npcType 0 (the enum was still
            // unused then); the client identified them by npcName instead.
            var npcs = mbs.Where(m => m.Script == "ShopNpc" || m.Script == "NPC_Well").Select(m => new
            {
                NpcId = Convert.ToInt32(m.Fields.GetValueOrDefault("npcType", 0)),
                Name = m.Fields.GetValueOrDefault("npcName") as string is { Length: > 0 } n ? n : m.GameObjectName,
                GameObject = m.GameObjectName,
                Position = new[] { m.WorldPosition.X, m.WorldPosition.Y, m.WorldPosition.Z },
            }).OrderBy(n => n.GameObject).ToList();

            var p = player.WorldPosition;
            result[MapName(file)] = new
            {
                Spawn = new[] { p.X, p.Y, p.Z },
                CollectTargets = Array.Empty<object>(),
                Npcs = npcs,
            };
            Console.WriteLine($"{MapName(file)}: spawn ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}), {npcs.Count} NPCs");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"{result.Count} scenes written to {outPath}");
        return 0;
    }

    // --- spawners.json / triggers.json (verified absent in the April client) ----------------

    private static bool HasServerPayload(AprBundle.MonoBehaviourInfo m)
    {
        bool NonEmptyRefs(string field) =>
            m.Fields.GetValueOrDefault(field) is List<object> list && list.OfType<Dictionary<string, object>>().Any(p => AprBundle.PathIdOf(p) != 0);
        bool NonEmpty(string field) => m.Fields.GetValueOrDefault(field) is List<object> list && list.Count > 0;
        return m.Script == "Trigger_Unified" || m.Script == "UnifiedEventData_Spawn" || m.Script == "UnifiedEventData_Teleport"
            || NonEmptyRefs("TriggerNpcList") || NonEmpty("AnimDataArrays") || NonEmptyRefs("EnableTriggerList")
            || NonEmptyRefs("DisableSpawnerList") || NonEmpty("SpeechList");
    }

    private static List<AprBundle.MonoBehaviourInfo> SpawnerMarkers(AprBundle bundle) => bundle.AllMonoBehaviours()
        .Where(m => (m.Script == "NpcSpawner" || m.Fields.ContainsKey("SpawnerList"))
                    && m.Fields.GetValueOrDefault("SpawnerList") is List<object> list && list.Count > 0)
        .ToList();

    private static List<AprBundle.MonoBehaviourInfo> TriggerMarkers(AprBundle bundle)
    {
        var hits = bundle.AllMonoBehaviours()
            .Where(m => m.Script == "Trigger_Unified" || m.Fields.ContainsKey("unityID_") || m.Fields.ContainsKey("TriggerNpcList"))
            .ToList();
        foreach (var m in hits.Where(m => !HasServerPayload(m)))
            Console.Error.WriteLine($"  note: {m.Script} {m.GameObjectName} (id {m.Fields.GetValueOrDefault("id", m.Fields.GetValueOrDefault("unityID_"))}) has empty references - no server payload (like the 2014 extractor, such client-only triggers are skipped)");
        return hits.Where(HasServerPayload).ToList();
    }

    private static int Empties(string bundles, string outPath, string what, Func<AprBundle, List<AprBundle.MonoBehaviourInfo>> marker)
    {
        var perScene = new SortedDictionary<string, object>();
        foreach (string file in Maps(bundles))
        {
            var found = marker(AprBundle.Load(file));
            if (found.Count != 0)
            {
                foreach (var m in found)
                    Console.Error.WriteLine($"  {what}: {m.Script} {m.GameObjectName} carries spawner/trigger payload - the extractor cannot place it, refusing to guess");
                return 1;
            }
            perScene[MapName(file)] = Array.Empty<object>();
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(perScene, Json));
        Console.WriteLine($"{what}: 0 components with payload in any April map (the placement data was 2013-server-side); {perScene.Count} empty scene lists written to {outPath}");
        return 0;
    }

    // --- monsters.json ----------------------------------------------------------------------

    private static int Monsters(string bundles, string outPath)
    {
        // Which monsters exist is exactly what the per-map Relation tables list; every other
        // CH_* bundle is player gear or an NPC model.
        var wanted = new SortedDictionary<int, string>(); // TypeId -> prefab bundle name
        foreach (string file in Directory.EnumerateFiles(bundles, "*Relation.assetbundle").OrderBy(f => f))
        {
            var content = RelationContent(file);
            if (content == null)
                continue;
            foreach (var kv in RelationSection(ParseRelation(content), "MonsterType"))
            {
                int id = int.Parse(kv.Key);
                if (!wanted.TryGetValue(id, out string known))
                    wanted[id] = kv.Value;
                else if (known != kv.Value)
                    Console.Error.WriteLine($"monster {id}: Relation says both {known} and {kv.Value}; keeping {known}");
            }
        }

        var monsters = new List<object>();
        foreach (var kv in wanted)
        {
            string bundlePath = Path.Combine(bundles, kv.Value);
            if (!File.Exists(bundlePath))
            {
                Console.Error.WriteLine($"monster {kv.Key}: Relation references missing bundle {kv.Value}");
                continue;
            }
            var bundle = AprBundle.Load(bundlePath);
            var candidates = bundle.AllMonoBehaviours()
                .Where(m => m.Fields.ContainsKey("TypeID") && m.Fields.ContainsKey("maxHealth")).ToList();
            var npc = candidates.FirstOrDefault(m => Convert.ToInt32(m.Fields["TypeID"]) == kv.Key);
            var file = npc?.File;
            if (npc == null && candidates.Count == 1)
            {
                // The Relation table is authoritative for the TypeId the server announced; the
                // prefab's own component just carries a different default.
                Console.Error.WriteLine($"monster {kv.Key}: {kv.Value} component has TypeID {Convert.ToInt32(candidates[0].Fields["TypeID"])}; using the Relation id");
                npc = candidates[0];
                file = npc.File;
            }
            if (npc == null)
            {
                Console.Error.WriteLine($"monster {kv.Key}: no monster component in {kv.Value}");
                continue;
            }

            var loot = bundle.MonoBehaviours(file).FirstOrDefault(m => m.GameObjectPathId == npc.GameObjectPathId && m.Fields.ContainsKey("CanDropLoot"))
                ?? bundle.AllMonoBehaviours().FirstOrDefault(m => m.Fields.ContainsKey("CanDropLoot"));

            int Int(string field) => Convert.ToInt32(npc.Fields.GetValueOrDefault(field, 0));
            float Flt(string field) => Convert.ToSingle(npc.Fields.GetValueOrDefault(field, 0f));

            // Ranged monsters throw a projectile (their toss ability names it).
            var toss = bundle.MonoBehaviours(file).FirstOrDefault(m => m.Fields.ContainsKey("ProjectilePrefab"));

            monsters.Add(new
            {
                TypeId = kv.Key,
                Name = npc.Fields.GetValueOrDefault("NpcName", "") as string,
                Prefab = Path.GetFileNameWithoutExtension(kv.Value),
                Script = npc.Script,
                MaxHealth = Int("maxHealth"),
                Level = Int("EnemyLevel"),
                LootLevel = Int("LootLevel"),
                Xp = Int("XP"),
                KarmaMin = Int("MinKarmaValue"),
                KarmaMax = Int("MaxKarmaValue"),
                IsBoss = Int("IsBoss") != 0,
                IsWanted = Int("IsWanted") != 0,
                WantedXp = Int("RewardWantedExp"),
                WantedKarma = Int("RewardWantedSk"),
                VisionRadius = Flt("VisionRadius"),
                AlertDistance = Flt("AlertDistance"),
                WanderSpeed = Flt("SpeedWanderState"),
                InitState = Int("InitState"),
                AttackSpeed = AttackState(npc, "SpeedAttackState"),
                AttackDamage = FirstAttack(npc, "AttackDamage"),
                AttackRange = FirstAttack(npc, "MaxAttackDistance"),
                Attacks = Attacks(npc),
                ThrowAbility = toss == null ? 0 : Convert.ToInt32(toss.Fields.GetValueOrDefault("id", 0)),
                ThrowRelease = toss == null ? 0f : bundle.ClipFirstEvent(file, toss.Fields.GetValueOrDefault("CastAnimation")),
                ThrowCastLength = toss == null ? 0f : bundle.ClipSeconds(file, toss.Fields.GetValueOrDefault("CastAnimation")),
                ThrowSpeed = toss == null ? 0f : bundle.PrefabFloat(file, toss.Fields.GetValueOrDefault("ProjectilePrefab"), "speed"),
                WakeUpSeconds = WakeUpSeconds(bundle, file, npc),
                SpawnSeconds = SpawnSeconds(bundle, file, npc),
                CanDropLoot = loot != null && Convert.ToInt32(loot.Fields.GetValueOrDefault("CanDropLoot", 0)) != 0,
                EquipmentDrops = DropList(loot, "RandomDropItemList", "ItemNumber"),
                // The April LootDrop component has no ConsumableDropList (added in 2014).
                ConsumableDrops = DropList(loot, "ConsumableDropList", "DropAmount"),
                Materials = Materials(bundle, file, loot),
                UniqueDrops = UniqueDrops(loot),
            });
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(monsters, Json));
        Console.WriteLine($"{monsters.Count} monsters written to {outPath}");
        return 0;
    }

    private static object[] Attacks(AprBundle.MonoBehaviourInfo npc)
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

    private static float AttackState(AprBundle.MonoBehaviourInfo npc, string field) =>
        npc.Fields.GetValueOrDefault("AttackState") is Dictionary<string, object> state && state.TryGetValue(field, out object v)
            ? Convert.ToSingle(v) : 0f;

    private static float FirstAttack(AprBundle.MonoBehaviourInfo npc, string field)
    {
        if (npc.Fields.GetValueOrDefault("AttackState") is not Dictionary<string, object> state ||
            state.GetValueOrDefault("AttackArray") is not List<object> attacks || attacks.Count == 0 ||
            attacks[0] is not Dictionary<string, object> attack)
            return 0f;
        return attack.TryGetValue(field, out object v) ? Convert.ToSingle(v) : 0f;
    }

    private static float WakeUpSeconds(AprBundle bundle, UnitySerializedFile file, AprBundle.MonoBehaviourInfo npc)
    {
        float clip = bundle.ClipSeconds(file, npc.Fields.GetValueOrDefault("WakeupAnim"));
        if (clip <= 0)
            return 0;
        var extra = npc.Fields.GetValueOrDefault("AdditinalResetTime") as Dictionary<string, object>;
        return clip + Convert.ToSingle(extra?.GetValueOrDefault("WakeupTime", 0f) ?? 0f);
    }

    private static List<float> SpawnSeconds(AprBundle bundle, UnitySerializedFile file, AprBundle.MonoBehaviourInfo npc)
    {
        var result = new List<float>();
        if (npc.Fields.GetValueOrDefault("SpawnAnims") is not List<object> clips)
            return result;
        var extra = (npc.Fields.GetValueOrDefault("AdditinalResetTime") as Dictionary<string, object>)?.GetValueOrDefault("SpawnResetTimes") as List<object>;
        for (int i = 0; i < clips.Count; i++)
        {
            float add = extra != null && i < extra.Count ? Convert.ToSingle(extra[i]) : 0f;
            result.Add(bundle.ClipSeconds(file, clips[i]) + add);
        }
        return result;
    }

    private static object[] DropList(AprBundle.MonoBehaviourInfo loot, string field, string amountField)
    {
        if (loot == null || loot.Fields.GetValueOrDefault(field) is not List<object> entries)
            return Array.Empty<object>();
        return entries.OfType<Dictionary<string, object>>().Select(e => (object)new
        {
            Amount = Convert.ToInt32(e.GetValueOrDefault(amountField, 0)),
            Chance = Convert.ToInt32(e.GetValueOrDefault("DropChance", 0)),
        }).ToArray();
    }

    private static object[] UniqueDrops(AprBundle.MonoBehaviourInfo loot)
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

    private static List<object> Materials(AprBundle bundle, UnitySerializedFile file, AprBundle.MonoBehaviourInfo loot)
    {
        var result = new List<object>();
        if (loot == null || loot.Fields.GetValueOrDefault("MaterialDropList") is not List<object> entries)
            return result;
        foreach (var e in entries.OfType<Dictionary<string, object>>())
        {
            if (e.GetValueOrDefault("MaterialPrefab") is not Dictionary<string, object> pptr)
                continue;
            var item = bundle.PrefabFields(file, pptr)
                .FirstOrDefault(f => f.ContainsKey("TypeID") && f.ContainsKey("PrefabID"));
            if (item == null)
                continue;
            result.Add(new
            {
                ItemId = Convert.ToInt32(item["TypeID"]),
                PrefabId = Convert.ToInt32(item["PrefabID"]),
                Count = Convert.ToInt32(e.GetValueOrDefault("MaterialNumber", 1)),
                Chance = Convert.ToInt32(e.GetValueOrDefault("DropChance", 100)),
            });
        }
        return result;
    }

    private static object LootOf(AprBundle.MonoBehaviourInfo loot) => loot == null ? null : new
    {
        CanDropLoot = Convert.ToInt32(loot.Fields.GetValueOrDefault("CanDropLoot", 0)) != 0,
        EquipmentDrops = DropList(loot, "RandomDropItemList", "ItemNumber"),
        ConsumableDrops = DropList(loot, "ConsumableDropList", "DropAmount"),
        UniqueDrops = UniqueDrops(loot),
    };

    // --- missions.json (verified absent in the April client) --------------------------------

    private static int Missions(string bundles, string outPath)
    {
        // 2014 mission definitions are prefabs with a branchArray field. Scan every scene and
        // prefab bundle for that marker; the April client has none (missions were server data,
        // shipped only as the MissionDescription table which the server already has).
        int found = 0;
        foreach (string file in Directory.EnumerateFiles(bundles, "*.unity3d").OrderBy(f => f))
        {
            try
            {
                found += AprBundle.Load(file).AllMonoBehaviours().Count(m => m.Fields.ContainsKey("branchArray"));
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is InvalidDataException || ex is IOException
                                      || ex is IndexOutOfRangeException || ex is ArgumentException || ex is OutOfMemoryException)
            {
                // not a readable UnityWeb bundle
            }
        }
        if (found != 0)
        {
            Console.Error.WriteLine($"{found} mission prefabs found in the bundles - unexpected; refusing to guess");
            return 1;
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(new Dictionary<int, object>(), Json));
        Console.WriteLine($"missions: 0 mission prefab components in any April bundle (missions were 2013-server-side data); wrote empty missions.json to {outPath}");
        return 0;
    }

    // --- breakables.json --------------------------------------------------------------------

    /// <summary>
    /// The April maps contain no placed InteractiveObj components: the 2013 server announced
    /// breakables itself and the client built them from these prefabs by TypeId. So this is the
    /// per-map set of breakable TYPE definitions (no positions - the client never knew them).
    /// </summary>
    private static int Breakables(string bundles, string outPath)
    {
        var perScene = new SortedDictionary<string, object>();
        foreach (string file in Maps(bundles))
        {
            string map = MapName(file);
            string relationPath = Path.Combine(bundles, map + "Relation.assetbundle");
            var entries = new List<object>();
            if (File.Exists(relationPath))
            {
                var content = RelationContent(relationPath);
                if (content != null)
                    foreach (var kv in RelationSection(ParseRelation(content), "BreakableType"))
                    {
                        string propPath = Path.Combine(bundles, kv.Value);
                        if (!File.Exists(propPath))
                        {
                            Console.Error.WriteLine($"{map}: Relation references missing prop bundle {kv.Value}");
                            continue;
                        }
                        var bundle = AprBundle.Load(propPath);
                        int typeId = int.Parse(kv.Key);
                        var candidates = bundle.AllMonoBehaviours()
                            .Where(m => m.Fields.ContainsKey("TypeID") && m.Fields.ContainsKey("Health")).ToList();
                        var prop = candidates.FirstOrDefault(m => Convert.ToInt32(m.Fields["TypeID"]) == typeId);
                        if (prop == null && candidates.Count == 1)
                        {
                            // The Relation table names this prefab for the TypeId the server announced.
                            Console.Error.WriteLine($"{map}: {kv.Value} component has TypeID {Convert.ToInt32(candidates[0].Fields["TypeID"])}; using the Relation id {typeId}");
                            prop = candidates[0];
                        }
                        if (prop == null)
                        {
                            Console.Error.WriteLine($"{map}: no breakable component for TypeId {typeId} in {kv.Value}");
                            continue;
                        }
                        var loot = bundle.MonoBehaviours(prop.File).FirstOrDefault(m => m.GameObjectPathId == prop.GameObjectPathId && m.Fields.ContainsKey("CanDropLoot"));

                        int Int(string field) => Convert.ToInt32(prop.Fields.GetValueOrDefault(field, 0));
                        entries.Add(new
                        {
                            Name = prop.GameObjectName,
                            Script = prop.Script,
                            TypeId = int.Parse(kv.Key),
                            Health = Math.Max(1, Int("Health")),
                            Exp = Int("Exp"),
                            LootLevel = Int("LootLevel"),
                            KarmaMin = (int)Convert.ToSingle(prop.Fields.GetValueOrDefault("MinKarmaValue", 0f)),
                            KarmaMax = (int)Convert.ToSingle(prop.Fields.GetValueOrDefault("MaxKarmaValue", 0f)),
                            AppearChance = prop.Fields.ContainsKey("AppearChance") ? Int("AppearChance") : 100,
                            DestroyOnDeath = Int("DestroyOnDeath") != 0,
                            Looping = false,
                            LoopCount = 0,
                            Links = Array.Empty<object>(),
                            Animations = Array.Empty<object>(),
                            Loot = LootOf(loot),
                            DeathResults = Array.Empty<object>(),
                        });
                    }
            }
            perScene[map] = entries;
            Console.WriteLine($"{map}: {entries.Count} breakable types");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(perScene, Json));
        Console.WriteLine($"breakable type definitions of {perScene.Count} maps written to {outPath}");
        return 0;
    }

    // --- world map regions (mainData, no type trees) ----------------------------------------

    private static int Regions(string winDir)
    {
        string dataDir = Path.Combine(winDir, "Akaneiro_Data");
        var main = new UnitySerializedFile(Path.Combine(dataDir, "mainData"));

        // Externals point at the other player files ("sharedassets0.assets" & friends).
        var filesByExternal = new List<UnitySerializedFile>();
        foreach (var ext in main.Externals)
        {
            string candidate = string.IsNullOrEmpty(ext.PathName) ? null
                : Directory.EnumerateFiles(dataDir).FirstOrDefault(f => Path.GetFileName(f).Equals(ext.PathName, StringComparison.OrdinalIgnoreCase));
            UnitySerializedFile file = null;
            if (candidate != null)
                try { file = new UnitySerializedFile(candidate); }
                catch (Exception ex) when (ex is NotSupportedException || ex is IndexOutOfRangeException || ex is ArgumentException) { }
            filesByExternal.Add(file);
        }

        var scriptNames = new Dictionary<(UnitySerializedFile, long), string>();
        var classCounts = new SortedDictionary<string, int>();
        string ClassName(UnitySerializedFile file, long pathId)
        {
            if (!scriptNames.TryGetValue((file, pathId), out string name))
            {
                name = "<unknown>";
                try
                {
                    if (file.Objects[pathId].ClassId == 115)
                    {
                        // Built-file MonoScript: string name, zero u32 + u32 hash, string class
                        // (usually identical), string assembly. The first string is the class name.
                        var r = new NoTreeReader(file.Raw(pathId));
                        name = r.String(256);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is ArgumentException) { }
                scriptNames[(file, pathId)] = name;
            }
            return name;
        }

        var regions = new List<(int RegionID, string Name, int MapIndex, int RegionIndex)>();
        var levelItems = new List<(int LevelIndex, int[] MissionIDs, int[] MaxLevels, int[] LastTime)>();
        var bigRegions = new List<(int Index, int Icons)>();

        foreach (var info in main.Objects.Values.Where(o => o.ClassId == 114))
        {
            byte[] raw = main.Raw(info.PathId);
            string cls;
            long scriptId;
            int scriptFileId;
            try
            {
                var r = new NoTreeReader(raw);
                r.Int(); r.Int();          // m_GameObject
                r.Byte(); r.Align();       // m_Enabled
                scriptFileId = r.Int();
                scriptId = r.Int();        // m_Script
                r.String();                // m_Name (instance name)
            }
            catch (Exception) { classCounts["<header-parse-fail>"] = classCounts.GetValueOrDefault("<header-parse-fail>") + 1; continue; }

            // Unity PPtr semantics: m_FileID 0 = this file, n >= 1 = Externals[n - 1].
            UnitySerializedFile scriptFile = scriptFileId == 0 ? main
                : scriptFileId >= 1 && scriptFileId <= filesByExternal.Count ? filesByExternal[scriptFileId - 1] : null;
            if (scriptFile == null || !scriptFile.Objects.ContainsKey(scriptId))
            {
                classCounts[$"<unresolved-script-file-{scriptFileId}>"] = classCounts.GetValueOrDefault($"<unresolved-script-file-{scriptFileId}>") + 1;
                continue;
            }
            cls = ClassName(scriptFile, scriptId);
            classCounts[cls] = classCounts.GetValueOrDefault(cls) + 1;

            switch (cls)
            {
                case "_UI_CS_WorldRegion":
                {
                    var m = TryDecode(raw, r =>
                    {
                        int regionId = r.Int();
                        if (regionId < 0 || regionId > 100000)
                            throw new InvalidOperationException();
                        r.Pptr();
                        r.PptrList(64);
                        string desc = r.String(2048);
                        r.Pptr();
                        int mapIndex = r.Int();
                        r.Pptr();
                        int regionIndex = r.Int();
                        r.Pptr(); r.Pptr(); r.Pptr();
                        r.Bool(); r.Bool(); r.Bool();
                        r.Pptr(); r.Pptr(); r.Pptr();
                        r.Bool();
                        return (regionId, desc, mapIndex, regionIndex);
                    });
                    if (m != null)
                        regions.Add((m.Value.Item1, m.Value.Item2, m.Value.Item3, m.Value.Item4));
                    else
                        Console.Error.WriteLine($"mainData: could not decode a _UI_CS_WorldRegion object (layout mismatch)");
                    break;
                }
                case "_UI_CS_RegionLevel":
                {
                    var m = TryDecode(raw, r =>
                    {
                        for (int i = 0; i < 7; i++)
                            r.PptrList(64);
                        int[] starCount = r.IntList(64);
                        int levelIndex = r.Int();
                        int[] missionIds = r.IntList(64);
                        int[] lastTime = r.IntList(64);
                        int[] maxLevel = r.IntList(64);
                        r.PptrList(64); // LevelName
                        r.Bool();
                        return (levelIndex, missionIds, maxLevel, lastTime);
                    });
                    if (m != null)
                        levelItems.Add(m.Value);
                    else
                        Console.Error.WriteLine("mainData: could not decode a _UI_CS_RegionLevel object (layout mismatch)");
                    break;
                }
                case "_UI_CS_WorldBigRegion":
                {
                    var m = TryDecode(raw, r =>
                    {
                        r.Pptr();
                        int index = r.Int();
                        int icons = r.PptrList(64);
                        r.PptrList(64);
                        return (index, icons);
                    });
                    if (m != null)
                        bigRegions.Add(m.Value);
                    else
                        Console.Error.WriteLine("mainData: could not decode a _UI_CS_WorldBigRegion object (layout mismatch)");
                    break;
                }
            }
        }

        Console.WriteLine("MonoBehaviour classes in mainData:");
        foreach (var (cls, count) in classCounts.OrderByDescending(kv => kv.Value).Take(40))
            Console.WriteLine($"  {count,5} {cls}");
        Console.WriteLine("world regions (mainData _UI_CS_WorldRegion):");
        foreach (var region in regions.OrderBy(r => r.RegionID))
            Console.WriteLine($"  RegionID {region.RegionID}: mapIndex={region.MapIndex} regionIndex={region.RegionIndex} desc=\"{region.Name}\"");
        Console.WriteLine("region level rows (_UI_CS_RegionLevel):");
        foreach (var item in levelItems.OrderBy(l => l.LevelIndex))
            Console.WriteLine($"  level {item.LevelIndex}: MissionID [{string.Join(",", item.MissionIDs)}] MaxLevel [{string.Join(",", item.MaxLevels)}] lastTime [{string.Join(",", item.LastTime)}]");
        Console.WriteLine("big regions (_UI_CS_WorldBigRegion):");
        foreach (var big in bigRegions.OrderBy(b => b.Index))
            Console.WriteLine($"  index {big.Index}: {big.Icons} region icons");
        return 0;
    }

    /// <summary>Skips the MonoBehaviour header and runs layout; only a value that consumes the object exactly.</summary>
    private static T? TryDecode<T>(byte[] raw, Func<NoTreeReader, T> layout) where T : struct
    {
        try
        {
            var r = new NoTreeReader(raw);
            r.Int(); r.Int();      // m_GameObject
            r.Byte(); r.Align();   // m_Enabled
            r.Int(); r.Int();      // m_Script
            r.String();            // m_Name
            T value = layout(r);
            return r.AtEnd ? value : (T?)null;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is ArgumentException)
        {
            return null;
        }
    }

    private sealed class NoTreeReader
    {
        private readonly byte[] _d;
        private int _pos;

        public NoTreeReader(byte[] d) => _d = d;

        public bool AtEnd => _pos == _d.Length;

        public byte Byte() => _d[_pos++];

        public int Int()
        {
            int v = BitConverter.ToInt32(_d, _pos);
            _pos += 4;
            return v;
        }

        public void Align() => _pos = (_pos + 3) & ~3;

        public int Pptr()
        {
            Int(); Int();
            return 0;
        }

        public int PptrList(int max)
        {
            int count = Int();
            if (count < 0 || count > max)
                throw new InvalidOperationException("bad PPtr list");
            _pos += 8 * count;
            return count;
        }

        public int[] IntList(int max)
        {
            int count = Int();
            if (count < 0 || count > max)
                throw new InvalidOperationException("bad int list");
            var list = new int[count];
            for (int i = 0; i < count; i++)
                list[i] = Int();
            return list;
        }

        public bool Bool()
        {
            byte b = Byte();
            Align();
            return b != 0;
        }

        public string String(int max = 4096)
        {
            int length = Int();
            if (length < 0 || length > max || _pos + length > _d.Length)
                throw new InvalidOperationException("not a string");
            string s = Encoding.UTF8.GetString(_d, _pos, length);
            _pos += length;
            Align();
            return s;
        }
    }
}
