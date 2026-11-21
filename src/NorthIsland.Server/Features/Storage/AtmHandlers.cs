using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Storage;

/// <summary>
/// Serves the ATM: a gold balance kept per character that players pay into, take out of, send to another online player, and fill by exchanging mall
/// points.
/// </summary>
/// <param name="world">The online sessions, used to find the receiver of a transfer.</param>
/// <param name="options">The mall options; they hold the gold paid per mall point.</param>
/// <param name="logger">The logger.</param>
public sealed class AtmHandlers(
    WorldState world,
    IOptions<MallOptions> options,
    ILogger<AtmHandlers> logger)
{
    /// <summary>Sends the ATM balance to a player who enters the world, when it is not zero.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The character that entered.</param>
    public static void SendOnLogin(GameSession session, Character character)
    {
        if (character.AtmGold > 0)
            session.Send(AtmMessages.Balance(character.AtmGold));
    }

    /// <summary>
    /// Opens the ATM window: records the form as open on the player's map (<see cref="OpenServices"/>), sends the balance, then the form.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="form">1 for the deposit form, anything else for the transfer form.</param>
    public void OpenForm(GameSession session, byte form)
    {
        var character = session.ActiveCharacter!;
        OpenServices.Open(session, character, form == 1 ? ServiceWindow.AtmDeposit : ServiceWindow.AtmTransfer); // form 1: deposit
        session.Send(AtmMessages.Balance(character.AtmGold));
        session.Send(AtmMessages.Form(form));
        logger.LogInformation(
            "[{Session}] {Name}: ATM {Form} form (balance {Balance})",
            session.Id,
            character.Name,
            form == 1 ? "deposit" : "transfer",
            character.AtmGold);
    }

    /// <summary>
    /// Opens the point exchange form: records it as open on the player's map (<see cref="OpenServices"/>), then sends the account's mall points
    /// and the gold paid for one point.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void OpenExchange(GameSession session)
    {
        if (session.ActiveCharacter is { } character)
            OpenServices.Open(session, character, ServiceWindow.PointExchange);
        session.Send(MallMessages.Points(session.Account!.MallPoints));
        session.Send(AtmMessages.ExchangeRate(options.Value.PointExchangeRate));
        logger.LogInformation("[{Session}] point exchange form ({Rate} gold a point)", session.Id, options.Value.PointExchangeRate);
    }

    /// <summary>
    /// Moves carried gold into the ATM balance (<c>29/3</c>). Refused when the amount is not positive or more than they carry; a balance over the
    /// limit is answered with the full notice.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 gold amount.</param>
    internal ValueTask OnDeposit(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, body, 4) is not { } character || !AtAtm(session, character, "deposit", ServiceWindow.AtmDeposit))
            return ValueTask.CompletedTask;
        var amount = new PacketReader(body.Span).I32();
        if (amount <= 0 || amount > character.Gold)
        {
            logger.LogWarning("[{Session}] {Name}: ATM deposit {Amount} refused (carried {Gold})", session.Id, character.Name, amount, character.Gold);
            return ValueTask.CompletedTask;
        }
        if ((long)character.AtmGold + amount > AtmMessages.MaxBalance)
        {
            session.Send(AtmMessages.Full());
            return ValueTask.CompletedTask;
        }
        character.Gold -= amount;
        character.AtmGold += amount;
        session.Send(PlayerMessages.MoneyChange(-amount));
        session.Send(AtmMessages.Added(amount));
        logger.LogInformation(
            "[{Session}] {Name} puts {Amount} gold in the ATM (ATM {Atm}, carried {Gold})",
            session.Id,
            character.Name,
            amount,
            character.AtmGold,
            character.Gold);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Moves gold from the ATM balance to the purse (<c>29/4</c>). Refused when the amount is not positive, more than the balance, or would take the
    /// purse over the gold limit.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 gold amount.</param>
    internal ValueTask OnWithdraw(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, body, 4) is not { } character || !AtAtm(session, character, "withdraw", ServiceWindow.AtmDeposit))
            return ValueTask.CompletedTask;
        var amount = new PacketReader(body.Span).I32();
        if (amount <= 0 || amount > character.AtmGold || (long)character.Gold + amount > BankHandlers.MaxGold)
        {
            logger.LogWarning(
                "[{Session}] {Name}: ATM withdraw {Amount} refused (ATM {Atm}, carried {Gold})",
                session.Id,
                character.Name,
                amount,
                character.AtmGold,
                character.Gold);
            return ValueTask.CompletedTask;
        }
        character.AtmGold -= amount;
        character.Gold += amount;
        session.Send(AtmMessages.Subtracted(amount));
        session.Send(PlayerMessages.MoneyChange(amount));
        logger.LogInformation(
            "[{Session}] {Name} takes {Amount} gold from the ATM (ATM {Atm}, carried {Gold})",
            session.Id,
            character.Name,
            amount,
            character.AtmGold,
            character.Gold);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sends gold from the player's ATM balance to the ATM balance of another online player (<c>29/5</c>), under the receiver's session gate.
    /// Refused for an offline or own target, an amount over the balance, a full receiver, or a receiver whose session is busy.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 login id of the receiver, i32 gold amount.</param>
    internal ValueTask OnTransfer(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, body, 8) is not { } character || !AtAtm(session, character, "transfer", ServiceWindow.AtmTransfer))
            return ValueTask.CompletedTask;
        var reader = new PacketReader(body.Span);
        var targetId = reader.I32();
        var amount = reader.I32();
        var senderId = session.Account!.LoginId;
        if (amount <= 0 || amount > character.AtmGold || targetId == senderId
            || world.SessionOf(targetId) is not { } target || target.ActiveCharacter is not { } receiver)
        {
            logger.LogWarning(
                "[{Session}] {Name}: ATM transfer {Amount} to {Target} refused (ATM {Atm})",
                session.Id,
                character.Name,
                amount,
                targetId,
                character.AtmGold);
            return ValueTask.CompletedTask;
        }
        if (!target.TryExclusiveFrom(session, out var held)) // the receiver's own packet may be changing their ATM balance
        {
            logger.LogWarning("[{Session}] {Name}: ATM transfer {Amount} to {Target} refused: the receiver's session is busy", session.Id,
                character.Name, amount, targetId);
            return ValueTask.CompletedTask;
        }
        using (held)
        {
            if (!ReferenceEquals(target.ActiveCharacter, receiver)) // the receiver may have left the world before their gate was free
            {
                logger.LogWarning("[{Session}] {Name}: ATM transfer {Amount} to {Target} refused: the receiver left", session.Id, character.Name,
                    amount, targetId);
                return ValueTask.CompletedTask;
            }
            if ((long)receiver.AtmGold + amount > AtmMessages.MaxBalance)
            {
                session.Send(AtmMessages.Full());
                return ValueTask.CompletedTask;
            }
            receiver.AtmGold += amount;
            character.AtmGold -= amount;
            session.Send(AtmMessages.Subtracted(amount, targetId));
            target.Send(AtmMessages.Added(amount, senderId));
            logger.LogInformation("[{Session}] {Name} transfers {Amount} gold from the ATM to {Target} (ATM {Atm}, theirs {Theirs})", session.Id,
                character.Name, amount, receiver.Name, character.AtmGold, receiver.AtmGold);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Exchanges mall points for carried gold at the configured rate (<c>23/109</c>). Refused when they lack the points or the gold would pass the
    /// purse limit.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: i32 number of points.</param>
    internal ValueTask OnPointExchange(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (CharacterOf(session, body, 4) is not { } character)
            return ValueTask.CompletedTask;
        var account = session.Account!;
        if (!AtAtm(session, character, "point exchange", ServiceWindow.PointExchange))
        {
            session.Send(MallMessages.Points(account.MallPoints));
            return ValueTask.CompletedTask;
        }
        var points = new PacketReader(body.Span).I32();
        var gold = (long)points * options.Value.PointExchangeRate;
        if (points <= 0 || points > account.MallPoints || character.Gold + gold > BankHandlers.MaxGold)
        {
            logger.LogWarning(
                "[{Session}] {Name}: exchange of {Points} points refused ({Have} points, carried {Gold})",
                session.Id,
                character.Name,
                points,
                account.MallPoints,
                character.Gold);
            session.Send(MallMessages.Points(account.MallPoints));
            return ValueTask.CompletedTask;
        }
        account.MallPoints -= points;
        character.Gold += (int)gold;
        session.Send(AtmMessages.ExchangeGold(character.Gold, (int)gold));
        session.Send(MallMessages.Points(account.MallPoints));
        logger.LogInformation(
            "[{Session}] {Name} exchanges {Points} IM points for {Gold} gold ({Left} points left)",
            session.Id,
            character.Name,
            points,
            gold,
            account.MallPoints);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whether the ATM form of a request is open: an NPC event opened it on the player's map and they are not in a battle
    /// (<see cref="OpenServices"/>). The deposit form takes deposits and withdrawals, the transfer form transfers. A refusal is logged.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's active character.</param>
    /// <param name="request">The name of the request, for the log.</param>
    /// <param name="window">The form the request belongs to.</param>
    /// <returns>True when the request may be served.</returns>
    private bool AtAtm(GameSession session, Character character, string request, ServiceWindow window)
    {
        if (OpenServices.IsOpen(session, character, window))
            return true;
        logger.LogWarning(
            "[{Session}] {Name}: ATM {Request} refused, no {Window} open (map {Map}, window map {WindowMap}, in battle {Battle})",
            session.Id,
            character.Name,
            request,
            window,
            character.MapId,
            OpenServices.MapOf(session, window),
            session.Battle is not null);
        return false;
    }

    /// <summary>Returns the active character when the session is in the world and the packet body is long enough.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body.</param>
    /// <param name="length">The least number of bytes the body must have.</param>
    /// <returns>The active character, or null when the request must be ignored.</returns>
    private static Character? CharacterOf(GameSession session, ReadOnlyMemory<byte> body, int length)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && body.Length >= length ? character : null;
    }
}
