namespace Pqax.Collector;

/// <summary>
/// An action of the Delta log, as a commit or a checkpoint holds it. Only the fields that the collector uses are
/// modeled; the derived classes are the kinds of action.
/// </summary>
public abstract class DeltaAction
{
    /// <summary>Only the action classes of this library derive from this class.</summary>
    internal DeltaAction()
    {
    }
}
