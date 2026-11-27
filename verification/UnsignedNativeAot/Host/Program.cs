using System.Globalization;

// Direct static call into emitted CIL; no dynamic code, reflection or fallback.
try
{
    Console.WriteLine(Oreslang.Generated.OresProgram.RunU64().ToString(CultureInfo.InvariantCulture));
    return 0;
}
catch (OverflowException) { Console.Error.WriteLine("integer_overflow"); return 11; }
catch (DivideByZeroException) { Console.Error.WriteLine("division_by_zero"); return 12; }
