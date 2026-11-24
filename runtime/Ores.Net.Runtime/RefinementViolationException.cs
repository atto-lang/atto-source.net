namespace Ores.Net.Runtime;

/// <summary>A checked cast received a value outside its target refinement.</summary>
public sealed class RefinementViolationException() : Exception("Oreslang refinement check failed");
