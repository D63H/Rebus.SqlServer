namespace Rebus.Config.Outbox;

/// <summary>
/// Outbox related configuration
/// </summary>
public class OutboxOptions
{
    /// <summary>
    /// Whether or not to periodically delete sent records from the Outbox table.
    /// </summary>
    public bool EnableOutboxCleaner { get; internal set; }

    /// <summary>
    /// The period of time between each cleaner execution.
    /// </summary>
    public int CleanerIntervalInSeconds { get; internal set; }
}