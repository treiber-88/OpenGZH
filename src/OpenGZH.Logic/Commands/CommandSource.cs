namespace OpenGZH.Logic.Commands;

/// <summary>Who gave an order. Some behaviour depends on it (e.g. AutoChooseSources, retaliation).</summary>
public enum CommandSource
{
    /// <summary>A human player clicked.</summary>
    Player,
    /// <summary>A mission script.</summary>
    Script,
    /// <summary>The computer player or the unit's own automatic behaviour.</summary>
    AI,
    /// <summary>Internal: dozers and workers repairing or building.</summary>
    Dozer,
}
