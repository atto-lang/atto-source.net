using Ores.Net.Compiler;

if (args.Length == 0)
{
    StrictI64ProofTests.Run();
    return 0;
}
if (args is ["emit", var input, var output])
{
    if (File.Exists(output)) throw new InvalidOperationException("refusing existing artifact");
    try
    {
        var verified = OresParser.LowerVerifiedStrict(File.ReadAllText(input));
        CilEmitter.EmitLibrary(verified, output);
        return 0;
    }
    catch (FormatException error)
    {
        if (File.Exists(output)) throw new InvalidOperationException("rejected source emitted artifact",error);
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}
Console.Error.WriteLine("usage: StrictI64Proof [emit <source.atto> <new output.dll>]");
return 2;
