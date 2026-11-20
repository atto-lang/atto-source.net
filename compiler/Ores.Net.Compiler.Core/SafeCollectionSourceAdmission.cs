namespace Ores.Net.Compiler;

/// <summary>
/// Admission guard for collection indexing in the existing i64-only .NET compilers.
/// This is deliberately not guest Option, bounds, or Rust-style borrow-checking
/// parity: those require typed array layout/ownership and verified Native AOT CIL.
/// Checking lexed punctuation prevents accidental unchecked indexing from
/// reaching future structured lowering without an explicit proof gate.
/// </summary>
internal static class SafeCollectionSourceAdmission
{
    internal const string Diagnostic = "E_SAFE_COLLECTION_ACCESS_UNSUPPORTED";

    internal static void RejectUnverifiedBracket(char punctuation, int offset)
    {
        if (punctuation is '[' or ']')
            throw new FormatException(
                $"{Diagnostic}: array/collection indexing and literals require checked bounds, " +
                $"Option<T> results, actor-owned ref32 validation and exclusive-borrow lowering " +
                $"before .NET CIL admission at offset {offset}");
    }
}
