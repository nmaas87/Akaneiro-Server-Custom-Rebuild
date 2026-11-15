using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Protocol;
using Microsoft.Extensions.Logging;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Shimizu's spirit helpers (PETNPC). They are rented for an hour, a day or a week at the
    /// prices of PetPrice.Price; the client opens its spirit shop on RolePetListAck, buys with
    /// BuyPet and, once that succeeded, picks the spirit with choosePet. The spirit itself follows
    /// the player on the client side (Player.CallOnSpirit) until PetRentTimeEndNTF.
    /// The chosen spirit earns the experience its hunter earns from monsters and grows to the
    /// level of PetPrice.XPNeed; its bonus ("Increases Power 2%") counts in PlayerStats.
    /// </summary>
    public sealed class Spirits
    {
        // Rent buttons of UI_SpriteShop_DetailPanel: 3 = one hour, 7 = one day, 12 = one week.
        public const int RentHour = 3, RentDay = 7, RentWeek = 12;

        private readonly Session _s;

        public Spirits(Session session) => _s = session;

        private CharacterData Character => _s.Character;
        private long Now => new DateTimeOffset(_s.World.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();

        /// <summary>Spirits of one kind share the id without its last two digits (Inugami 10xx).</summary>
        private static int Kind(int petId) => petId / 100;

        /// <summary>
        /// The level-1 id of a spirit's line (1002 -> 1001). A rental keeps this id and never
        /// grows with the spirit: the client owns exactly one 3D prefab per line
        /// (SpiritBase.eSpiriteType: dog 1001, gold dog 1101, cat 2001 ...) and calls the spirit
        /// by the id its choosePet answer echoes (UI_SpriteShop_SuccessPanel ->
        /// Player.CallOnSpirit). A promoted id like 1002 matches no prefab, the lookup comes up
        /// empty and the spirit silently never appears - the growth belongs in the list's
        /// petLevel/petExp fields instead, which is what the client's shop shows.
        /// </summary>
        private static int LineId(int petId) => petId - petId % 100 + 1;

        /// <summary>The row this kind's shared experience has reached (level 1 at 0).</summary>
        private GameData.SpiritRow LevelRow(int kind, ulong experience) =>
            _s.Data.Spirits.Values
                .Where(s => Kind(s.Id) == kind && s.XpNeeded <= experience)
                .OrderByDescending(s => s.Level)
                .FirstOrDefault();

        /// <summary>
        /// Account files written while the server still promoted the rental itself (1001 ->
        /// 1002): fold the promoted row's experience into the shared kind counter and move the
        /// rental back to its line id, so nothing is lost.
        /// </summary>
        private void NormalizeRentals()
        {
            foreach (SpiritRental rental in Character.Spirits)
                if (rental.PetId != LineId(rental.PetId))
                {
                    int kind = Kind(rental.PetId);
                    if (_s.Data.Spirits.TryGetValue(rental.PetId, out GameData.SpiritRow promoted))
                        Character.SpiritExp[kind] = Math.Max(Character.SpiritExp.GetValueOrDefault(kind), promoted.XpNeeded);
                    rental.PetId = LineId(rental.PetId);
                }
        }

        public void SendList()
        {
            NormalizeRentals();
            ExpireRentals();
            var list = new vectorSPetInfo();
            foreach (SpiritRental rental in Character.Spirits)
            {
                int kind = Kind(rental.PetId);
                ulong experience = Character.SpiritExp.GetValueOrDefault(kind);
                list.Add(new SPetInfo
                {
                    petTypeID = rental.PetId,
                    petLevel = LevelRow(kind, experience)?.Level ?? 1,
                    petExp = experience,
                    buypetBegTime = (ulong)rental.BeginUnix,
                    leftTime = (int)Math.Max(0, rental.BeginUnix + rental.Seconds - Now), // what is left, not the whole rent
                });
            }
            _s.Send(GameResponses.RolePetListAck(list));
        }

        public void Rent(int petId, int rent)
        {
            int error = TryRent(petId, rent);
            if (error != EServerErrorType.eSuccess)
                _s.Log.LogInformation("   spirit {Pet} rejected ({Error})", petId, error);
            _s.Send(GameResponses.BuyPetAck(petId, rent, Proto.Error(error)));
        }

        private int TryRent(int petId, int rent)
        {
            NormalizeRentals();
            if (!_s.Data.Spirits.TryGetValue(petId, out GameData.SpiritRow spirit))
                return EServerErrorType.eUnknownError;
            if (Character.Level < spirit.LevelRequired)
                return EServerErrorType.eGS_LessLevel;

            ((int Price, bool Crystal) cost, int seconds) = rent switch
            {
                RentHour => (spirit.Hour, 3600),
                RentDay => (spirit.Day, 86400),
                RentWeek => (spirit.Week, 7 * 86400),
                _ => ((0, false), 0),
            };
            if (seconds == 0)
                return EServerErrorType.eUnknownError;
            // Crystals are the real-money currency and cost nothing here; karma is paid.
            if (!cost.Crystal)
            {
                if (_s.Account.Karma < cost.Price)
                    return EServerErrorType.eGS_LessMoney_karma;
                _s.Account.Karma -= cost.Price;
                _s.SendMoney();
            }

            Character.Spirits.RemoveAll(r => Kind(r.PetId) == Kind(petId));
            Character.Spirits.Add(new SpiritRental { PetId = LineId(petId), BeginUnix = Now, Seconds = seconds });
            _s.SaveAccount();
            _s.Log.LogInformation("   rented {Spirit} for {Seconds} s", spirit.Name, seconds);
            return EServerErrorType.eSuccess;
        }

        public void Choose(int petId)
        {
            NormalizeRentals();
            ExpireRentals();
            // The shop clicks the spirit row of the current level (1002 once the spirit grew);
            // the rental lives under the line id. The answer must carry the line id: the client
            // spawns the prefab that matches it, and only the level-1 id has one.
            SpiritRental rental = Character.Spirits.FirstOrDefault(r => Kind(r.PetId) == Kind(petId));
            if (rental == null)
            {
                _s.Send(GameResponses.choosePetAck(petId, Proto.Error(EServerErrorType.eUnknownError)));
                return;
            }
            foreach (SpiritRental other in Character.Spirits)
                other.Chosen = other == rental;
            _s.SaveAccount();
            _s.Send(GameResponses.choosePetAck(rental.PetId, Proto.Error(EServerErrorType.eSuccess)));
        }

        /// <summary>The spirit at the player's side, if any.</summary>
        public GameData.SpiritRow Active()
        {
            if (Character == null)
                return null;
            NormalizeRentals();
            SpiritRental rental = Character.Spirits.FirstOrDefault(r => r.Chosen && Now < r.BeginUnix + r.Seconds);
            return rental == null ? null : LevelRow(Kind(rental.PetId), Character.SpiritExp.GetValueOrDefault(Kind(rental.PetId)));
        }

        /// <summary>
        /// The chosen spirit shares the hunter's experience from monsters; enough of it brings the
        /// next level. The rental keeps its line id - only the shared experience counter and the
        /// petLevel the shop shows grow (see <see cref="LineId"/>).
        /// </summary>
        public void GainExperience(ulong xp)
        {
            if (Character == null || xp == 0)
                return;
            NormalizeRentals();
            SpiritRental rental = Character.Spirits.FirstOrDefault(r => r.Chosen && Now < r.BeginUnix + r.Seconds);
            if (rental == null)
                return;
            int kind = Kind(rental.PetId);
            ulong total = Character.SpiritExp[kind] = Character.SpiritExp.GetValueOrDefault(kind) + xp;

            int before = LevelRow(kind, total - xp)?.Level ?? 1;
            GameData.SpiritRow reached = LevelRow(kind, total);
            if (reached != null && reached.Level > before)
            {
                _s.Log.LogInformation("   spirit {Spirit} reached level {Level}", reached.Name, reached.Level);
                _s.SaveAccount();
                _s.Battle.SendAttributes(); // the bonus grew: the client's numbers change
            }
        }

        /// <summary>Sends the spirit home when its time is up; checked on requests and by the session tick.</summary>
        public void ExpireRentals()
        {
            if (Character == null)
                return;
            foreach (SpiritRental rental in Character.Spirits.Where(r => Now >= r.BeginUnix + r.Seconds).ToList())
            {
                Character.Spirits.Remove(rental);
                _s.SaveAccount();
                if (rental.Chosen)
                    _s.Send(GameResponses.PetRentTimeEndNTF(LineId(rental.PetId))); // the id of the prefab the client attached
            }
        }
    }
}
