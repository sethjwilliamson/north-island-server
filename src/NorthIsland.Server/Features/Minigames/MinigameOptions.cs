namespace NorthIsland.Server.Features.Minigames;

/// <summary>Settings of the Q&amp;A and Golden Compass games.</summary>
public sealed class MinigameOptions
{
    /// <summary>The number of questions in one game.</summary>
    public int QuizQuestions { get; set; } = 20;

    /// <summary>The seconds players have to answer a question.</summary>
    public int QuizAnswerSeconds { get; set; } = 6;

    /// <summary>The seconds between the end of a question and the next one.</summary>
    public int QuizGapSeconds { get; set; } = 3;

    /// <summary>The lives a Q&amp;A player starts with; a wrong or missing answer costs one.</summary>
    public int QuizLives { get; set; } = 5;

    /// <summary>
    /// The seconds the Q&amp;A waits after its first player joins before it asks the first question; players can join only in this time.
    /// </summary>
    public int QuizStartSeconds { get; set; } = 15;

    /// <summary>The percent chance that a question is a generated sum instead of one from the question bank.</summary>
    public int QuizMathPercent { get; set; } = 40;

    /// <summary>The lives a Golden Compass player starts with, and the most they can have.</summary>
    public int CompassLives { get; set; } = 3;

    /// <summary>The right answers in a row that give a Golden Compass player a life back; 0 or less turns it off.</summary>
    public int CompassStreakForLife { get; set; } = 5;

    /// <summary>The seconds the Golden Compass waits after its first player joins before it asks the first question.</summary>
    public int CompassStartSeconds { get; set; } = 15;
}
