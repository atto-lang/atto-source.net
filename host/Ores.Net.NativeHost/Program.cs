using Ores.Net.Runtime;
using System.Globalization;

// Trusted command-line result transport, outside generated guest execution.
// RuntimeHost itself returns data and exposes no guest I/O binding.
var result = RuntimeHost.Evaluate(Oreslang.Generated.OresProgram.Run);
if (result.Value is long value) Console.WriteLine(value.ToString(CultureInfo.InvariantCulture));
else Console.Error.WriteLine(result.Error switch {
    ExecutionError.ArithmeticOverflow => "Oreslang arithmetic overflow",
    ExecutionError.DivisionByZero => "Oreslang division by zero",
    _ => "Oreslang execution failed"
});
return result.ExitCode;
