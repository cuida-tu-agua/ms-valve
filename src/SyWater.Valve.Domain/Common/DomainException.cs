namespace SyWater.Valve.Domain.Common;

/// <summary>Base of every business error. Code goes in the "title" of the Problem Details answer.</summary>
public abstract class DomainException(string code, string message) : Exception(message)
{
    /// <summary>Stable, machine-readable code (e.g. "valve.command_in_progress").</summary>
    public string Code { get; } = code;
}
