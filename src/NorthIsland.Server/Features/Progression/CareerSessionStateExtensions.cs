using System.Diagnostics.CodeAnalysis;

namespace NorthIsland.Server.Features.Progression;

/// <summary>Adds the career form state to a game session.</summary>
public static class CareerSessionStateExtensions
{
    /// <summary>Extension members of a game session.</summary>
    /// <param name="session">The player's session.</param>
    extension(
        [NotNull] GameSession session)
    {
        /// <summary>The career form state of the session.</summary>
        public CareerSessionState CareerForm => session.Feature<CareerSessionState>();
    }
}
