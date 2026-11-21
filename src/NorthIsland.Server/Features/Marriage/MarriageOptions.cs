namespace NorthIsland.Server.Features.Marriage;

/// <summary>Settings of the marriage feature.</summary>
public sealed class MarriageOptions
{
    /// <summary>Milliseconds between the steps of the wedding ceremony.</summary>
    public int WeddingStepMs { get; set; } = 3000;

    /// <summary>Whether the wedding requires both partners to wear the wedding clothes and ring.</summary>
    public bool WeddingClothesCheck { get; set; } = true;

    /// <summary>Seconds both partners have to say yes to a marriage application or divorce form before it is dropped.</summary>
    public int AnswerSeconds { get; set; } = 120;
}
