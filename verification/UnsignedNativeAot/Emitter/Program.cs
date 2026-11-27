using Ores.Net.Compiler;

// Source -> existing lexer/precedence reader -> verified u64 SSA -> CIL.
if (args.Length != 2) throw new ArgumentException("usage: Emitter CASE OUTPUT");
var expression = args[0] switch
{
    "max" => "18446744073709551615-1+1",
    "divide" => "18446744073709551615/2",
    "multiply" => "4294967295*4294967297",
    "add-overflow" => "18446744073709551615+1-1",
    "sub-underflow" => "0-1",
    "mul-overflow" => "9223372036854775808*2*0",
    "divide-zero" => "18446744073709551615/0",
    _ => throw new ArgumentException("unknown case")
};
var checkedIr = OresParser.LowerUnsigned("pub fnc main():u64{return " + expression + ";}");
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
CilEmitter.EmitUnsignedLibrary(checkedIr, output);
