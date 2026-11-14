using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// The mission map's progression:
    ///   - which maps are open: the first one, and whatever finished missions open up
    ///     (MissionDefinition.NewMissionIds: the same mission again, its next threat, the next map);
    ///   - how long a finished map rests before it can be played again: the n-th run within a day
    ///     costs the n-th entry of MissionsThreatCycle.CoolDown (5 min, 20 min, 1 h, ...). The client
    ///     offers to cut it short (decreaseCoolDown), which costs crystals - free on this server.
    /// </summary>
    public static class MissionMap
    {
        /// <summary>The first mission map ("Wicked Beasts" on Low) is always open.</summary>
        public const int FirstMission = 5111;

        /// <summary>The tutorial ("Welcome, Red Hunter"): every new character is in it; the client
        /// starts it via TutorialEnterReq without it ever showing on the mission map.</summary>
        public const int TutorialMission = 5011;

        /// <summary>Whether finished maps rest (MissionsThreatCycle). Off: maps are playable again at once.</summary>
        public const bool CooldownsEnabled = false;

        public static int BaseId(int missionId) => missionId - missionId % 10;

        /// <summary>Mission ids (map + threat) the character may play.</summary>
        public static HashSet<int> Unlocked(CharacterData character, GameData data)
        {
            var open = new HashSet<int> { FirstMission, TutorialMission };
            foreach (int done in character.CompletedMissions)
            {
                open.Add(done);
                if (!data.Missions.TryGetValue(done, out MissionDefinition mission))
                    continue;
                foreach (string part in (mission.NewMissionIds ?? "").Split(';', ',', ' '))
                {
                    if (int.TryParse(part, out int id) && id != 0)
                        open.Add(id);
                }
                // Beating a threat opens the next one, even where the data forgets to say so.
                if (done % 10 is >= 1 and < 4)
                    open.Add(done + 1);
            }
            // A map that is open at all is open on Low.
            foreach (int id in open.ToList())
                open.Add(BaseId(id) + 1);
            // Maps the shipped client has no level for never open (see ClientMaps).
            open.RemoveWhere(id => !data.Missions.TryGetValue(id, out MissionDefinition mission)
                                   || !ClientHasMap(mission.MapName));
            return open;
        }

        /// <summary>Whether the shipped client can load this mission map at all.</summary>
        public static bool ClientHasMap(string mapName) => ClientMaps.Contains(mapName);

        /// <summary>
        /// The mission maps the shipped client has a level for: its assetbundles/Scenes folder
        /// holds exactly these 25, one per mission map (plus the two hub scenes). Act 8
        /// ("Knock, Knock", "Fire in the Dark", "What Lies Below") is in the mission tables, and
        /// beating Act 7's "Wicked Beasts" even unlocks it there - but the client ships no level,
        /// no scene XML and no minimap texture for those maps: choosing one hangs the loading
        /// screen and leaves the minimap on the previous map's image. The live game clearly never
        /// opened Act 8; keep these maps dark.
        /// </summary>
        private static readonly HashSet<string> ClientMaps = new(StringComparer.Ordinal)
        {
            "Hub_Village_Tutorial",
            "A1_M1", "A1_M2", "A1_M3", "A1_M4",
            "A2_M1", "A2_M2", "A2_M3", "A2_M4",
            "A3_M1", "A3_M2", "A3_M3", "A3_M4",
            "A4_M1", "A4_M2", "A4_M3", "A4_M4",
            "A5_M1", "A5_M2", "A5_M3",
            "A6_M1", "A6_M2", "A6_M3",
            "A7_M1", "A7_M2", "A7_M3",
        };

        /// <summary>Seconds until the map can be played again (0 = now).</summary>
        public static int CooldownSeconds(CharacterData character, int missionId, long nowUnix) =>
            character.MissionReadyUnix.TryGetValue(BaseId(missionId), out long ready) && ready > nowUnix
                ? (int)Math.Min(int.MaxValue, ready - nowUnix)
                : 0;

        /// <summary>Records a finished run and lets the map rest.</summary>
        public static void Finished(CharacterData character, int missionId, long nowUnix, GameData data)
        {
            int map = BaseId(missionId);
            if (!character.MissionRuns.TryGetValue(map, out List<long> runs))
                character.MissionRuns[map] = runs = new List<long>();
            runs.RemoveAll(t => t < nowUnix - 24 * 3600);
            runs.Add(nowUnix);

            IReadOnlyList<int> table = data.MissionCooldownMinutes;
            if (table == null || table.Count == 0)
                return;
            int minutes = table[Math.Min(runs.Count, table.Count) - 1];
            character.MissionReadyUnix[map] = nowUnix + minutes * 60L;
        }

        /// <summary>decreaseCoolDown: 0 = end it now, 1 = 30 minutes less, 2 = 60 minutes less (MissionPanel.ResetCoolTime).</summary>
        public static void Shorten(CharacterData character, int missionId, int type, long nowUnix)
        {
            int map = BaseId(missionId);
            if (!character.MissionReadyUnix.TryGetValue(map, out long ready))
                return;
            ready = type switch
            {
                1 => ready - 1800,
                2 => ready - 3600,
                _ => nowUnix,
            };
            character.MissionReadyUnix[map] = Math.Max(nowUnix, ready);
        }
    }
}
