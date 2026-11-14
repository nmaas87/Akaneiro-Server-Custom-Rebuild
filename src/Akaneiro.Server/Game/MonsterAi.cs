using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Server side monster behaviour: notice the player, chase, attack.
    ///
    /// The client only plays what it is told (see NpcBase / CS_SceneInfo):
    ///   UpdateAnimationNotify(id, target, state) puts the monster into a state
    ///       (1 = chase, 3 = attack, 0 = idle; NpcBase.EAnimationStateType)
    ///   SetObjectVecPosition(elapsed, id, path) moves it along a path of x/z points
    ///   UseNormalAttackResult(.., source = monster) applies its damage to the player
    /// </summary>
    public sealed class MonsterAi
    {
        private const int AniIdle = 0;
        private const int AniChase = 1;
        private const int AniWander = 2;
        private const int AniAttack = 3;
        private const int AniSleep = 5;
        private const int AniSpawn = 6;
        private const int AniWakeUp = 8;

        /// <summary>
        /// How long entrance animations keep a monster busy: the clip length + NpcBase.AdditinalResetTime
        /// of its prefab, like the original level export (SpawnAniTime, WakeUpTime); these are only
        /// for monsters without the clips.
        /// </summary>
        private static readonly TimeSpan SpawnTime = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan WakeUpTime = TimeSpan.FromSeconds(1.0);

        private static TimeSpan SpawnDuration(Monster monster) =>
            monster.Type.SpawnSeconds is { Count: > 0 } times && monster.SpawnAnimIndex >= 0 && monster.SpawnAnimIndex < times.Count && times[monster.SpawnAnimIndex] > 0
                ? TimeSpan.FromSeconds(times[monster.SpawnAnimIndex])
                : SpawnTime;

        private static TimeSpan WakeUpDuration(Monster monster) =>
            monster.Type.WakeUpSeconds > 0 ? TimeSpan.FromSeconds(monster.Type.WakeUpSeconds) : WakeUpTime;

        /// <summary>A monster's melee attack is ability 10001 for the client too (NpcBase: AS = GetAbilityByID(10001)).</summary>
        private const int MonsterMeleeAbility = 10001;

        /// <summary>How often the behaviour is evaluated.</summary>
        public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// Delay between the start of an attack animation and its damage, so the hit lands when the
        /// monster actually strikes instead of the moment it starts moving.
        /// </summary>
        private static readonly TimeSpan HitDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>A monster gives up when the player gets this much further away than its vision.</summary>
        private const float GiveUpFactor = 2.5f;

        private sealed class State
        {
            /// <summary>Whether this monster's current target is the hired companion (else the player).</summary>
            public bool OnAlly;

            /// <summary>Not before this time the target may switch again (anti flicker).</summary>
            public DateTime SwitchTargetAt;
            public bool Chasing;
            public DateTime NextAttack;

            /// <summary>A swing that started and whose damage lands when the animation hits.</summary>
            public DateTime HitDue;
            public GameData.MonsterAttack PendingAttack;

            /// <summary>Sleeping until it notices the player (entrance Sleep).</summary>
            public bool Asleep;

            /// <summary>An ambush: it knows where the player is and does not give up.</summary>
            public bool Ambush;

            /// <summary>Playing an entrance or wake-up animation until then.</summary>
            public DateTime BusyUntil;

            /// <summary>Chasing: the corner points still ahead (NavGrid), when and towards where they were found.</summary>
            public List<Vector3> Path;
            public DateTime PathAt;
            public Vector3 PathGoal;

            /// <summary>Hit by the player: it comes for them even from afar, and does not give up until then.</summary>
            public DateTime ProvokedUntil;

            /// <summary>Since when it stands within reach (null = not in reach); the client needs a moment to catch up.</summary>
            public DateTime? InReachSince;

            /// <summary>
            /// It swung since it last got the chase state. The client ignores chase paths of a
            /// monster in its attack state (NpcBase.SetNextMoveTarget), so it has to be told to
            /// chase again before it can follow the player.
            /// </summary>
            public bool NeedsChaseState;

            /// <summary>Wandering: the waypoint it walks to, and the pause before the next leg.</summary>
            public int PatrolIndex = -1;
            public DateTime PatrolRestUntil;
        }

        private readonly Session _s;
        private readonly Dictionary<int, State> _states = new Dictionary<int, State>();
        private DateTime _lastTick;

        public MonsterAi(Session session)
        {
            _s = session;
            _lastTick = session.World.Clock.UtcNow;
        }

        private Random Rng => _s.World.Random;

        /// <summary>Time a monster waits after reaching the player before it swings (client catch-up).</summary>
        private static readonly TimeSpan CatchUpTime = TimeSpan.FromSeconds(0.3);

        /// <summary>How far outside its reach a swing still lands (the player's own reach of a step).</summary>
        private const float DodgeMargin = 1.5f;

        /// <summary>How long a hit monster keeps after the player, however far they run.</summary>
        private static readonly TimeSpan ProvokeTime = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The player hit it: a sleeping or unaware monster wakes and comes for them, even when the
        /// hit came from beyond its sight (ranged abilities), instead of standing there.
        /// </summary>
        public void Provoke(Monster monster)
        {
            if (!monster.IsAlive)
                return;
            State state = _states.TryGetValue(monster.ObjectId, out State s) ? s : _states[monster.ObjectId] = new State();
            state.ProvokedUntil = _s.World.Clock.UtcNow + ProvokeTime;
            if (state.Chasing)
                return;
            state.Asleep = false;
            state.Chasing = true;
            _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, Session.PlayerObjectId, AniChase));
        }

        /// <summary>
        /// Puts a monster the client was just told about into its entrance state, as its spawner
        /// says: asleep, or an ambush that plays its entrance animation (jumping over a wall, out
        /// of the ground, ...). Without it the client keeps an ambusher hidden where it spawned.
        /// </summary>
        public void Enter(Monster monster)
        {
            var state = new State();
            _states[monster.ObjectId] = state;

            switch (monster.Entrance)
            {
                case MonsterEntrance.Sleep:
                    state.Asleep = true;
                    _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, 0, AniSleep));
                    break;

                case MonsterEntrance.Spawn:
                    state.Ambush = true;
                    state.BusyUntil = _s.World.Clock.UtcNow + SpawnDuration(monster);
                    _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, monster.SpawnAnimIndex, AniSpawn));
                    // The entrance jump (over a wall, down into the arena) lands on the group's
                    // waypoint. During its spawn state the client ignores paths (SetObjectVecPosition)
                    // and only takes a single position (CS_SceneInfo.On_SetObjectPosition ->
                    // NPC_SpawnState.LastMovePosition), which it jumps to during the animation.
                    if (monster.Waypoint is Vector3 landing)
                    {
                        _s.Send(BattleResponses.SetObjectPosition(monster.ObjectId, landing));
                        monster.Position = landing;
                    }
                    break;
            }
        }

        public void Tick()
        {
            if (_s.Character == null || _s.Scene == null)
                return;

            // Level events with a delay (a gate opening a second after the last wolf fell).
            _s.Scene.RunDueEvents(_s.World.Clock.UtcNow);

            // Nothing to do while the player is dead and looking at the revive screen, but ability
            // objects still have to disappear.
            if (_s.IsDead)
            {
                RemoveExpiredObjects(_s.World.Clock.UtcNow);
                return;
            }

            var now = _s.World.Clock.UtcNow;
            // After a pause (revive screen, loading) monsters carry on from where they were instead
            // of covering the whole missed time in one step.
            float dt = (float)Math.Min((now - _lastTick).TotalSeconds, 2 * TickInterval.TotalSeconds);
            _lastTick = now;

            RemoveExpiredObjects(now);

            // The hired companion: healed out of combat, checked against its 30 minutes.
            FriendAlly ally = _s.FriendAlly != null ? TendAlly(_s.FriendAlly, now, dt) : null;

            Vector3 player = _s.Position;
            foreach (Monster monster in _s.Scene.Monsters.ToList())
            {
                // Only monsters the client can see may act: waiting ones are not in the world yet.
                if (!monster.IsActive || !_s.Scene.IsAnnounced(monster.ObjectId))
                    continue;

                State state = _states.TryGetValue(monster.ObjectId, out State s) ? s : _states[monster.ObjectId] = new State();

                // A monster takes whichever of the two stands closer (the companion needs a clear
                // lead to take it over, so the pair does not flicker between them).
                if (state.OnAlly && (ally == null || !ally.Present))
                    state.OnAlly = false;
                float playerDistance = SceneInstance.Distance(monster.Position, player);
                float allyDistance = ally != null && ally.Present
                    ? SceneInstance.Distance(monster.Position, ally.Position) : float.MaxValue;
                bool switchNow = state.OnAlly
                    ? allyDistance > playerDistance + 3f
                    : allyDistance < playerDistance - 2f;
                if (switchNow && now >= state.SwitchTargetAt)
                {
                    state.OnAlly = !state.OnAlly;
                    state.SwitchTargetAt = now + TimeSpan.FromSeconds(2);
                    state.Path = null;
                }
                Vector3 targetPos = state.OnAlly ? ally.Position : player;
                int targetId = state.OnAlly ? ally.ObjectId : Session.PlayerObjectId;

                // A swing that has started lands when its animation reaches the hit.
                if (state.PendingAttack != null && now >= state.HitDue)
                {
                    if (monster.Type.ThrowAbility != 0)
                        Throw(monster, state.PendingAttack, state.OnAlly ? ally : null);
                    else
                        LandHit(monster, state.PendingAttack, state.OnAlly ? ally : null);
                    state.PendingAttack = null;
                    if (_s.IsDead)
                        return;
                }
                if (now < state.BusyUntil)
                    continue; // still in its entrance or wake-up animation
                if (now < monster.StunnedUntil)
                {
                    state.PendingAttack = null; // a stun breaks off the swing
                    continue;
                }

                float distance = state.OnAlly ? allyDistance : playerDistance;
                float vision = monster.Type.VisionRadius > 0 ? monster.Type.VisionRadius : 6f;
                float reach = monster.Type.AttackRange > 0 ? monster.Type.AttackRange : 2f;

                if (!state.Chasing)
                {
                    if (distance > vision && !state.Ambush)
                    {
                        Wander(monster, state, now, dt, playerDistance);
                        continue;
                    }
                    if (state.Asleep)
                    {
                        state.Asleep = false;
                        state.BusyUntil = now + WakeUpDuration(monster);
                        _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, targetId, AniWakeUp));
                        _s.Log.LogInformation("   {Monster} #{Id} wakes up", monster.Type.Name, monster.ObjectId);
                        continue;
                    }
                    state.Chasing = true;
                    _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, targetId, AniChase));
                    _s.Log.LogInformation("   {Monster} #{Id} noticed the {Target}", monster.Type.Name, monster.ObjectId,
                        state.OnAlly ? "companion" : "player");
                }

                if (distance > vision * GiveUpFactor && !state.Ambush && now >= state.ProvokedUntil)
                {
                    state.Chasing = false;
                    _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, targetId, AniIdle));
                    continue;
                }

                if (distance > reach)
                {
                    state.InReachSince = null;
                    MoveTowards(monster, state, targetPos, targetId, dt);
                    continue;
                }
                // Just arrived: the client shows it a step behind, so it swings once it is there too.
                state.InReachSince ??= now;
                if (now - state.InReachSince.Value < CatchUpTime)
                    continue;

                if (now >= state.NextAttack && state.PendingAttack == null)
                {
                    GameData.MonsterAttack attack = PickAttack(monster);
                    state.NextAttack = now + AttackInterval(attack) * (now < monster.SlowedUntil ? 1.5 : 1.0);
                    state.PendingAttack = attack;
                    state.HitDue = now + (monster.Type.ThrowAbility != 0 ? ThrowRelease(monster) : HitDelay);
                    if (monster.Type.ThrowAbility != 0 && monster.Type.ThrowCastLength > 0)
                        state.NextAttack = Max(state.NextAttack, now + TimeSpan.FromSeconds(monster.Type.ThrowCastLength));
                    StartSwing(monster, attack, state.OnAlly ? ally : null);
                    state.NeedsChaseState = true;
                }
            }
        }

        /// <summary>
        /// Keeps the companion alive while its hire lasts: energy always, health after a moment
        /// without hits, and both reported to the client (its own copy only learns the truth
        /// through AllyRegenerate). When the 30 minutes run out, health 0 is the client's only
        /// path to take an ally out of the world.
        /// </summary>
        private FriendAlly TendAlly(FriendAlly ally, DateTime now, float dt)
        {
            if (now >= ally.HiredUntil)
            {
                if (ally.ObjectId != 0)
                    _s.Send(BattleResponses.AllyRegenerate(ally.ObjectId, 0, 0));
                _s.FriendAlly = null;
                _s.Log.LogInformation("   friend {Name} goes home after the hired time", ally.Character.Name);
                return null;
            }

            if (ally.Present && ally.ObjectId != 0)
            {
                ally.Mp = Math.Min(ally.MaxMp, ally.Mp + (int)Math.Round(dt * Views.RegenTicksPerSecond * ally.Stats.MpRegen));
                if (ally.Hp < ally.MaxHp && now - ally.LastHitAt >= RegenPause)
                    ally.Hp = Math.Min(ally.MaxHp, ally.Hp + (int)Math.Round(dt * Views.RegenTicksPerSecond * ally.Stats.HpRegen));

                if (now >= ally.NextRegenerateAt && (ally.Hp != ally.SyncHp || ally.Mp != ally.SyncMp))
                    SendAllyHealth(ally, now);
            }
            return ally;
        }

        /// <summary>The companion's health pauses a moment after every hit (assumed; the tables give none).</summary>
        private static readonly TimeSpan RegenPause = TimeSpan.FromSeconds(3);

        private void SendAllyHealth(FriendAlly ally, DateTime now)
        {
            _s.Send(BattleResponses.AllyRegenerate(ally.ObjectId, ally.Hp, ally.Mp));
            ally.SyncHp = ally.Hp;
            ally.SyncMp = ally.Mp;
            ally.NextRegenerateAt = now + TimeSpan.FromSeconds(2);
        }

        /// <summary>The companion fell: health 0 makes the client play its death and forget it.</summary>
        private void AllyDown(FriendAlly ally)
        {
            _s.Send(BattleResponses.AllyRegenerate(ally.ObjectId, 0, 0));
            _s.FriendAlly = null;
            _s.Log.LogInformation("   the friend {Name} fell", ally.Character.Name);
        }

        /// <summary>Takes ability objects (meteors, caltrops, ...) out of the world once they are done.</summary>
        private void RemoveExpiredObjects(DateTime now)
        {
            List<int> expired = _s.Scene.TemporaryObjects
                .Where(o => now >= o.Value)
                .Select(o => o.Key)
                .ToList();
            if (expired.Count == 0)
                return;

            var leaving = new vectorInt();
            foreach (int objectId in expired)
            {
                _s.Scene.TemporaryObjects.Remove(objectId);
                leaving.Add(objectId);
            }
            _s.Send(BattleResponses.ObjectLeave(leaving));
        }

        /// <summary>Monsters further away than this do not wander (nobody would see it).</summary>
        private const float WanderRange = 40f;

        /// <summary>Pause at each waypoint (assumed).</summary>
        private static readonly TimeSpan WanderRest = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Entrance "Wander": until it notices the player, the monster walks its group's waypoints
        /// one after the other at its wander speed, resting a moment at each.
        /// </summary>
        private void Wander(Monster monster, State state, DateTime now, float dt, float distanceToPlayer)
        {
            if (monster.Entrance != MonsterEntrance.Wander || monster.Patrol.Count == 0 || state.Asleep
                || distanceToPlayer > WanderRange || now < state.PatrolRestUntil)
                return;

            if (state.PatrolIndex < 0)
            {
                state.PatrolIndex = 0;
                StartLeg(monster, monster.Patrol[0]);
            }
            Vector3 target = monster.Patrol[state.PatrolIndex];
            float speed = Math.Max(1f, monster.Type.WanderSpeed);
            float left = SceneInstance.Distance(monster.Position, target);
            if (left <= speed * dt)
            {
                monster.Position = new Vector3(target.x, monster.Position.y, target.z);
                state.PatrolRestUntil = now + WanderRest;
                state.PatrolIndex = (state.PatrolIndex + 1) % monster.Patrol.Count;
                _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, 0, AniIdle));
                SendPathLater(monster, state);
                return;
            }
            monster.Position = new Vector3(
                monster.Position.x + (target.x - monster.Position.x) / left * speed * dt,
                monster.Position.y,
                monster.Position.z + (target.z - monster.Position.z) / left * speed * dt);
        }

        /// <summary>After the rest the next leg starts; the client walks it from the path.</summary>
        private void SendPathLater(Monster monster, State state)
        {
            SceneInstance scene = _s.Scene;
            scene.Schedule(state.PatrolRestUntil, () =>
            {
                if (scene == _s.Scene && monster.IsActive && !state.Chasing)
                    StartLeg(monster, monster.Patrol[state.PatrolIndex]);
            });
        }

        /// <summary>
        /// A leg of the wander route: the client only walks paths of a wandering monster once it is
        /// in its wander state (NpcBase.SetNextMoveTarget: AniWander), so that goes first.
        /// </summary>
        private void StartLeg(Monster monster, Vector3 target)
        {
            _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, 0, AniWander));
            SendPath(monster, target);
        }

        private void SendPath(Monster monster, Vector3 target)
        {
            var path = new vectorServerPosition
            {
                new SServerPosition { fx = monster.Position.x, fz = monster.Position.z },
                new SServerPosition { fx = target.x, fz = target.z },
            };
            float elapsed = (float)(_s.World.Clock.UtcNow - monster.SpawnedAt).TotalSeconds;
            _s.Send(BattleResponses.SetObjectVecPosition(elapsed, monster.ObjectId, path));
        }

        /// <summary>How often a chasing monster looks for a new way to the player.</summary>
        private static readonly TimeSpan RepathInterval = TimeSpan.FromSeconds(0.5);

        /// <summary>
        /// Chases the player along the corner points of <see cref="NavGrid.FindPath"/>: the player's
        /// trail around obstacles where the straight way is not known to be walkable. The client
        /// gets the points whenever the way is found anew and walks them itself.
        /// </summary>
        private void MoveTowards(Monster monster, State state, Vector3 goal, int targetObjectID, float dt)
        {
            DateTime now = _s.World.Clock.UtcNow;
            float speed = monster.Type.AttackSpeed > 0 ? monster.Type.AttackSpeed : Math.Max(1f, monster.Type.WanderSpeed);
            if (now < monster.SlowedUntil)
                speed *= 0.5f;

            if (state.NeedsChaseState)
            {
                state.NeedsChaseState = false;
                state.Path = null; // the client needs the way again, in the chase state
                _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, targetObjectID, AniChase));
            }

            if (state.Path == null || state.Path.Count == 0 || now - state.PathAt >= RepathInterval
                || SceneInstance.Distance(state.PathGoal, goal) > 2f)
            {
                state.Path = _s.Scene.Nav.FindPath(monster.Position, goal);
                state.PathAt = now;
                state.PathGoal = goal;

                var path = new vectorServerPosition { new SServerPosition { fx = monster.Position.x, fz = monster.Position.z } };
                foreach (Vector3 point in state.Path)
                    path.Add(new SServerPosition { fx = point.x, fz = point.z });
                float elapsed = (float)(now - monster.SpawnedAt).TotalSeconds;
                _s.Send(BattleResponses.SetObjectVecPosition(elapsed, monster.ObjectId, path));
            }

            // Walk along the corners, as far as this tick goes.
            float budget = speed * dt;
            while (budget > 0 && state.Path.Count > 0)
            {
                Vector3 corner = state.Path[0];
                float distance = SceneInstance.Distance(monster.Position, corner);
                if (distance <= budget)
                {
                    monster.Position = new Vector3(corner.x, monster.Position.y, corner.z);
                    budget -= distance;
                    if (state.Path.Count > 1)
                        state.Path.RemoveAt(0);
                    else
                        break;
                    continue;
                }
                monster.Position = new Vector3(
                    monster.Position.x + (corner.x - monster.Position.x) / distance * budget,
                    monster.Position.y,
                    monster.Position.z + (corner.z - monster.Position.z) / distance * budget);
                budget = 0;
            }
        }

        /// <summary>Starts the attack animation. The damage follows when the animation hits.</summary>
        private void StartSwing(Monster monster, GameData.MonsterAttack attack, FriendAlly allyTarget)
        {
            // The animation index picks which of the monster's attack animations plays.
            _s.Send(BattleResponses.UpdateAnimationNotify(monster.ObjectId, attack.AnimIndex, AniAttack));

            // BeginUseSkill is what puts the monster into its attack state and plays the animation
            // (NPCAbilityBaseState.UseAbilityOK); the result afterwards carries the damage.
            var begin = Proto.New<SUseSkillResult>();
            // Throwers start their toss ability (charge in the hand, face the player) instead.
            begin.skillID = monster.Type.ThrowAbility != 0 ? monster.Type.ThrowAbility : MonsterMeleeAbility;
            begin.sourceObjectID = monster.ObjectId;
            begin.destObejctID = allyTarget != null ? allyTarget.ObjectId : Session.PlayerObjectId;
            begin.pos = allyTarget != null ? allyTarget.Position : _s.Position;
            _s.Send(BattleResponses.BeginUseSkill(begin));
        }

        /// <summary>Speed of a thrown ball when the projectile prefab gives none (OniThrowObj default), meters per second.</summary>
        public const float ProjectileSpeed = 20f;

        /// <summary>
        /// Share of the cast animation after which the ball leaves the hand: the Lobber's Oni_Range_Throw
        /// marks it at 1.03 of 1.67 s; the werewolf throwers' clips carry no mark and get the same share.
        /// </summary>
        private const double ReleaseShare = 0.62;

        private static TimeSpan ThrowRelease(Monster monster) =>
            monster.Type.ThrowRelease > 0 ? TimeSpan.FromSeconds(monster.Type.ThrowRelease)
            : monster.Type.ThrowCastLength > 0 ? TimeSpan.FromSeconds(monster.Type.ThrowCastLength * ReleaseShare)
            : HitDelay;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        /// <summary>A ball hits the player within this distance of where they stood when it was thrown.</summary>
        private const float ProjectileHitRadius = 2f;

        /// <summary>
        /// A thrower lets go of its ball: the client flies it from the monster towards the player
        /// (SkillObjectEnter -> NPC_Toss.On_SkillObjectEnter -> OniThrowObj) and is told when it
        /// arrives (BulletHit), with the damage if the player is still there, which also removes it.
        /// </summary>
        private void Throw(Monster monster, GameData.MonsterAttack attack, FriendAlly allyTarget)
        {
            SceneInstance scene = _s.Scene;
            Vector3 from = monster.Position, target = allyTarget != null ? allyTarget.Position : _s.Position;
            float dx = target.x - from.x, dz = target.z - from.z;
            float distance = Math.Max(0.1f, (float)Math.Sqrt(dx * dx + dz * dz));

            var ball = Proto.New<SSkillObjectEnter>();
            ball.objectID = scene.NewObjectId();
            ball.skillID = monster.Type.ThrowAbility;
            ball.pos = from;
            ball.direction = new Vector3(dx / distance, 0f, dz / distance);
            ball.ownerObjectID = monster.ObjectId;
            _s.Send(BattleResponses.SkillObjectEnter(ball));

            scene.Schedule(_s.World.Clock.UtcNow.AddSeconds(distance / (monster.Type.ThrowSpeed > 0 ? monster.Type.ThrowSpeed : ProjectileSpeed)), () =>
            {
                if (scene != _s.Scene)
                    return;
                var effect = Proto.New<SSkillEffect>();

                if (allyTarget != null)
                {
                    effect.targetID = allyTarget.ObjectId;
                    effect.targetPos = allyTarget.Position;
                    int hitDamage = 0;
                    if (allyTarget.Hp > 0 && SceneInstance.Distance(target, allyTarget.Position) <= ProjectileHitRadius)
                    {
                        hitDamage = Math.Max(1, (int)Math.Round(MonsterDamage(monster, attack) *
                            (1f - Combat.DamageReduction(allyTarget.Character, _s.World.Data, allyTarget.Stats))));
                        allyTarget.Hp = Math.Max(0, allyTarget.Hp - hitDamage);
                        allyTarget.LastHitAt = _s.World.Clock.UtcNow;
                        effect.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, -hitDamage));
                    }
                    _s.Send(BattleResponses.BulletHit(ball.objectID, effect));
                    if (hitDamage > 0)
                        SendAllyHealth(allyTarget, _s.World.Clock.UtcNow);
                    _s.Log.LogInformation("   {Monster} #{Id} hits the friend {Name} for {Damage} with a throw ({Hp} left)",
                        monster.Type.Name, monster.ObjectId, allyTarget.Character.Name, hitDamage, allyTarget.Hp);
                    if (allyTarget.Hp <= 0 && hitDamage > 0)
                        AllyDown(allyTarget);
                    return;
                }

                effect.targetID = Session.PlayerObjectId;
                effect.targetPos = _s.Position;
                int damage = 0;
                if (!_s.IsDead && _s.InvulnerableUntil <= _s.World.Clock.UtcNow
                    && SceneInstance.Distance(target, _s.Position) <= ProjectileHitRadius)
                {
                    damage = Math.Max(1, (int)Math.Round(MonsterDamage(monster, attack) * (1f - Combat.DamageReduction(_s.Character, _s.World.Data, _s.Stats))));
                    _s.CurrentHp = Math.Max(0, _s.CurrentHp - damage);
                    effect.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, -damage));
                }
                _s.Send(BattleResponses.BulletHit(ball.objectID, effect));
                _s.Log.LogInformation("   {Monster} #{Id} hits the player for {Damage} with a throw ({Hp} left)",
                    monster.Type.Name, monster.ObjectId, damage, _s.CurrentHp);
                if (_s.CurrentHp <= 0 && damage > 0)
                    KillPlayer();
            });
        }

        /// <summary>Applies the damage of a swing that has reached its hit moment.</summary>
        private void LandHit(Monster monster, GameData.MonsterAttack attack, FriendAlly allyTarget)
        {
            // The target stepped out of reach during the swing: it misses.
            Vector3 targetPos = allyTarget != null ? allyTarget.Position : _s.Position;
            float reach = monster.Type.AttackRange > 0 ? monster.Type.AttackRange : 2f;
            bool inReach = SceneInstance.Distance(monster.Position, targetPos) <= reach + DodgeMargin;
            var result = Proto.New<SUseSkillResult>();
            result.skillID = MonsterMeleeAbility;
            result.sourceObjectID = monster.ObjectId;
            result.destObejctID = allyTarget != null ? allyTarget.ObjectId : Session.PlayerObjectId;
            result.pos = targetPos;

            bool hits = inReach && (attack.HitChance >= 100 || Rng.Next(100) < attack.HitChance);
            if (hits && allyTarget == null && _s.InvulnerableUntil > _s.World.Clock.UtcNow)
                hits = false; // the revival shield: the blow comes through without biting
            // Armor takes its share of every hit (Combat.DamageReduction).
            int damage = hits
                ? Math.Max(1, (int)Math.Round(MonsterDamage(monster, attack) * (1f - Combat.DamageReduction(
                    allyTarget?.Character ?? _s.Character, _s.World.Data, allyTarget?.Stats ?? _s.Stats))))
                : 0;
            if (hits)
            {
                if (allyTarget != null)
                {
                    allyTarget.Hp = Math.Max(0, allyTarget.Hp - damage);
                    allyTarget.LastHitAt = _s.World.Clock.UtcNow;
                }
                else
                    _s.CurrentHp = Math.Max(0, _s.CurrentHp - damage);
                var effect = Proto.New<SSkillEffect>();
                effect.targetID = result.destObejctID;
                effect.targetPos = targetPos;
                effect.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, -damage));
                result.skillEffectVec.Add(effect);
            }

            _s.Send(BattleResponses.UseNormalAttackResult(false, result));

            if (allyTarget != null)
            {
                if (damage > 0)
                    SendAllyHealth(allyTarget, _s.World.Clock.UtcNow);
                _s.Log.LogInformation("   {Monster} #{Id} hits the friend {Name} for {Damage} ({Hp} left)",
                    monster.Type.Name, monster.ObjectId, allyTarget.Character.Name, damage, allyTarget.Hp);
                if (allyTarget.Hp <= 0)
                    AllyDown(allyTarget);
                return;
            }

            _s.Log.LogInformation("   {Monster} #{Id} hits the player for {Damage} ({Hp} left)",
                monster.Type.Name, monster.ObjectId, damage, _s.CurrentHp);

            if (_s.CurrentHp <= 0)
                KillPlayer();
        }

        /// <summary>Picks one of the monster's attack variants according to their chances.</summary>
        private GameData.MonsterAttack PickAttack(Monster monster)
        {
            List<GameData.MonsterAttack> attacks = monster.Type.Attacks;
            if (attacks == null || attacks.Count == 0)
                return new GameData.MonsterAttack { ResetTime = 1.5f, HitChance = 100 };

            int total = attacks.Sum(a => Math.Max(0, a.Chance));
            if (total <= 0)
                return attacks[Rng.Next(attacks.Count)];

            int roll = Rng.Next(total);
            foreach (GameData.MonsterAttack attack in attacks)
            {
                roll -= Math.Max(0, attack.Chance);
                if (roll < 0)
                    return attack;
            }
            return attacks[^1];
        }

        private static TimeSpan AttackInterval(GameData.MonsterAttack attack)
        {
            // ResetTime is the monster's own recovery; it is the cadence between two swings.
            float seconds = attack.ResetTime > 0 ? attack.ResetTime : 1.5f;
            return HitDelay + TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Tells the client the character is dead. Only a full attribute update makes it enter its
        /// death state and open the revive screen (PlayerAttributionManager.UpdateAttrs), because a
        /// single damage effect never checks for death.
        /// </summary>
        private void KillPlayer()
        {
            _s.InvulnerableUntil = DateTime.MinValue;
            var attributes = Views.AttributeMap(_s.Character, _s.World.Data, _s.Stats);
            attributes[EAttributeType.ATTR_CurHP] = 0;
            attributes[EAttributeType.ATTR_CurMP] = _s.CurrentMp;
            _s.Send(GameResponses.ChangeCharacterAttribNTF(Proto.Attrs(attributes)));
            _s.Log.LogInformation("   the player died");
        }

        /// <summary>
        /// Damage of one monster hit. Most monsters carry an AttackDamage in their prefab; the few
        /// that do not (like the tutorial wolf) fall back to a small value scaled by their level.
        /// </summary>
        private static int MonsterDamage(Monster monster, GameData.MonsterAttack attack)
        {
            if (attack.Damage > 0)
                return (int)Math.Round(attack.Damage);
            if (monster.Type.AttackDamage > 0)
                return (int)Math.Round(monster.Type.AttackDamage);
            return Math.Max(1, 5 * Math.Max(1, monster.Type.Level));
        }
    }
}
