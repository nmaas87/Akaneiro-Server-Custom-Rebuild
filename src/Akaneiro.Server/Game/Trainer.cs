using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Takeda the trainer: abilities and weapon/armor masteries (the client's ability shop).
    /// <para>
    /// Abilities cost karma and need POW, DEF or SKILL (the one of their discipline) of at least
    /// Ability.ReqBaseStatAmount; rank II replaces rank I (PlayerAbilityManager.AddSkill swaps
    /// abilities of one kind). They are learned at once, their TrainingTime is 0.
    /// </para>
    /// <para>
    /// Masteries (EquipMastery.level) work the same way but take their TrainTime; the client shows
    /// the countdown from UpdateCoolDown (unix seconds, kept under the id of the level being trained)
    /// and can finish it early with crystals (SpeedUpCoolDown), which cost nothing here.
    /// </para>
    /// </summary>
    public sealed class Trainer
    {
        /// <summary>The two masteries each discipline starts with (the creation screen lists them).</summary>
        private static readonly Dictionary<int, int[]> StarterMasteries = new()
        {
            [StarterLoadout.Prowess] = new[] { EMasteryType.eDualWeaponType, EMasteryType.eMediumArmorType },
            [StarterLoadout.Fortitude] = new[] { EMasteryType.e2HWeaponType, EMasteryType.eHeavyArmorType },
            [StarterLoadout.Cunning] = new[] { EMasteryType.e1HWeaponType, EMasteryType.eLightArmorType },
        };

        private readonly Session _s;

        public Trainer(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private GameData Data => _s.Data;
        private long Now => new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();

        /// <summary>POW, DEF or SKILL, whichever the discipline (1, 2, 4) trains.</summary>
        private int StatFor(int discipline)
        {
            var stats = Views.BaseStats(Character);
            return discipline switch
            {
                StarterLoadout.Prowess => stats.Power,
                StarterLoadout.Fortitude => stats.Defense,
                StarterLoadout.Cunning => stats.Skill,
                _ => 0,
            };
        }

        private bool Pay(int karma)
        {
            if (_s.Account.Karma < karma)
                return false;
            _s.Account.Karma -= karma;
            _s.SendMoney();
            return true;
        }

        // ---- abilities ----------------------------------------------------------------------------

        public void StudyAbility(int npcId, int abilityId)
        {
            int error = TryStudy(abilityId);
            if (error != EServerErrorType.eSuccess)
            {
                _s.Log.LogInformation("   study ability {Ability} rejected ({Error})", abilityId, error);
                _s.Send(GameResponses.StudyAbilityFailed(Proto.Error(error), npcId, abilityId));
                return;
            }
            _s.Send(GameResponses.StudyAbilitySucceed(npcId, abilityId));
            _s.Send(GameResponses.AddNewAbilityNotify(abilityId));
        }

        private int TryStudy(int abilityId)
        {
            if (!Data.Abilities.TryGetValue(abilityId, out GameData.AbilityRow ability) || ability.KarmaCost <= 0)
                return EServerErrorType.eGS_SkillIDInvalid;

            AbilityData known = Character.Abilities.FirstOrDefault(a => a.Id / 100 == ability.Kind);
            int knownRank = known == null ? 0 : known.Id % 100;
            if (knownRank >= ability.Rank)
                return EServerErrorType.eGS_SkillExist;
            if (knownRank != ability.Rank - 1)
                return EServerErrorType.eGS_SkillIDInvalid; // ranks are learned one after the other
            if (StatFor(ability.Discipline) < ability.RequiredStat)
                return EServerErrorType.eGS_LessLevel;
            if (!Pay(ability.KarmaCost))
                return EServerErrorType.eGS_LessMoney_karma;

            if (known == null)
            {
                Character.Abilities.Add(new AbilityData { Id = abilityId });
            }
            else
            {
                // The new rank takes the old one's place, also on the shortcut bar.
                foreach (ShortcutData shortcut in Character.Shortcuts.Where(s => s.AbilityId == known.Id))
                    shortcut.AbilityId = abilityId;
                known.Id = abilityId;
            }
            _s.SaveAccount();
            _s.Log.LogInformation("   learned {Ability} for {Karma} karma", ability.Name, ability.KarmaCost);
            return EServerErrorType.eSuccess;
        }

        // ---- masteries ----------------------------------------------------------------------------

        /// <summary>Gives the character its discipline's two masteries once. Returns true if anything changed.</summary>
        public static bool GrantStarterMasteries(CharacterData character)
        {
            if (character.StarterMasteriesGranted)
                return false;
            character.StarterMasteriesGranted = true;
            if (StarterMasteries.TryGetValue(character.Style, out int[] types))
            {
                foreach (int type in types)
                    character.Masteries[type] = Math.Max(1, character.Masteries.GetValueOrDefault(type));
            }
            return true;
        }

        public void SendMasteries()
        {
            FinishTraining();
            var list = new vectorMasteryLevelInfo();
            foreach (var (type, level) in Character.Masteries.Where(m => m.Value > 0).OrderBy(m => m.Key))
                list.Add(new SMasteryLevelInfo { masteryType = new EMasteryType(type), level = level });
            _s.Send(GameResponses.RoleMasteryListAck(list));
        }

        public void LevelUpMastery(int masteryType)
        {
            int error = TryTrain(masteryType);
            _s.Send(GameResponses.masteryLvlupAck(new EMasteryType(masteryType), Proto.Error(error)));
            if (error != EServerErrorType.eSuccess)
            {
                _s.Log.LogInformation("   mastery {Type} rejected ({Error})", masteryType, error);
                return;
            }
            MasteryTraining training = Character.Training;
            _s.Send(GameResponses.UpdateCoolDown(new ECooldownType(ECooldownType.eCooldownType_Mastery), training.MasteryId, training.ReadyUnix));
            FinishTraining(); // a level without training time is done right away
        }

        private int TryTrain(int masteryType)
        {
            FinishTraining();
            if (Character.Training != null)
                return EServerErrorType.eMasteryError_CoolDown;

            int level = Character.Masteries.GetValueOrDefault(masteryType);
            GameData.MasteryRow next = Data.Masteries.Values.FirstOrDefault(m => m.Type == masteryType && m.Level == level + 1);
            if (next == null)
                return EServerErrorType.eMasteryError_fullLvl;
            if (StatFor(next.Discipline) < next.RequiredStat)
                return EServerErrorType.eGS_LessLevel;
            if (!Pay(next.KarmaCost))
                return EServerErrorType.eGS_LessMoney_karma;

            Character.Training = new MasteryTraining { MasteryId = next.Id, MasteryType = masteryType, ReadyUnix = Now + next.TrainSeconds };
            _s.SaveAccount();
            _s.Log.LogInformation("   training {Mastery} ({Seconds} s)", next.Name, next.TrainSeconds);
            return EServerErrorType.eSuccess;
        }

        /// <summary>Completes the mastery training once its time is up; checked on requests and by the session tick.</summary>
        public void FinishTraining()
        {
            MasteryTraining training = Character?.Training;
            if (training == null || Now < training.ReadyUnix)
                return;
            Character.Training = null;
            Character.Masteries[training.MasteryType] = Character.Masteries.GetValueOrDefault(training.MasteryType) + 1;
            _s.SaveAccount();
            _s.Log.LogInformation("   mastery {Type} is now level {Level}", training.MasteryType, Character.Masteries[training.MasteryType]);
            _s.Send(GameResponses.AddNewMasteryNotify(training.MasteryId));
        }

        /// <summary>After login: the running training's countdown, and the server clock the client counts against.</summary>
        public void SendTimers()
        {
            _s.Send(GameResponses.ServerSystemTimeNotify(Now));
            FinishTraining();
            MasteryTraining training = Character.Training;
            if (training != null)
                _s.Send(GameResponses.UpdateCoolDown(new ECooldownType(ECooldownType.eCooldownType_Mastery), training.MasteryId, training.ReadyUnix));
        }

        /// <summary>Finishing a training early costs crystals, the real-money currency; here it is free.</summary>
        public void SpeedUp(int cooldownType, int id, int decreaseType)
        {
            MasteryTraining training = Character.Training;
            if (cooldownType != ECooldownType.eCooldownType_Mastery || training == null || training.MasteryId != id)
            {
                _s.Send(GameResponses.SpeedUpCoolDownResult(Proto.Error(EServerErrorType.eUnknownError), new ECooldownType(cooldownType), id, 0));
                return;
            }
            training.ReadyUnix = decreaseType switch
            {
                EDecreaseCooldownType.eDecreaseCooldownType_30 => training.ReadyUnix - 30 * 60,
                EDecreaseCooldownType.eDecreaseCooldownType_60 => training.ReadyUnix - 60 * 60,
                _ => Now,
            };
            training.ReadyUnix = Math.Max(Now, training.ReadyUnix);
            _s.SaveAccount();
            _s.Send(GameResponses.SpeedUpCoolDownResult(Proto.Error(EServerErrorType.eSuccess), new ECooldownType(cooldownType), id, training.ReadyUnix));
            FinishTraining();
        }
    }
}
