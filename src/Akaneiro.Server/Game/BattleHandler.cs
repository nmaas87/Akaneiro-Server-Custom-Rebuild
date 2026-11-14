using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;
using UnityEngine;

namespace Akaneiro.Server.Game
{
    /// <summary>Battle protocol (scenes, movement, combat).</summary>
    public sealed class BattleHandler : BattleRequestHandlerBase
    {
        private readonly Session _s;

        public BattleHandler(Session session) => _s = session;

        protected override void OnUnhandled(string request) => _s.Log.LogWarning("   unhandled battle request {Request}", request);

        // ---- scenes -------------------------------------------------------------------------
        // Flow: client EnterScene(name) -> EnterSceneOk(name) -> client loads the level
        //       -> client MapLoadOK -> CharacterStartInfo (position + attributes) -> player can move.

        public override void EnterScene(string mapName) => EnterSceneByName(mapName);

        /// <summary>
        /// A scene change starts here; the world of the old one ends here too: dropping
        /// <c>_s.Scene</c> stops its monsters (the AI tick and the scheduled swings are gated on
        /// it) so nothing from the previous map fights or kills the player while the client
        /// loads. The new SceneInstance is built on MapLoadOK.
        /// </summary>
        public void EnterSceneByName(string mapName)
        {
            _s.Log.LogInformation("   enter scene {Scene}", mapName);
            _s.FriendRewardAnswered = _s.DayRewardOnScreen = _s.FriendRewardSkippedOnce = false;
            _s.KillChain.End(); // a chain does not carry over into the next map
            _s.InvulnerableUntil = DateTime.MinValue; // the revival shield does not cross maps either
            _s.Scene = null;
            _s.SceneName = mapName;
            _s.Send(BattleResponses.EnterSceneOk(mapName));
        }

        public override void MapLoadOK()
        {
            if (_s.Character == null)
                return;

            if (_s.World.Data.Scenes.TryGetValue(_s.SceneName, out GameData.SceneInfo scene))
            {
                _s.Position = scene.SpawnPosition;
            }
            else
                _s.Log.LogWarning("   no spawn point known for scene '{Scene}'", _s.SceneName);

            // Enter every scene at full health and energy, with no ability on cooldown.
            PlayerStats full = _s.Stats;
            _s.CurrentHp = full.MaxHp;
            _s.CurrentMp = full.MaxMp;
            _s.LastRegen = _s.World.Clock.UtcNow;
            _s.Revivals = 0;
            _s.AbilityCooldowns.Clear();

            var start = Proto.New<SCharacterStartInfo>();
            start.objectID = Session.PlayerObjectId;
            start.pos = _s.Position;
            start.attrVec = Views.Attributes(_s.Character, _s.World.Data, full);
            _s.Send(BattleResponses.CharacterStartInfo(start));

            _s.Scene = SceneInstance.Build(_s.SceneName, MissionThreat(), _s.World.Data, _s.Log, _s.World.Random);
            _s.Scene.Nav.Jumped(_s.Position); // the walkable trail starts at the spawn point
            if (scene != null)
                SendNpcs(scene);
            SendMonstersNearPlayer();
            _s.Triggers.CheckPosition(_s.Position); // the spawn point may lie inside a trigger
            _s.StartMonsterAi();
        }

        /// <summary>
        /// Vendors, trainers, bounty board, well and stash. The client builds them from its own
        /// prefabs (NpcInfo) by type; clicking one opens its screen and sends the request itself.
        /// </summary>
        private void SendNpcs(GameData.SceneInfo scene)
        {
            foreach (GameData.NpcPlacement npc in scene.Npcs)
            {
                var info = Proto.New<SNpcEnter>();
                info.objectID = _s.Scene.NewObjectId();
                info.npcID = npc.NpcId;
                info.pos = new Vector3(npc.Position[0], npc.Position[1], npc.Position[2]);
                _s.Send(BattleResponses.npcEnter(info));
            }
            if (scene.Npcs.Count > 0)
                _s.Log.LogInformation("   {Count} NPCs placed", scene.Npcs.Count);
        }

        /// <summary>
        /// The threat level the player picked for the mission (MissionSelect.ChooseLevelThreat puts
        /// it into the last digit of the mission id, 1..4). It decides which spawners take part.
        /// </summary>
        private int MissionThreat()
        {
            int threat = (_s.Mission?.Mission.Id ?? 0) % 10;
            return threat is >= 1 and <= 4 ? threat : SceneInstance.DefaultThreat;
        }

        /// <summary>
        /// Monsters appear for the client once the player comes within this distance, and keep
        /// existing from then on (the client removes them when they die).
        /// </summary>
        private const float SpawnRadius = 60f;

        /// <summary>Announces monsters the client does not know about yet (also used by triggers).</summary>
        public void AnnounceMonsters() => SendMonstersNearPlayer();

        private void SendMonstersNearPlayer()
        {
            if (_s.Scene == null)
                return;

            int sent = 0;
            foreach (Monster monster in _s.Scene.Near(_s.Position, SpawnRadius))
            {
                if (!_s.Scene.MarkAnnounced(monster.ObjectId))
                    continue;

                var info = Proto.New<SMonsterEnter>();
                info.objectID = monster.ObjectId;
                info.npcID = monster.Type.TypeId;
                info.pos = monster.Position;
                info.hp = monster.Hp;
                info.moveSpeed = 100; // percent, like the client's own default
                info.bRayCast = true;
                monster.SpawnedAt = _s.World.Clock.UtcNow;
                _s.Send(BattleResponses.monsterEnter(info));
                _s.MonsterAi.Enter(monster); // asleep, or an ambush playing its entrance
                sent++;
            }
            if (sent > 0)
                _s.Log.LogInformation("   {Count} monsters announced around the player", sent);

            // Baskets, chests and switches: the client builds them from its prefab by type.
            foreach (Breakable breakable in _s.Scene.BreakablesNear(_s.Position, SpawnRadius))
            {
                if (!_s.Scene.MarkAnnounced(breakable.ObjectId))
                    continue;
                var info = Proto.New<SBreakableActorEnter>();
                info.objectID = breakable.ObjectId;
                info.ActorID = breakable.Info.TypeId;
                info.pos = breakable.Position;
                info.rotation = Vec(breakable.Info.Rotation);
                info.scale = Vec(breakable.Info.Scale);
                _s.Send(BattleResponses.BreakableActorEnter(info));
                if (breakable.Info.Animations.Count > 0 || breakable.Info.Links.Count > 0)
                    _s.Log.LogInformation("   object {Object} #{Id} announced", breakable.Info.Name, breakable.ObjectId);
            }
        }

        private static Vector3 Vec(float[] v) => v != null && v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : new Vector3(1f, 1f, 1f);

        // ---- abilities ----------------------------------------------------------------------

        public override void UseFightSkill(uint skillID, uint targetObjectID, Vector3 location, uint curTime)
        {
            if (_s.Character == null || _s.IsDead)
                return;

            _s.ApplyRegeneration();

            int abilityId = (int)skillID;
            if ((!IsReviveShockwave(abilityId) && !_s.Character.Abilities.Any(a => a.Id == abilityId)) ||
                !_s.World.Data.Abilities.TryGetValue(abilityId, out GameData.AbilityRow ability))
            {
                Fail(targetObjectID, abilityId, EServerErrorType.eGS_SkillNotExist);
                return;
            }

            if (_s.AbilityCooldowns.TryGetValue(abilityId, out DateTime ready) && _s.World.Clock.UtcNow < ready)
            {
                Fail(targetObjectID, abilityId, EServerErrorType.eGS_SkillColding);
                return;
            }

            if (_s.CurrentMp < ability.MpNeed)
            {
                Fail(targetObjectID, abilityId, EServerErrorType.eGS_MpInadequate);
                return;
            }

            _s.CurrentMp -= ability.MpNeed;
            _s.AbilityCooldowns[abilityId] = _s.World.Clock.UtcNow.AddMilliseconds(ability.CooldownMs);
            _s.Log.LogInformation("   ability {Name} on target {Target} (energy {Mp})", ability.Name, targetObjectID, _s.CurrentMp);

            // The client applies the attribute changes of BeginUseSkill to the caster: the energy cost.
            var begin = Proto.New<SUseSkillResult>();
            begin.skillID = abilityId;
            begin.sourceObjectID = Session.PlayerObjectId;
            begin.destObejctID = (int)targetObjectID;
            begin.pos = location;
            begin.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurMP, -ability.MpNeed));
            _s.Send(BattleResponses.BeginUseSkill(begin));

            // Abilities such as Meteor Rain or Caltrops place an object in the world; without it the
            // client has nothing to show (CS_SceneInfo.On_SkillObjectEnter).
            if (ability.ObjectId != 0 && _s.Scene != null)
            {
                var skillObject = Proto.New<SSkillObjectEnter>();
                skillObject.objectID = _s.Scene.NewObjectId();
                skillObject.skillID = abilityId;
                skillObject.objectTypeID = ability.ObjectId;
                skillObject.pos = location;
                skillObject.direction = new Vector3(0f, 0f, 1f);
                skillObject.ownerObjectID = Session.PlayerObjectId;
                _s.Send(BattleResponses.SkillObjectEnter(skillObject));
                _s.Send(BattleResponses.SkillObjectActive(skillObject.objectID));
                // Without an end the object would stay in the world forever, so it is removed again
                // after its animation (Ability table AnimDuration, in milliseconds).
                _s.Scene.TemporaryObjects[skillObject.objectID] =
                    _s.World.Clock.UtcNow.AddMilliseconds(Math.Max(ability.AnimDurationMs, 1000) + 1000);
            }

            var result = Proto.New<SUseSkillResult>();
            result.skillID = abilityId;
            result.sourceObjectID = Session.PlayerObjectId;
            result.destObejctID = (int)targetObjectID;
            result.pos = location;

            PlayerStats stats = _s.Stats;
            var attributes = Views.AttributeMap(_s.Character, _s.World.Data, stats);
            List<Monster> targets = AbilityTargets(ability, targetObjectID, location, _s.Position).ToList();
            for (int hit = 0; hit < ability.AttackCount; hit++)
            {
                foreach (Monster monster in targets.Where(m => m.IsAlive))
                    Hit(result, monster, Combat.AbilityDamage(ability, _s.Character, _s.World.Data, attributes, _s.World.Random), stats);
            }

            // The revive slam also hits and staggers everything around the player and shields
            // the player briefly; all four numbers come from the tables (see ReviveShockwave).
            if (IsReviveShockwave(abilityId))
                ReviveShockwaveEffects(result, targets, abilityId);

            _s.Log.LogInformation("   ability hit {Count} monsters", targets.Count);

            _s.Send(BattleResponses.UseFightSkillResult(result));
            AwardKills();
        }

        /// <summary>
        /// The revive shockwave (12001 Revive_Shockwave, and 12002 RevivePrayer_Shockwave for
        /// item/crystal revivals): the client fires it on its own when a revive succeeds
        /// (SessionBattleClient.On_ReviveResult -> UseAbilityFromID), and the ability state then
        /// sends the UseFightSkill and waits - the VFX only plays when the answer arrives. It is
        /// not an ability the character learns, so it skips the ownership check; the table gives
        /// its 5 m radius, its damage and the four statuses below.
        /// </summary>
        private static bool IsReviveShockwave(int abilityId) => abilityId == ReviveShockwave || abilityId == RevivePrayerShockwave;

        public const int ReviveShockwave = 12001, RevivePrayerShockwave = 12002;

        // The push speed is the one number the tables do not carry; the monster staggers for the
        // 400 ms the DestStatusID entry (40002/40003 "Revival Knockback") prescribes, the player
        // is untouchable for the SelfStatusID duration (40018: 7 s, 40019 with a scroll: 12 s).
        private const float RevivePush = 8f, RevivePrayerPush = 12f;

        private void ReviveShockwaveEffects(SUseSkillResult result, List<Monster> targets, int abilityId)
        {
            bool prayer = abilityId == RevivePrayerShockwave;
            DateTime now = _s.World.Clock.UtcNow;

            result.statusEffectVec.Add(
                StatusEffect(prayer ? 40019 : 40018, EStatusOperateType.eStatusType_Add, new Dictionary<int, int>()));
            _s.InvulnerableUntil = now.AddMilliseconds(prayer ? 12_000 : 7_000);

            foreach (Monster monster in targets.Where(m => m.IsAlive))
            {
                monster.StunnedUntil = now.AddMilliseconds(400);

                // The physical shove: SetKnockBackSpeed behavior 1 is what the client applies.
                float dx = monster.Position.x - _s.Position.x;
                float dz = monster.Position.z - _s.Position.z;
                float length = Math.Max(0.01f, (float)Math.Sqrt(dx * dx + dz * dz));
                float push = prayer ? RevivePrayerPush : RevivePush;
                _s.Send(BattleResponses.SetKnockBackSpeed(monster.ObjectId,
                    new Vector3(dx / length * push, 0f, dz / length * push), 1));

                // The knockback pose rides along with the hit.
                SSkillEffect effect = result.skillEffectVec.Cast<SSkillEffect>()
                    .FirstOrDefault(e => e.targetID == monster.ObjectId);
                if (effect != null)
                    effect.statusEffectVec.Add(
                        StatusEffect(prayer ? 40003 : 40002, EStatusOperateType.eStatusType_Add, new Dictionary<int, int>()));
            }
        }

        /// <summary>The player's basic attack ability (AbilityIDs.NormalAttack_1H_ID), used for every weapon.</summary>
        private const int NormalAttack1H = 10001;

        public override void UseNormalAttack(EWeaponType weaponType, uint targetObjectID, uint curTime)
        {
            if (_s.Character == null || _s.IsDead)
                return;

            var result = Proto.New<SUseSkillResult>();
            // The player's melee ability is 10001 for every weapon on the client (MeleeAttackState);
            // results with 10002 (two-handed) or 10003 (fists) found no ability and showed no hit.
            result.skillID = NormalAttack1H;
            result.sourceObjectID = Session.PlayerObjectId;
            result.destObejctID = (int)targetObjectID;

            Monster target = _s.Scene?.Find((int)targetObjectID);
            if (target != null && target.IsAlive)
                Hit(result, target, Combat.WeaponDamage(_s.Character, _s.World.Data, _s.World.Random));

            // Baskets and chests are opened by hitting them like a monster.
            Breakable breakable = _s.Scene?.FindBreakable((int)targetObjectID);
            if (breakable != null && breakable.Hp > 0)
                HitBreakable(result, breakable, Combat.WeaponDamage(_s.Character, _s.World.Data, _s.World.Random));
            if (target == null && breakable == null && targetObjectID != 0)
                _s.Log.LogInformation("   attack on unknown object #{Id}", targetObjectID);

            // The client shows the hit the moment the result arrives (CS_SceneInfo.On_UpdateResult),
            // so it goes out when the swing lands instead of when it starts.
            SceneInstance scene = _s.Scene;
            double delay = SwingHitDelay();
            if (scene == null || delay <= 0)
            {
                FinishNormalAttack(result);
                return;
            }
            // Its own timer: the scene's events only run every AI tick (150 ms), too coarse for a swing.
            _s.World.Clock.After(TimeSpan.FromSeconds(delay), () =>
            {
                lock (_s.World.Sync)
                {
                    try
                    {
                        if (scene == _s.Scene)
                            FinishNormalAttack(result);
                    }
                    catch (Exception ex)
                    {
                        _s.Log.LogError(ex, "Basic attack failed");
                    }
                }
            });
        }

        private void FinishNormalAttack(SUseSkillResult result)
        {
            _s.Send(BattleResponses.UseNormalAttackResult(false, result));
            AwardKills();
            BreakObjects();
        }

        /// <summary>
        /// Seconds from the start of a basic attack until it hits: the AttackNotify event of the
        /// player's attack clips (Aka_0H/1H/2H/2HNodachi_Attack_*), played faster or slower by the
        /// weapon's swing speed and the attack speed bonus (MeleeAttackState.playAttackAnimation).
        /// </summary>
        public const double FistHit = 0.133, OneHandHit = 0.267, TwoHandHit = 0.333;

        /// <summary>The longest a swing can take until it hits (Great Maul).</summary>
        public const double MaxSwingHit = TwoHandHit * 1.3;

        private double SwingHitDelay()
        {
            ItemData weapon = _s.Character.Equipment.FirstOrDefault(e => e.Slot == Inventory.PartMainHand);
            if (weapon == null || !_s.World.Data.Weapons.TryGetValue(weapon.ItemId, out GameData.WeaponRow row))
                return FistHit * 100.0 / Math.Max(50, _s.Stats.AttackSpeed);
            double hit = (row.TwoHanded ? TwoHandHit : OneHandHit) * row.SwingFactor;
            return hit * 100.0 / Math.Max(50, _s.Stats.AttackSpeed);
        }

        /// <summary>Damages a basket, chest or switch; the client shows the hit on the object.</summary>
        private void HitBreakable(SUseSkillResult result, Breakable breakable, int damage)
        {
            breakable.Hp = Math.Max(0, breakable.Hp - damage);
            var effect = Proto.New<SSkillEffect>();
            effect.targetID = breakable.ObjectId;
            effect.targetPos = breakable.Position;
            effect.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, -damage));
            result.skillEffectVec.Add(effect);
        }

        /// <summary>
        /// Objects whose health is gone break: the client plays it on ObjectLeave (and counts the
        /// "destroy" objective, removes the tutorial arrow); karma drops, and whatever the object
        /// brings along (InteractiveHandler.LinkedEvents) comes, like the wolves in the basket.
        /// </summary>
        private void BreakObjects()
        {
            if (_s.Scene == null || _s.Character == null)
                return;
            List<Breakable> broken = _s.Scene.Breakables.Where(b => b.Hp <= 0).ToList();
            if (broken.Count == 0)
                return;

            var leaving = new vectorInt();
            foreach (Breakable breakable in broken)
            {
                _s.Scene.RemoveBreakable(breakable.ObjectId);
                leaving.Add(breakable.ObjectId);
                DropKarmaAt(breakable.Info.KarmaMin, breakable.Info.KarmaMax, breakable.Position);
                DropItems(Loot.Roll(breakable.Info, _s.World.Data, _s.World.Random), breakable.Position);
                _s.Log.LogInformation("   broke {Object} (type {Type})", breakable.Info.Name, breakable.Info.TypeId);
            }
            _s.Send(BattleResponses.ObjectLeave(leaving));

            foreach (Breakable breakable in broken)
            {
                if (breakable.Info.Exp > 0)
                    GrantExperience((ulong)breakable.Info.Exp);
                RecordMissionProgress(MissionObjectiveType.Destroy, breakable.Info.TypeId);
                _s.Triggers.OnObjectBroken(breakable);
            }
        }

        /// <summary>Abilities with this target type affect the caster (buffs, heals), not enemies.</summary>
        private const int TargetTypeSelf = 3;

        /// <summary>
        /// Who an ability hits.
        ///
        /// The client rarely sends a target: most abilities carry a position instead, and which
        /// position that is depends on the ability (PlayerAbilityBaseState.SendUseAbilityRequest):
        /// the caster's position, the point the player aimed at, or just a direction. So the server
        /// takes the sent position when it is a real place on the map, and otherwise falls back to
        /// the player's own position, then hits everything within the ability's reach.
        /// </summary>
        private IEnumerable<Monster> AbilityTargets(GameData.AbilityRow ability, uint targetObjectID, Vector3 location, Vector3 casterPos)
        {
            if (_s.Scene == null || ability.TargetType == TargetTypeSelf)
                return Enumerable.Empty<Monster>();

            // A ranged shot names its target (PlayerAbilityShot).
            if (targetObjectID != 0)
            {
                Monster target = _s.Scene.Find((int)targetObjectID);
                return target != null && target.IsAlive ? new[] { target } : Enumerable.Empty<Monster>();
            }

            float reach = ability.AreaRadius > 0 ? ability.AreaRadius : 3f;
            Vector3 center = IsPlaceOnTheMap(location, casterPos) ? location : casterPos;
            return _s.Scene.Near(center, reach).ToList();
        }

        /// <summary>
        /// Tells a world position from a direction vector: directions are short and point away from
        /// the caster, while a real target position is somewhere near them on the map.
        /// </summary>
        private bool IsPlaceOnTheMap(Vector3 location, Vector3 casterPos)
        {
            const float maxCastDistance = 40f;
            if (Math.Abs(location.x) < 2f && Math.Abs(location.z) < 2f)
                return false; // a direction vector, or nothing at all
            return SceneInstance.Distance(location, casterPos) <= maxCastDistance;
        }

        /// <summary>Applies damage to a monster and adds the hit to the result the client will show.</summary>
        private void Hit(SUseSkillResult result, Monster monster, int damage, PlayerStats stats = null)
        {
            stats ??= _s.Stats;
            (damage, bool critical) = Combat.Boost(damage, stats, _s.World.Random);

            var effect = Proto.New<SSkillEffect>();
            effect.targetID = monster.ObjectId;
            effect.targetPos = monster.Position;
            effect.isCritical = critical;
            damage += ApplyProcs(result, effect, monster, damage, stats);

            _s.MonsterAi.Provoke(monster);
            monster.Hp = System.Math.Max(0, monster.Hp - damage);
            effect.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, -damage));
            result.skillEffectVec.Add(effect);

            _s.Log.LogInformation("   hit {Monster} #{Id} for {Damage} ({Hp} left)",
                monster.Type.Name, monster.ObjectId, damage, monster.Hp);
        }

        /// <summary>
        /// What the hit sets off (weapon elements, Two-Handed mastery): stun, slow and knockback hold
        /// the monster, burn adds its damage at once, Thirsty heals the player. The status goes along
        /// with the hit so the client shows it. Returns extra damage.
        /// </summary>
        private int ApplyProcs(SUseSkillResult result, SSkillEffect effect, Monster monster, int damage, PlayerStats stats)
        {
            int extra = 0;
            DateTime now = _s.World.Clock.UtcNow;
            foreach (HitProc proc in stats.Procs)
            {
                if (_s.World.Random.NextDouble() * 100 >= proc.Chance)
                    continue;
                switch (proc.Kind)
                {
                    case "Stun":
                        monster.StunnedUntil = now.AddSeconds(Math.Max(0.5, proc.Seconds));
                        break;
                    case "Knockback":
                        monster.StunnedUntil = now.AddSeconds(0.5);
                        break;
                    case "Slow":
                        monster.SlowedUntil = now.AddSeconds(Math.Max(0.5, proc.Seconds));
                        break;
                    case "Burn":
                        extra += (int)Math.Round(proc.Value);
                        break;
                    case "Heal":
                        int heal = Math.Min((int)Math.Round(damage * proc.Value / 100f), Math.Max(0, stats.MaxHp - _s.CurrentHp));
                        if (heal > 0 && !_s.IsDead)
                        {
                            _s.CurrentHp += heal;
                            var healed = Proto.New<SSkillEffect>();
                            healed.targetID = Session.PlayerObjectId;
                            healed.targetPos = _s.Position;
                            healed.attributeChangeVec.Add(Proto.Attr(EAttributeType.ATTR_CurHP, heal));
                            result.skillEffectVec.Add(healed);
                        }
                        break;
                }
                if (proc.StatusId != 0)
                {
                    var status = Proto.New<SStatusEffect>();
                    status.statustID = proc.StatusId;
                    status.statusOperateType = new EStatusOperateType(EStatusOperateType.eStatusType_Add);
                    status.soureObjectID = Session.PlayerObjectId;
                    status.destObjectID = monster.ObjectId;
                    effect.statusEffectVec.Add(status);
                }
                _s.Log.LogInformation("   {Proc} on {Monster} #{Id}", proc.Kind, monster.Type.Name, monster.ObjectId);
            }
            return extra;
        }

        /// <summary>
        /// Experience per kill is the prefab's XP times this. The original server's formula is lost;
        /// recordings of the original show 12 for an Insane Wolf (XP 3) and 24 for the Amano
        /// Harbinger (XP 6) at level 1.
        /// </summary>
        public const int MonsterXpFactor = 4;

        /// <summary>
        /// "Wanted" monsters pay a bounty: the client shows the panel with the prefab's values
        /// (RewardWantedExp, RewardWantedSk), the server grants exactly those.
        /// </summary>
        private void PayBounty(GameData.MonsterRow type)
        {
            _s.Log.LogInformation("   bounty for {Monster}: {Xp} xp, {Karma} karma", type.Name, type.WantedXp, type.WantedKarma);
            if (type.WantedKarma > 0)
            {
                _s.Account.Karma += type.WantedKarma;
                _s.SendMoney();
            }
            GrantExperience((ulong)Math.Max(0, type.WantedXp));
        }

        public void GrantExperience(ulong xp)
        {
            if (xp == 0)
                return;
            _s.Character.Exp += xp;
            // catolog 1 = character experience (see client On_AddExpNTF / On_LevelUp)
            _s.Send(GameResponses.AddExpNTF(1, xp));
            CheckLevelUp();
            _s.SaveAccount();
        }

        /// <summary>Grants experience for monsters that died from the last attack and forgets them.</summary>
        private void AwardKills()
        {
            if (_s.Scene == null || _s.Character == null)
                return;

            var dead = _s.Scene.Monsters.Where(m => !m.IsAlive).ToList();
            if (dead.Count == 0)
                return;

            ulong xp = 0;
            var leaving = new vectorInt();
            foreach (Monster monster in dead)
            {
                xp += (ulong)(monster.Type.Xp * MonsterXpFactor);
                leaving.Add(monster.ObjectId);
                DropKarma(monster);
                DropLoot(monster);
                _s.Scene.Remove(monster.ObjectId);
                _s.Log.LogInformation("   killed {Monster} (+{Xp} xp)", monster.Type.Name, monster.Type.Xp * MonsterXpFactor);
                if (monster.Type.IsWanted)
                    PayBounty(monster.Type);
                RecordMissionProgress(MissionObjectiveType.Hunt, monster.Type.TypeId);
            }

            // Sake and Shochu: more experience from monsters.
            PlayerStats stats = _s.Stats;
            if (stats.XpPct > 0)
                xp = (ulong)Math.Round(xp * (1 + stats.XpPct / 100.0));

            _s.Spirits.GainExperience(xp);

            // Before ObjectLeave: the client counts the chain on it once the server has started it.
            _s.KillChain.OnKills(dead.Count, xp);

            // ObjectLeave is what makes the client play the death animation and remove the corpse
            // (CS_SceneInfo.OnObjectLeave -> NpcBase.NotifyDie).
            _s.Send(BattleResponses.ObjectLeave(leaving));

            GrantExperience(xp);

            // Next waves, gates and other level events that wait for these deaths.
            foreach (Monster monster in dead)
                _s.Triggers.OnMonsterKilled(monster);
        }

        /// <summary>Reaching a place the mission data files as a hunt (see MissionProgress.MisfiledPlaces).</summary>
        private void CheckMisfiledPlaces(Vector3 pos)
        {
            if (_s.Mission == null)
                return;
            foreach (MissionObjective place in _s.Mission.MisfiledPlaces(id => _s.Scene?.Monsters.Any(m => m.Type.TypeId == id) == true))
            {
                if (Math.Abs(pos.x - place.ObjectId) <= 5 && Math.Abs(pos.z - place.Recycle) <= 5)
                    RecordMissionProgress(place.TypeId, place.ObjectId);
            }
        }

        /// <summary>
        /// Counts an event towards the current mission and reports finished tasks, branches and
        /// missions to the client, which shows the panels and the rewards.
        /// </summary>
        public void RecordMissionProgress(int objectiveType, int objectId)
        {
            if (_s.Mission == null || _s.Character == null)
                return;

            foreach (MissionProgress.Completion done in _s.Mission.Record(objectiveType, objectId))
            {
                _s.Log.LogInformation("   mission task done: {Task} ({Objective})",
                    done.Task.Name, done.Task.Objectives.FirstOrDefault()?.Description);
                GrantMissionReward(done.Task.RewardExp, done.Task.RewardKarma);
                _s.Triggers.OnTaskCompleted(done.Task.TriggerId);
                _s.Send(GameResponses.taskCompleteNTF(_s.Mission.Mission.Id, done.Branch.BranchId, done.Task.TaskId,
                    done.Task.RewardExp, done.Task.RewardKarma));

                if (done.BranchCompleted)
                {
                    _s.Log.LogInformation("   mission branch done: {Branch}", done.Branch.Name);
                    GrantMissionReward(done.Branch.RewardExp, done.Branch.RewardKarma);
                    _s.Send(GameResponses.branchCompleteNTF(_s.Mission.Mission.Id, done.Branch.BranchId, done.Task.TaskId,
                        done.Branch.RewardExp, done.Branch.RewardKarma));
                }
            }

            if (!_s.Mission.IsComplete)
                return;

            MissionDefinition mission = _s.Mission.Mission;
            _s.Log.LogInformation("   mission complete: {Name} (+{Xp} xp, +{Karma} karma)",
                mission.Name, mission.RewardExp, mission.RewardKarma);
            GrantMissionReward(mission.RewardExp, mission.RewardKarma);
            _s.Send(GameResponses.MissionCompleteNTF(mission.Id, mission.RewardExp, mission.RewardKarma));
            _s.Character.TutorialDone = true; // finishing a mission ends the tutorial state
            if (!_s.Character.CompletedMissions.Contains(mission.Id))
                _s.Character.CompletedMissions.Add(mission.Id);
            MissionMap.Finished(_s.Character, mission.Id, new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds(), _s.World.Data);
            _s.Mission = null;
            _s.SaveAccount(); // a reached place finishes missions too, without a kill that saves
        }

        private void GrantMissionReward(int exp, int karma)
        {
            if (exp > 0)
            {
                _s.Character.Exp += (ulong)exp;
                _s.Send(GameResponses.AddExpNTF(1, (ulong)exp));
                CheckLevelUp();
            }
            if (karma > 0)
            {
                _s.Account.Karma += karma;
                _s.SendMoney();
            }
        }

        /// <summary>
        /// Raises the character's level while its experience passes the thresholds of the level
        /// table, and tells the client about the new level and the attributes that come with it.
        /// </summary>
        private void CheckLevelUp()
        {
            GameData data = _s.World.Data;
            bool leveled = false;

            while (_s.Character.Level < data.Levels.Count &&
                   _s.Character.Exp >= (ulong)data.Level(_s.Character.Level + 1).ExpTotal)
            {
                _s.Character.Level++;
                leveled = true;
                _s.Log.LogInformation("   level up: {Name} is now level {Level}", _s.Character.Name, _s.Character.Level);
            }

            if (!leveled)
                return;

            // A new level means more health and energy; refill both, like the original did on level up.
            PlayerStats full = _s.Stats;
            _s.CurrentHp = full.MaxHp;
            _s.CurrentMp = full.MaxMp;

            _s.Send(GameResponses.LevelUp(_s.Character.Level, 1));
            vectorAttrChange attributes = Views.Attributes(_s.Character, data, full);
            _s.Send(GameResponses.UpdateBaseAttr(attributes));
            _s.Send(GameResponses.ChangeCharacterAttribNTF(attributes));
        }

        /// <summary>
        /// Tells the client the character's attributes after gear or talents changed, keeping the
        /// health and energy it has right now (a full update with CurHP 0 would kill it).
        /// </summary>
        public void SendAttributes()
        {
            if (_s.Character == null)
                return;
            _s.ApplyRegeneration();
            Dictionary<int, int> map = Views.AttributeMap(_s.Character, _s.World.Data, _s.Stats);
            map[EAttributeType.ATTR_CurHP] = Math.Max(1, _s.CurrentHp);
            map[EAttributeType.ATTR_CurMP] = _s.CurrentMp;
            vectorAttrChange attributes = Proto.Attrs(map);
            _s.Send(GameResponses.UpdateBaseAttr(attributes));
            _s.Send(GameResponses.ChangeCharacterAttribNTF(attributes));
        }

        /// <summary>
        /// Puts a buff on the player (icon and effect on the client) with the attribute changes it
        /// brings right away, and takes it off again when its time is up.
        /// </summary>
        public void ApplyBuff(GameData.BuffRow buff, Dictionary<int, int> changes)
        {
            if (buff.Duration > 0)
            {
                // Food and drink: the stats count it until it runs out (PlayerStats). Taking the
                // same thing again replaces the running buff; its old expiry moment must not
                // cancel the fresh one, so removal runs exactly once per entry in ExpireBuffs
                // (called from the AI tick), not from a timer tied to the scene of the drink.
                _s.ActiveBuffs.RemoveAll(b => b.Buff.Id == buff.Id || b.Until <= _s.World.Clock.UtcNow);
                _s.ActiveBuffs.Add(new ActiveBuff(buff, _s.World.Clock.UtcNow.AddMilliseconds(buff.Duration)));
            }
            _s.Send(BattleResponses.UpdateStatusEffect(new vectorStatusEffects { StatusEffect(buff.Id, EStatusOperateType.eStatusType_Add, changes) }));
        }

        /// <summary>
        /// Takes every buff whose time is up off the player: the icon goes away on the client and
        /// the attributes drop back. Runs on the AI tick, so a map change or a refreshed buff
        /// cannot leave a dead icon up or a bonus active.
        /// </summary>
        public void ExpireBuffs()
        {
            if (_s.Character == null)
                return;

            DateTime now = _s.World.Clock.UtcNow;
            List<ActiveBuff> expired = _s.ActiveBuffs.Where(b => b.Until <= now).ToList();
            if (expired.Count == 0)
                return;

            _s.ActiveBuffs.RemoveAll(expired.Contains);
            var effects = new vectorStatusEffects();
            foreach (ActiveBuff buff in expired)
                effects.Add(StatusEffect(buff.Buff.Id, EStatusOperateType.eStatusType_Delete, new Dictionary<int, int>()));
            _s.Send(BattleResponses.UpdateStatusEffect(effects));
            _s.Log.LogInformation("   {Count} buff(s) ran out", expired.Count);
            SendAttributes();
        }

        private static SStatusEffect StatusEffect(int buffId, int operation, Dictionary<int, int> changes)
        {
            var effect = Proto.New<SStatusEffect>();
            effect.statustID = buffId;
            effect.statusOperateType = new EStatusOperateType(operation);
            effect.soureObjectID = Session.PlayerObjectId;
            effect.destObjectID = Session.PlayerObjectId;
            effect.attribueVec = Proto.Attrs(changes);
            return effect;
        }

        /// <summary>KarmaGroupManager.EnumKarmaType: 1 = normal karma, 2 = purple karma.</summary>
        private const int NormalKarmaType = 1;

        /// <summary>Drops the monster's karma shards, which the player can walk over to pick up.</summary>
        private void DropKarma(Monster monster) =>
            DropKarmaAt(monster.Type.KarmaMin, monster.Type.KarmaMax, monster.Position);

        private void DropKarmaAt(int karmaMin, int karmaMax, Vector3 position)
        {
            int karma = _s.World.Random.Next(karmaMin, Math.Max(karmaMin, karmaMax) + 1);
            float karmaPct = _s.Stats.KarmaPct; // Gold gems: shards are worth more
            if (karmaPct > 0)
                karma = (int)Math.Round(karma * (1 + karmaPct / 100f));
            if (karma <= 0)
                return;

            int objectId = _s.Scene.NewObjectId();
            _s.Scene.Karma[objectId] = karma;

            // The client spawns one shard object per Distribution entry value at moneyInfo.pos
            // (KarmaGroupManager.CreateKarmaWithMoneyInfo); karma type 1 is the normal shard.
            var info = Proto.New<SMoneyEnter>();
            info.objectID = objectId;
            info.money = karma;
            info.pos = position;
            info.hp = 0;
            info.Distribution.Add(new SServerMapMoney { ID = NormalKarmaType, Value = karma });
            _s.Send(BattleResponses.moneyEnter(info));
        }

        /// <summary>Drops the monster's loot on the ground, where the player can pick it up.</summary>
        private void DropLoot(Monster monster)
        {
            // The tutorial is scripted: no random materials there (its pelt comes from the collect target).
            List<ItemData> loot = Loot.Roll(monster.Type, _s.World.Data, _s.World.Random, materials: _s.SceneName != "Hub_Village_Tutorial");
            // Plum Wine and Awamori: now and then one more piece of equipment.
            if (monster.Type.CanDropLoot && _s.World.Random.NextDouble() * 100 < _s.Stats.LootPct)
            {
                ItemData extra = Loot.RollEquipment(_s.World.Data, Math.Max(1, monster.Type.LootLevel), _s.World.Random);
                if (extra != null)
                    loot.Add(extra);
            }

            // The tutorial waits for items only the server can drop (the pelt after the basket);
            // the next monster with fixed loot of its own brings them, like in the original.
            if (_s.Scene.PendingCollectTargets.Count > 0 && monster.Type.UniqueDrops?.Count > 0)
            {
                foreach (GameData.CollectTarget target in _s.Scene.PendingCollectTargets)
                {
                    for (int i = 0; i < Math.Max(1, target.Count); i++)
                    {
                        var item = new ItemData { ItemId = target.ItemId, PrefabId = 0, Level = Math.Max(1, monster.Type.LootLevel), Count = 1 };
                        // Quest items need the client's index 1: a COLLECT task matches on
                        // perfrab == its "recycle" (MissionLogic checks both), which is 1 in every
                        // mission, and index 0 (the world model) has no icon.
                        _s.World.Data.EnsurePrefab(item);
                        loot.Add(item);
                    }
                }
                _s.Scene.PendingCollectTargets.Clear();
            }

            DropItems(loot, monster.Position);
        }

        /// <summary>
        /// Gap between two item drops. The client puts each item's name label up in a coroutine that
        /// waits for the label list to grow by exactly one (CS_SceneInfo.ShowItem); items arriving
        /// together break that count and stay without a name.
        /// </summary>
        public const double DropGapSeconds = 0.3;

        private DateTime _nextDropAt;

        private void SendDropStaggered(SItemEnter drop)
        {
            DateTime now = _s.World.Clock.UtcNow;
            SceneInstance scene = _s.Scene;
            if (_nextDropAt <= now || scene == null)
            {
                _nextDropAt = now.AddSeconds(DropGapSeconds);
                _s.Send(BattleResponses.ItemEnter(drop));
                return;
            }
            DateTime at = _nextDropAt;
            _nextDropAt = at.AddSeconds(DropGapSeconds);
            scene.Schedule(at, () =>
            {
                if (scene == _s.Scene && scene.Items.ContainsKey(drop.objectID))
                    _s.Send(BattleResponses.ItemEnter(drop));
            });
        }

        /// <summary>Lays items on the ground around a place, where the player can pick them up.</summary>
        private void DropItems(List<ItemData> loot, Vector3 position)
        {
            foreach (ItemData item in loot)
            {
                int objectId = _s.Scene.NewObjectId();
                item.Serial = objectId;
                _s.Scene.Items[objectId] = item;

                var drop = Proto.New<SItemEnter>();
                drop.objectID = objectId;
                drop.iteminfo = Views.Item(item);
                drop.pos = new Vector3(
                    position.x + (float)(_s.World.Random.NextDouble() * 2 - 1),
                    position.y,
                    position.z + (float)(_s.World.Random.NextDouble() * 2 - 1));
                drop.scale = new Vector3(1f, 1f, 1f);
                SendDropStaggered(drop);
                _s.Log.LogInformation("   dropped item {Item} (prefab {Prefab}, level {Level})",
                    item.ItemId, item.PrefabId, item.Level);
            }
        }

        public override void PickupItemReq(int objectID)
        {
            if (_s.Character == null || _s.Scene == null)
                return;

            if (!_s.Scene.Items.TryGetValue(objectID, out ItemData item))
            {
                _s.Send(BattleResponses.PickupItemFailed(objectID, Proto.Error(EServerErrorType.eUnknownError)));
                return;
            }

            if (!_s.Inventory.CanAdd(item))
            {
                _s.Log.LogInformation("   bag full, item {Item} stays on the ground", item.ItemId);
                _s.Send(BattleResponses.PickupItemFailed(objectID, Proto.Error(EServerErrorType.eItemError_BagFull)));
                return;
            }

            _s.Scene.Items.Remove(objectID);
            _s.Send(BattleResponses.PickupItemSuccAck(objectID, Proto.Error(EServerErrorType.eSuccess)));
            _s.Inventory.AddToBag(item); // stacks food and potions, announces the changed slots
            _s.SaveAccount();
            _s.Log.LogInformation("   picked up item {Item}", item.ItemId);
            // Only ObjectLeave takes the item out of the world; it also removes the arrow above it
            // and counts towards the tutorial's collect step (CS_SceneInfo.OnObjectLeave).
            _s.Send(BattleResponses.ObjectLeave(new vectorInt { objectID }));

            RecordMissionProgress(MissionObjectiveType.Collect, item.ItemId);
        }

        public override void PickupMoneyReq(int objectID)
        {
            if (_s.Character == null || _s.Scene == null)
                return;

            if (!_s.Scene.Karma.TryGetValue(objectID, out int karma))
            {
                _s.Send(BattleResponses.PickupMoneyResult(objectID, Proto.Error(EServerErrorType.eUnknownError)));
                return;
            }

            _s.Scene.Karma.Remove(objectID);
            _s.Account.Karma += karma;
            _s.SaveAccount();
            _s.Log.LogInformation("   picked up {Karma} karma (total {Total})", karma, _s.Account.Karma);

            _s.Send(BattleResponses.PickupMoneyResult(objectID, Proto.Error(EServerErrorType.eSuccess)));
            _s.SendMoney();
        }

        private void Fail(uint targetObjectID, int abilityId, int error)
        {
            _s.Log.LogInformation("   ability {Ability} rejected: {Error}", abilityId, error);
            _s.Send(BattleResponses.UseFightSkillFailed(Session.PlayerObjectId, (uint)abilityId, Proto.Error(error)));
        }

        // ---- death and revival ----------------------------------------------------------------
        // The client opens its revive screen by itself once the player's health reaches zero
        // (DeathState) and then asks to be revived: type 1 = free, 0 = karma, 4 = crystals.

        private const int ReviveWithKarma = 0;
        private const int ReviveFree = 1;
        private const int ReviveWithItem = 3;   // consumes one Shinigami Scroll from the bag
        private const int ReviveWithCrystal = 4;

        public override void ReviveReq(EReviveType reviveType)
        {
            if (_s.Character == null)
                return;

            int type = reviveType.Get();
            int cost = type == ReviveWithKarma ? _s.World.Data.RevivalCost(_s.Character.Level, _s.Revivals) : 0;

            if (type == ReviveWithKarma && _s.Account.Karma < cost)
            {
                _s.Log.LogInformation("   revival refused: {Cost} karma needed, {Have} available", cost, _s.Account.Karma);
                _s.Send(BattleResponses.ReviveResult(reviveType, Proto.Error(EServerErrorType.eGS_LessMoney_karma), new vectorAttrChange()));
                return;
            }

            // A scroll revival pays with a Shinigami Scroll out of the bag; without one it fails,
            // like it did on the original (eGS_LessReviveItem).
            ItemData scroll = type == ReviveWithItem
                ? _s.Character.Bag.FirstOrDefault(i => i.ItemId == Views.ReviveScrollItem)
                : null;
            if (type == ReviveWithItem && scroll == null)
            {
                _s.Log.LogInformation("   revival refused: no Shinigami Scroll in the bag");
                _s.Send(BattleResponses.ReviveResult(reviveType, Proto.Error(EServerErrorType.eGS_LessReviveItem), new vectorAttrChange()));
                return;
            }

            if (cost > 0)
            {
                _s.Account.Karma -= cost;
                _s.SendMoney();
            }
            // Crystal revivals cost the real-money currency, which is free on this server.

            if (scroll != null)
            {
                _s.Inventory.Consume(Views.BagBase, scroll); // announces the changed bag slot
                _s.Log.LogInformation("   spent one Shinigami Scroll ({Left} left on the account)",
                    Views.ReviveScrollCount(_s.Account));
                _s.Send(GameResponses.UpdateReviveNum(Views.ReviveScrollCount(_s.Account)));
            }

            _s.Revivals++;
            PlayerStats full = _s.Stats;
            _s.CurrentHp = full.MaxHp;
            _s.CurrentMp = full.MaxMp;
            _s.LastRegen = _s.World.Clock.UtcNow;
            _s.SaveAccount();
            _s.Log.LogInformation("   revived (type {Type}, {Cost} karma, revival #{Count})", type, cost, _s.Revivals);

            _s.Send(BattleResponses.ReviveResult(reviveType, Proto.Error(EServerErrorType.eSuccess),
                Views.Attributes(_s.Character, _s.World.Data, full)));
            _s.Send(BattleResponses.PlayerReviveNumNTF(0, _s.Revivals));
        }

        // ---- movement -----------------------------------------------------------------------

        /// <summary>How far the player has to walk before the server looks for new monsters again.</summary>
        private const float StreamStep = 5f;

        private Vector3 _lastStreamPosition;

        public override void UpdateMoveRequest(Vector3 pos, Vector3 target, float rot, uint continueTime)
        {
            // Accepted as-is for now; the server does not validate movement.
            _s.Position = pos;
            _s.Scene?.Nav.MarkPlayer(pos); // where the player walks, monsters can walk
            _s.Triggers.CheckPosition(pos);
            CheckMisfiledPlaces(pos);

            if (SceneInstance.Distance(pos, _lastStreamPosition) < StreamStep)
                return;
            _lastStreamPosition = pos;
            SendMonstersNearPlayer();
        }

        public override void ActiveTriggerReq(int triggerID)
        {
            _s.Log.LogInformation("   lever pulled: trigger {Trigger}", triggerID);
            _s.Triggers.OnLeverPulled(triggerID);
        }

        public override void SetSkillShortcut(int skillID, int groupID, int idx)
        {
            if (_s.Character == null)
                return;

            _s.Character.Shortcuts.RemoveAll(s => s.Group == groupID && s.Index == idx);
            if (skillID != 0)
                _s.Character.Shortcuts.Add(new ShortcutData { Group = groupID, Index = idx, AbilityId = skillID });
            _s.SaveAccount();
        }
    }
}
