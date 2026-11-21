using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Actions;

/// <summary>Registers the routes of the packets the client sends with action 63: login and character select.</summary>
/// <param name="login">The <c>LoginHandlers</c> that serve the packets of this action.</param>
public sealed class Action63Account(
    LoginHandlers login) : IAction
{
    public byte Code => Action63.Code;

    public void Register(ActionRoutes routes) => routes
        .On(Action63.BackToLogin, "back to login", login.OnBackToLogin) // 63/0: go back to the login stage and forget the account
        .On(Action63.NumericLogin, "numeric login", login.OnNumericLogin) // 63/1: log in with a prefix and a number
        .On(Action63.SelectSlot, "select slot", login.OnSelectSlot) // 63/2: pick a character slot, or start creating in an empty one
        .On(Action63.CancelCreation, "cancel creation", login.OnCancelCreation) // 63/3: cancel character creation and return to slot select
        .On(Action63.AccountLogin, "account login", login.OnAccountLogin); // 63/4: log in with account name and password
}
