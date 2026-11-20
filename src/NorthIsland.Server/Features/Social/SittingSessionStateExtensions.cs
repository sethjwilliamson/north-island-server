using System.Diagnostics.CodeAnalysis;

namespace NorthIsland.Server.Features.Social;

/// <summary>Session accessor for the sitting state.</summary>
public static class SittingSessionStateExtensions
{
    /// <summary>Extension members of a game session.</summary>
    /// <param name="session">The session.</param>
    extension(
        [NotNull] GameSession session)
    {
        /// <summary>The sitting state of the session.</summary>
        public SittingSessionState Sitting => session.Feature<SittingSessionState>();
    }
}
