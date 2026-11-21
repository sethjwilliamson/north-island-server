using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Events;

namespace NorthIsland.Server.Features.Events;

/// <summary>One step of the group a run is in: a result record still to run, or the display of a result whose change is made already.</summary>
/// <param name="Record">The result record.</param>
/// <param name="Shown">The record to send for a result already applied; null when the record itself still runs.</param>
public readonly record struct EventStep(EveScriptRecord Record, EventResult? Shown = null);
