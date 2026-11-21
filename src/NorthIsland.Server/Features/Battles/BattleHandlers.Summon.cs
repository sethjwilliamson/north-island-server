using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>
    /// Calls a pet into the battle the player is in (C 19/1 during a fight): the pet joins in the player's column and the player's
    /// turn of this round is used up, as the client marks the player's command done when it sends the call. A pet that fell in this
    /// battle cannot be called into it again; only a revive brings it back.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="pet">The pet called.</param>
    /// <returns>True when the pet joined the battle.</returns>
    internal bool TrySummonPet(GameSession session, Character character, Pet pet)
    {
        if (session.Battle is not { } battle)
            return false;
        Fighter fighter;
        Dictionary<Fighter, BattleCommand>? commands = null;
        lock (battle.Lock)
        {
            var player = battle.Players.Find(p => ReferenceEquals(p.Session, session) && !p.Left);
            string? refusal = battle.Phase != BattlePhase.Command ? "not the command phase"
                : player is not { IsAlive: true } ? "the player is down"
                    : battle.Commands.ContainsKey(player) || player.JoinedRound == battle.Round || player.SummonedRound == battle.Round ? "the player already acted this round"
                        : battle.Pets.Exists(p => p.OwnerId == player.Id && !p.Left && p.IsAlive) ? "a pet is already fighting"
                            : battle.PetFell(player, pet) ? "the pet fell in this battle"
                                : null;
            if (refusal is not null || player is null)
            {
                logger.LogInformation("[{Session}] {Name}: pet {Pet} cannot be called into the battle ({Why})", session.Id, character.Name, pet.Name,
                    refusal);
                return false;
            }
            battle.RetireFallenPet(player); // a pet that fell earlier in this battle leaves the field; its fall is settled when the player leaves
            var petRow = player.Row == 1 ? (byte)2 : Battle.PetRow;
            fighter = battle.AddPet(pet, player, session, petRow, id => npcs[id], id => items[id]);
            fighter.JoinedRound = battle.Round; // it acts from the next round
            player.SummonedRound = battle.Round; // the call was the player's action this round
            player.Acted = true;
            character.ActivePetId = pet.NpcId;
            if (!battle.AwaitsCommands)
            {
                battle.Phase = BattlePhase.Acting;
                commands = new(battle.Commands);
            }
        }
        session.Send(PetMessages.Active(pet.NpcId));
        battle.Send(client => BattleMessages.Join([fighter.ToRecord(own: false)], client));
        if (pet.Equipment.Find(e => e.Slot == Gems.WeaponSlot) is { } weapon)
            battle.Send(BattleMessages.EquipLook([(fighter.Id, fighter.OwnerId, fighter.Row, fighter.Col, weapon.ItemId)]));
        logger.LogInformation("[{Session}] {Name} calls {Pet} into the battle at ({Row},{Col})", session.Id, character.Name, pet.Name, fighter.Row,
            fighter.Col);
        if (commands is not null)
            _ = PlayRoundAsync(session, battle, commands);
        return true;
    }
}
