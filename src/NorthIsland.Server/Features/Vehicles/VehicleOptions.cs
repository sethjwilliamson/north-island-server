namespace NorthIsland.Server.Features.Vehicles;

/// <summary>Numbers for vehicle fuel, wear and fees.</summary>
public sealed class VehicleOptions
{
    /// <summary>The percent of a vehicle's full tank one fuel item adds (the full tank is the client's fuel bar, as big as the most wear).</summary>
    public int FuelPercentPerItem { get; set; } = 10;

    /// <summary>
    /// What riding a vehicle costs per second while the rider moves; 0 means riding costs nothing. It is taken from the fuel first and from the
    /// vehicle's health (its wear) once the fuel is gone (IGG's Vehicle Introduction: the health does not drop while the fuel bar is shown; the
    /// client counts fuel plus health left as one store, WS_Menu.pas:1035). GUESS: the original rate is not in the client or the data.
    /// </summary>
    public double DrainPerSecond { get; set; } = 30;

    /// <summary>How often, in seconds, the ridden vehicles are drained and the client is sent their wear and fuel.</summary>
    public int DrainTickSeconds { get; set; } = 5;

    /// <summary>The mall points a parking deposit costs; 0 means free.</summary>
    public int ParkPoints { get; set; } = 1;

    /// <summary>The mall points a repair costs per rank of the vehicle.</summary>
    public double RepairPointsPerRank { get; set; } = 1;

    /// <summary>The fuel a transmit trip takes from the ridden vehicle; 0 means a trip costs nothing.</summary>
    public int TransmitFuel { get; set; }
}
