using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Runs a level's scripted events the way the level designers set them up:
    ///   - trigger volumes fire when the player walks in (gates open or close, ambushes spawn),
    ///   - spawners set things off once enough of their monsters are dead (NpcSpawner.LinkedEvents):
    ///     the next wave appears, a gate opens, another trigger gets armed.
    /// The client shows all of it, but only when the server says so
    /// (CS_SceneInfo.AddTriggerAnimation / On_EnterMonster).
    /// </summary>
    public sealed class TriggerRunner
    {
        private readonly Session _s;

        public TriggerRunner(Session session) => _s = session;

        /// <summary>Checks every armed trigger against the player's position.</summary>
        public void CheckPosition(Vector3 player)
        {
            SceneInstance scene = _s.Scene;
            if (scene == null)
                return;

            // Like the client's OnTriggerEnter: a trigger goes off when the player walks in, not on
            // every step inside it. Leaving the box lets unlimited triggers (MaxCount 0) fire again.
            foreach (TriggerInfo trigger in scene.Triggers)
            {
                if (!Contains(trigger, player))
                {
                    scene.TriggersEntered.Remove(trigger);
                    LogNearPortal(trigger, player);
                }
                else if (scene.ArmedTriggers.Contains(trigger) && scene.TriggersEntered.Add(trigger))
                    Fire(trigger);
            }
        }

        private readonly Dictionary<int, DateTime> _nearPortalLogged = new Dictionary<int, DateTime>();

        /// <summary>
        /// Standing right at a portal without it going off: logged (every few seconds) with how far the
        /// player is from its box, so a portal that does not work can be told apart from one not reached.
        /// </summary>
        private void LogNearPortal(TriggerInfo trigger, Vector3 player)
        {
            if (trigger.Center == null || trigger.Size == null || !trigger.Results.Any(r => r.Type == TriggerResultType.Teleport))
                return;
            float dx = player.x - trigger.Center[0], dz = player.z - trigger.Center[2];
            if (Math.Sqrt(dx * dx + dz * dz) > Math.Max(trigger.Size[0], trigger.Size[2]) / 2f + 3f)
                return;
            DateTime now = _s.World.Clock.UtcNow;
            if (_nearPortalLogged.TryGetValue(trigger.Id, out DateTime last) && now - last < TimeSpan.FromSeconds(5))
                return;
            _nearPortalLogged[trigger.Id] = now;
            _s.Log.LogInformation("   near portal {Id} without entering: offset ({Dx:0.0}, {Dy:0.0}, {Dz:0.0}), box ({Sx:0.0}, {Sy:0.0}, {Sz:0.0}), armed {Armed}",
                trigger.Id, dx, player.y - trigger.Center[1], dz, trigger.Size[0], trigger.Size[1], trigger.Size[2],
                _s.Scene.ArmedTriggers.Contains(trigger));
        }

        /// <summary>
        /// Counts a dead monster for its spawner and sets off the spawner's linked events whose
        /// kill count is reached.
        /// </summary>
        public void OnMonsterKilled(Monster monster)
        {
            SceneInstance scene = _s.Scene;
            if (scene != null)
                NextWave(scene, monster);
            if (scene == null || !scene.Spawners.TryGetValue(monster.SpawnerId, out GameData.SpawnerInfo spawner))
                return;

            Loop(scene, spawner);

            int kills = scene.KillsPerSpawner[spawner.Id] = scene.KillsPerSpawner.GetValueOrDefault(spawner.Id) + 1;
            // A few links ask for more kills than the spawner has monsters (spawn waves the server
            // does not repeat); those happen once the spawner is wiped out.
            int size = scene.MonstersPerSpawner.GetValueOrDefault(spawner.Id);

            foreach (GameData.SpawnerLink link in spawner.Links)
            {
                int needed = Math.Clamp(link.KillCount, 1, Math.Max(1, size));
                if (kills < needed || !scene.FiredLinks.Add(link))
                    continue;

                if (link.Delay > 0)
                    scene.Schedule(_s.World.Clock.UtcNow.AddSeconds(link.Delay), () => RunLink(scene, link));
                else
                    RunLink(scene, link);
            }
        }

        /// <summary>
        /// A finished mission task sets off the triggers it names (MissionTask.triggerId, e.g.
        /// "785;786;103"): the boss appears once the brutes are dead, the closing speech plays.
        /// </summary>
        public void OnTaskCompleted(string triggerIds) => FireByIds(triggerIds);

        /// <summary>The client asks for a trigger itself when the player pulls a lever (Lever.TriggerAllEvent).</summary>
        public void OnLeverPulled(int triggerId) => FireByIds(triggerId.ToString());

        private void FireByIds(string triggerIds)
        {
            SceneInstance scene = _s.Scene;
            if (scene == null || string.IsNullOrWhiteSpace(triggerIds))
                return;

            foreach (string part in triggerIds.Split(';', ',', ' '))
            {
                if (!int.TryParse(part, out int id) || id == 0)
                    continue;
                foreach (TriggerInfo trigger in scene.TriggersById(id).ToList())
                    Fire(trigger);
            }
        }

        private void RunLink(SceneInstance scene, GameData.SpawnerLink link)
        {
            if (scene != _s.Scene)
                return; // the player left the scene in the meantime

            switch (link.Kind)
            {
                case GameData.SpawnerLink.EnableSpawner:
                    Spawn(link.TargetId);
                    break;
                case GameData.SpawnerLink.DisableSpawner:
                    Disable(link.TargetId);
                    break;
                case GameData.SpawnerLink.AutoTrigger:
                    foreach (TriggerInfo trigger in scene.TriggersById(link.TargetId).ToList())
                        Fire(trigger);
                    break;
                case GameData.SpawnerLink.DisableTrigger:
                    foreach (TriggerInfo trigger in scene.TriggersById(link.TargetId))
                        scene.ArmedTriggers.Remove(trigger);
                    break;
                case GameData.SpawnerLink.ToggleTrigger:
                    foreach (TriggerInfo trigger in scene.TriggersById(link.TargetId))
                    {
                        if (!scene.ArmedTriggers.Remove(trigger))
                            scene.ArmedTriggers.Add(trigger);
                    }
                    break;
            }
        }

        /// <summary>Plays a trigger's results, as often as the level allows (MaxCount; 0 = unlimited).</summary>
        private void Fire(TriggerInfo trigger)
        {
            SceneInstance scene = _s.Scene;
            int fired = scene.TriggerFireCount.GetValueOrDefault(trigger);
            if (trigger.MaxCount > 0 && fired >= trigger.MaxCount)
                return;
            scene.TriggerFireCount[trigger] = fired + 1;

            _s.Log.LogInformation("   trigger {Id} '{Name}' fired ({Results} results)",
                trigger.Id, trigger.Name, trigger.Results.Count);

            foreach (TriggerResult result in trigger.Results)
            {
                switch (result.Type)
                {
                    case TriggerResultType.Animation:
                        // The client finds the object by the id of its SelfAnimation component
                        // (the "realID" of the level data), not by the trigger's combined id.
                        // Without an animation name this simply shows or hides it, which is how
                        // gates and barriers open and close.
                        _s.Send(BattleResponses.TriggerAnimationResult(result.RealId, result.AnimName ?? "",
                            result.Loop, result.Delay + trigger.DelayTime, result.Collision, result.Display));
                        break;

                    case TriggerResultType.Teleport when result.TeleportPos is { Length: 3 } target:
                        // A portal: the client fades out, moves the player and reports the new place.
                        _s.Position = new Vector3(target[0], target[1], target[2]);
                        scene.Nav.Jumped(_s.Position);
                        _s.Send(BattleResponses.TelePortMoveResult(trigger.Id, _s.Position, 0f, 0,
                            result.FadeIn, result.FadeOut, result.CameraFollow));
                        break;

                    case TriggerResultType.Speech:
                        Say(trigger, result);
                        break;

                    case TriggerResultType.Spawn when result.DisableSpawner:
                        Disable(result.SpawnerId);
                        break;

                    case TriggerResultType.Spawn:
                        Spawn(result.SpawnerId);
                        break;
                }
            }

            foreach (int id in trigger.EnableTriggers ?? new List<int>())
                scene.ArmedTriggers.UnionWith(scene.TriggersById(id));
        }

        /// <summary>
        /// A speech bubble: the client shows it over the next NPC of that type it has (0 = the player),
        /// with the lines from its word list; "inside" bubbles vanish once the player leaves the box.
        /// </summary>
        private void Say(TriggerInfo trigger, TriggerResult result)
        {
            var words = new vectorInt();
            foreach (int word in result.Words ?? new List<int>())
                words.Add(word);
            var min = Proto.New<SServerPosition>();
            var max = Proto.New<SServerPosition>();
            if (trigger.Center != null && trigger.Size != null)
            {
                // The client checks an upright rectangle; a turned box gets the one around it.
                float half = Math.Max(trigger.Size[0], trigger.Size[2]) / 2f;
                if (Math.Abs(trigger.RotationY) < 0.01f)
                {
                    min.fx = trigger.Center[0] - trigger.Size[0] / 2f;
                    min.fz = trigger.Center[2] - trigger.Size[2] / 2f;
                    max.fx = trigger.Center[0] + trigger.Size[0] / 2f;
                    max.fz = trigger.Center[2] + trigger.Size[2] / 2f;
                }
                else
                {
                    half *= 1.4143f;
                    min.fx = trigger.Center[0] - half;
                    min.fz = trigger.Center[2] - half;
                    max.fx = trigger.Center[0] + half;
                    max.fz = trigger.Center[2] + half;
                }
            }
            _s.Send(BattleResponses.SpeechResult(result.NpcId, result.Loop, result.Random, result.Delay, words,
                result.Inside, min, max));
        }

        /// <summary>A broken basket or chest brings what it hides (InteractiveHandler.LinkedEvents).</summary>
        public void OnObjectBroken(Breakable breakable)
        {
            SceneInstance scene = _s.Scene;
            if (scene == null)
                return;
            foreach (TriggerResult anim in breakable.Info.Animations)
            {
                _s.Log.LogInformation("   {Object} plays {Anim} on object {RealId}", breakable.Info.Name, anim.AnimName, anim.RealId);
                _s.Send(BattleResponses.TriggerAnimationResult(anim.RealId, anim.AnimName ?? "", anim.Loop, anim.Delay, anim.Collision, anim.Display));
            }
            foreach (GameData.SpawnerLink link in breakable.Info.Links)
            {
                if (link.Delay > 0)
                    scene.Schedule(_s.World.Clock.UtcNow.AddSeconds(link.Delay), () => RunLink(scene, link));
                else
                    RunLink(scene, link);
            }
        }

        /// <summary>
        /// Wakes the monsters of a spawner and tells the client about them. A pack comes one after
        /// the other when its group says so (SpawnDelay, SpawnDelayPerNpc).
        /// </summary>
        private void Spawn(int spawnerId)
        {
            SceneInstance scene = _s.Scene;
            if (scene.DisabledSpawners.Contains(spawnerId))
                return;

            // Later waves of repeating groups wait for the wave before them (OnMonsterKilled).
            List<Monster> waiting = scene.MonstersOf(spawnerId).Where(m => m.Dormant && m.Wave == 0).ToList();
            if (waiting.Count == 0)
                return;

            _s.Log.LogInformation("   spawner {Spawner} brings {Count} monsters", spawnerId, waiting.Count);
            Wake(scene, spawnerId, waiting);
        }

        /// <summary>Lets waiting monsters appear, each after its own delay (SpawnDelay, SpawnDelayPerNpc).</summary>
        private void Wake(SceneInstance scene, int spawnerId, List<Monster> waiting)
        {
            foreach (Monster monster in waiting.Where(m => m.AppearDelay > 0))
            {
                scene.Schedule(_s.World.Clock.UtcNow.AddSeconds(monster.AppearDelay), () =>
                {
                    if (scene != _s.Scene || !monster.Dormant || scene.DisabledSpawners.Contains(spawnerId))
                        return;
                    monster.Dormant = false;
                    _s.Battle.AnnounceMonsters();
                });
            }

            foreach (Monster monster in waiting.Where(m => m.AppearDelay <= 0))
                monster.Dormant = false;
            _s.Battle.AnnounceMonsters();
        }

        /// <summary>
        /// A repeating group (SpawnRepeatCount) sends its next wave once every monster of the
        /// current one is dead; a switched-off spawner sends none.
        /// </summary>
        private void NextWave(SceneInstance scene, Monster killed)
        {
            if (killed.WaveGroup == null || scene.DisabledSpawners.Contains(killed.SpawnerId))
                return;
            List<Monster> group = scene.MonstersOf(killed.SpawnerId).Where(m => m.WaveGroup == killed.WaveGroup).ToList();
            if (group.Any(m => m.Wave == killed.Wave && m.IsAlive && m.ObjectId != killed.ObjectId))
                return;
            List<Monster> next = group.Where(m => m.Wave == killed.Wave + 1 && m.Dormant).ToList();
            if (next.Count == 0)
                return;
            _s.Log.LogInformation("   spawner {Spawner} sends wave {Wave} ({Count} monsters)", killed.SpawnerId, killed.Wave + 2, next.Count);
            Wake(scene, killed.SpawnerId, next);
        }

        /// <summary>
        /// A looping spawner (LinkedEvents.LoopingSpawner) sends its monsters again once they are all
        /// dead, LoopCount times. All looping spawners of the levels (28 in A2_M1, 1 in A7_M3) have
        /// LoopCount 0, which is read as "no repeat" - endless monsters in a whole level are unlikely.
        /// </summary>
        private void Loop(SceneInstance scene, GameData.SpawnerInfo spawner)
        {
            if (!spawner.Looping || scene.DisabledSpawners.Contains(spawner.Id)
                || scene.MonstersOf(spawner.Id).Any(m => m.IsAlive))
                return;
            int loops = scene.SpawnerLoops.GetValueOrDefault(spawner.Id);
            if (loops >= spawner.LoopCount)
                return;
            scene.SpawnerLoops[spawner.Id] = loops + 1;

            scene.Schedule(_s.World.Clock.UtcNow.AddSeconds(LoopDelaySeconds), () =>
            {
                if (scene != _s.Scene || scene.DisabledSpawners.Contains(spawner.Id))
                    return;
                var (monsters, _) = scene.AddMonsters(spawner, true, _s.Data, _s.World.Random);
                _s.Log.LogInformation("   spawner {Spawner} loops ({Count} monsters)", spawner.Id, monsters.Count);
                Wake(scene, spawner.Id, monsters.Where(m => m.Wave == 0).ToList());
            });
        }

        /// <summary>Pause before a looping spawner sends its monsters again (assumed, not in the level data).</summary>
        public const double LoopDelaySeconds = 10;

        /// <summary>Switches a spawner off: monsters still waiting in it never come.</summary>
        private void Disable(int spawnerId)
        {
            SceneInstance scene = _s.Scene;
            if (!scene.DisabledSpawners.Add(spawnerId))
                return;
            foreach (Monster monster in scene.MonstersOf(spawnerId).Where(m => m.Dormant).ToList())
                scene.Remove(monster.ObjectId);
            _s.Log.LogInformation("   spawner {Spawner} switched off", spawnerId);
        }

        /// <summary>Is the player inside the trigger's box? The box may be turned around the Y axis.</summary>
        private static bool Contains(TriggerInfo trigger, Vector3 player)
        {
            if (trigger.Center == null || trigger.Size == null)
                return false;

            float dx = player.x - trigger.Center[0];
            float dy = player.y - trigger.Center[1];
            float dz = player.z - trigger.Center[2];

            if (Math.Abs(trigger.RotationY) > 0.01f)
            {
                // Into the box's own axes: Unity turns local (x, z) by yaw into
                // (x cos + z sin, -x sin + z cos), so the way back is the transposed rotation.
                double radians = trigger.RotationY * Math.PI / 180.0;
                double cos = Math.Cos(radians), sin = Math.Sin(radians);
                (dx, dz) = ((float)(dx * cos - dz * sin), (float)(dx * sin + dz * cos));
            }

            // The client fires on its character controller touching the box (OnTriggerEnter), not on
            // the reported foot position: the body reaches about half a meter to the side and two
            // meters up, so a box floating above the path still counts.
            return Math.Abs(dx) <= trigger.Size[0] / 2f + BodyRadius
                   && Math.Abs(dz) <= trigger.Size[2] / 2f + BodyRadius
                   && dy <= trigger.Size[1] / 2f
                   && dy >= -trigger.Size[1] / 2f - BodyHeight;
        }

        /// <summary>The player's character controller: radius and height (meters).</summary>
        private const float BodyRadius = 0.5f, BodyHeight = 2f;
    }
}
