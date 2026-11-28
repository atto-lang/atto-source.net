// .NET 8/Native AOT-compatible HOST fingerprint test; no Oreslang guest assertion.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class LiveViewFingerprintVectors
{
    private static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    throw new ArgumentException("Unpaired high surrogate");
                sb.Append(c); sb.Append(value[++i]); continue;
            }
            if (char.IsLowSurrogate(c)) throw new ArgumentException("Unpaired low surrogate");
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
    private static string Decode(string encoded)
    {
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
    }
    private static string Digest(string user,string name,long rev,string canonical)
    {
        if (rev < 0 || rev > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(rev));
        string json = "[" + Quote(user) + "," + Quote(name) + "," + rev.ToString(CultureInfo.InvariantCulture) + "," + Quote(canonical) + "]";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: fingerprint-vectors path.tsv"); return 2; }
        int passed = 0;
        foreach (string row in File.ReadLines(args[0]))
        {
            if (row.Length == 0 || row.StartsWith('#')) continue;
            string[] f = row.Split('\t');
            if (f.Length != 6) throw new FormatException("Invalid vector row");
            long rev = long.Parse(f[3], CultureInfo.InvariantCulture);
            string actual = Digest(Decode(f[1]),Decode(f[2]),rev,Decode(f[4]));
            if (actual != f[5]) throw new Exception("Fingerprint mismatch: " + f[0]);
            passed++;
        }
        if (passed != 8) throw new Exception("Expected exactly eight vectors");
        try { Digest("alice","increment",9007199254740992L,"{}"); throw new Exception("Unsafe revision accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine($".NET HOST LiveView fingerprint: {passed}/8 vectors passed");
        return 0;
    }
}
