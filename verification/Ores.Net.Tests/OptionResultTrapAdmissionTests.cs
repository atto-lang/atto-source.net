using Ores.Net.Compiler;

internal static class OptionResultTrapAdmissionTests
{
    private const string Diagnostic =
        "Option/Result/trap requires verified tagged-sum CIL and NativeAOT guest-control lowering";

    // This corpus is the exact shared nine-case .atto acceptance set, not a
    // copied text interpretation. A rejected case is NOT a runtime parity pass.
    private static void VerifyCanonicalSourceIsStillFailClosed()
    {
        string[] names =
        [
            "01-some-int.atto", "02-some-result-ok.atto",
            "03-some-result-err.atto", "04-some-none.atto",
            "05-ordinary-throw.atto", "06-panic-bypass.atto",
            "07-exactly-once.atto", "08-type-mismatch.atto",
            "09-result-is-not-throw.atto"
        ];
        string[] blobs =
        [
            "d0ec8a9bc9e73e016cf1c31e2cb35cb84ac281c7",
            "8dd86350bdfa4dc90e47b7dd5f44bd82b0266157",
            "0f431f23d40d46cacd07d81714780e0061b6413e",
            "038d5bfe66996efff91400a8d699dac3e6f43f54",
            "57a646240880dfc4b633c2f9eeb5b9278dd0022f",
            "7b674493d50e688666b1fa37f59b990a27f109e4",
            "6547de51bc2be9b52eca35359a00b692ed434cfb",
            "057544faa452e4942143503499c4a7b76072ff17",
            "c5a427450943ac32824e42c1261401777080724e"
        ];
        for (int i = 0; i < names.Length; i++)
        {
            var raw = File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory, "caller-trap-parity", names[i]));
            byte[] header = System.Text.Encoding.ASCII.GetBytes(
                "blob " + raw.Length + "\0");
            using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA1);
            digest.AppendData(header);
            digest.AppendData(raw);
            string actual = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            if (actual != blobs[i])
                throw new Exception("Canonical Atto caller-trap guest was modified: " + names[i]);

            string source = System.Text.Encoding.UTF8.GetString(raw);
            try
            {
                _ = OresStructuredCfgLowering.LowerModule(source);
            }
            catch (FormatException)
            {
                // Current NativeAOT CIL does NOT support any full canonical
                // cases, including unwrap(), typed Result and ordinary throw.
                // This only confirms an unsupported feature fails closed.
                continue;
            }
            throw new Exception(
                "UNVERIFIED_PARITY: canonical Atto source reached CIL " +
                "without native guest semantics: " + names[i]);
        }
    }

    public static void Run()
    {
        VerifyCanonicalSourceIsStillFailClosed();

        // 12 genuine source spellings, none of which may be lowered as an
        // untyped scalar, or falsely counted as a semantic negative pass.
        string[] unimplemented =
        [
            "fnc main():Option<int>{return Some(7);}",
            "fnc main():Option<int>{return None;}",
            "fnc main():Result<int,String>{return Ok(7);}",
            "fnc main():Result<int,String>{return Err(\"bad\");}",
            "trap fnc main():int{return 7;}",
            "fnc main():int{return Some(7);}",
            "fnc main():int{return Ok(7);}",
            "fnc main():int{return Err(7);}",
            "fnc main():int{return None;}",
            "fnc main():int{return Option(7);}",
            "fnc main():int{return Result(7);}",
            "fnc main():int{return 7;} trap"
        ];
        foreach (var source in unimplemented)
        {
            try { _ = OresParser.Parse(source); }
            catch (FormatException error) when
                (error.Message.Contains(Diagnostic, StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Scalar CIL admitted or misclassified tagged source: " + source);
        }

        // Structured/linked CIL has a separately verified closed Option<int>
        // lane. Unsupported Result/Ok/Err/trap must retain the stable
        // refusal diagnostic instead of silently becoming scalar SSA.
        foreach (var source in new[]
        {
            "fnc main():int{return Ok(7);}",
            "fnc main():int{return Err(7);}",
            "fnc main():int{return Result(7);}",
            "fnc main():int{val Result<int,int> x=Ok(7);return 7;}",
            "fnc main():int{return trap true;}",
            "trap fnc main():int{return 7;}"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException error) when (
                error.Message.Contains(Diagnostic, StringComparison.Ordinal) &&
                error.Message.Contains("offset ", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Structured CIL admitted or misclassified unsupported tagged source: " + source);
        }

        // Exact .atto bytes are shared with the executable Java/Graal guest
        // and LLVM's native-source gate. The current structured/CIL .NET
        // backend must fail closed rather than accidentally promise parity.
        string sharedSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "trap-three-backend-target.atto"));
        try
        {
            _ = OresStructuredCfgLowering.LowerModule(sharedSource);
            throw new Exception("Unimplemented cross-backend trap guest reached .NET CFG/CIL");
        }
        catch (FormatException error) when
            (error.Message.Contains(Diagnostic, StringComparison.Ordinal) &&
             error.Message.Contains("offset ", StringComparison.Ordinal))
        {
            // Correct capability diagnostic, NOT an executable guest parity pass.
        }
        foreach (var source in new[]
        {
            "fnc main():int{return trap false;}",
            "fnc checked():bool{return true;} fnc main():int{return (trap checked()).is_some();}",
            "fnc main():int{val Option<int> outcome=trap Some(7);return 7;}",
            "fnc main():int{val Result<int,String> outcome=Err(\"ordinary\");return 7;}",
            "fnc main():int{val Option<Result<int,String>> outcome=trap checked();return 7;}"
        })
        {
            try
            {
                _ = OresStructuredCfgLowering.LowerModule(source);
            }
            catch (FormatException error) when
                (error.Message.Contains(Diagnostic, StringComparison.Ordinal) &&
                 error.Message.Contains("offset ", StringComparison.Ordinal))
            {
                continue;
            }
            throw new Exception("Unsupported native trap/Result reached verified SSA: " + source);
        }

        // Reconcile with the experimental closed Option<int> path, rather
        // than applying a broad token deny that would invalidate its typed
        // Option tag and payload SSA proof.
        var closedOption = OresStructuredParser.Parse(
            "pub fnc main():int{val Option<int> x=Some(42);return x.unwrap_or(0);}");
        if (closedOption.Body.Count == 0)
            throw new Exception("Structured CIL lost verified closed Option<int> admission");
        foreach (var source in new[]
        {
            "fnc main():int{return 7;} // Result Ok Err trap",
            "fnc main():int{return 7;} /* Result /* Ok Err */ trap */",
            "fnc main():int{return 7;} // ResultValue Okapi ErrorCode"
        })
        {
            if (OresStructuredParser.Parse(source).Body.Count != 1)
                throw new Exception("Structured lexer treated ADT source trivia as executable code");
        }
        try
        {
            _ = OresStructuredParser.Parse("fnc main():int{return ResultValue;}");
            throw new Exception("Unsupported scalar name was unexpectedly admitted");
        }
        catch (FormatException error) when (
            !error.Message.Contains(Diagnostic, StringComparison.Ordinal))
        {
            // Correct: a longer identifier is not a reserved ADT token.
        }

        // Import discovery lexes all source before resolving any file path.
        // Unsupported tagged tokens must be rejected in this entrypoint too;
        // the experimental closed Option<int> still must be accepted.
        string linkedRoot = Path.Combine(Path.GetTempPath(),
            "atto-linked-adt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(linkedRoot);
        string linkedEntrypoint = Path.Combine(linkedRoot, "main.atto");
        try
        {
            foreach (string unsupported in new[]
            {
                "fnc main():int { return Result(7); }",
                "fnc main():int { return Ok(7); }",
                "fnc main():int { return Err(7); }",
                "fnc main():int { return trap(7); }",
                "trap fnc main():int { return 7; }"
            })
            {
                File.WriteAllText(linkedEntrypoint, unsupported);
                try
                {
                    _ = OresSourceModuleCompiler.Compile(
                        linkedEntrypoint, new[] { linkedRoot });
                }
                catch (FormatException error) when (
                    error.Message.Contains(Diagnostic, StringComparison.Ordinal)
                    && error.Message.Contains("offset ", StringComparison.Ordinal))
                {
                    continue;
                }
                throw new Exception("Linked CIL admitted unsupported ADT syntax: "
                    + unsupported);
            }
            File.WriteAllText(linkedEntrypoint,
                "pub fnc main():int { val Option<int> x = Some(42); return x.unwrap_or(0); }");
            _ = OresSourceModuleCompiler.Compile(linkedEntrypoint, new[] { linkedRoot });
            File.WriteAllText(linkedEntrypoint,
                "pub fnc main():int { return 42; } /* Result /* trap */ Ok Err */");
            _ = OresSourceModuleCompiler.Compile(linkedEntrypoint, new[] { linkedRoot });

            // Canonical .atto import discovery, explicit legacy fallback, and
            // fail-closed ambiguity when both suffixes exist.
            File.WriteAllText(linkedEntrypoint,
                "import fnc answer from \"./answer\"; pub fnc main():int { return answer(); }");
            string canonicalAnswer = Path.Combine(linkedRoot, "answer.atto");
            string legacyAnswer = Path.Combine(linkedRoot, "answer.ores");
            const string answerSource = "pub fnc answer():int { return 42; }";
            File.WriteAllText(canonicalAnswer, answerSource);
            if (OresSourceModuleCompiler.Compile(linkedEntrypoint,
                new[] { linkedRoot }).Sources.Count != 2)
                throw new Exception("Canonical .atto import did not resolve two exact source units");
            File.WriteAllText(legacyAnswer, answerSource);
            try
            {
                _ = OresSourceModuleCompiler.Compile(linkedEntrypoint,
                    new[] { linkedRoot });
                throw new Exception("Ambiguous .atto/.ores import was silently resolved");
            }
            catch (FormatException error) when (
                error.Message.Contains("Ambiguous AttoLang source import", StringComparison.Ordinal))
            {
                // Correct: require an explicit extension, no implicit selection.
            }
            File.Delete(canonicalAnswer);
            if (!OperatingSystem.IsWindows())
            {
                // Dangling canonical aliases must not silently redirect to a
                // different legacy source identity.
                File.CreateSymbolicLink(canonicalAnswer,
                    Path.Combine(linkedRoot, "nonexistent-answer.atto"));
                try
                {
                    _ = OresSourceModuleCompiler.Compile(
                        linkedEntrypoint, new[] { linkedRoot });
                    throw new Exception("Dangling canonical alias bypassed import ambiguity gate");
                }
                catch (FormatException error) when (
                    error.Message.Contains("Ambiguous AttoLang source import", StringComparison.Ordinal))
                {
                    // Correct: presence is checked without following symlinks.
                }
                File.Delete(canonicalAnswer);
            }
            if (OresSourceModuleCompiler.Compile(linkedEntrypoint,
                new[] { linkedRoot }).Sources.Count != 2)
                throw new Exception("Legacy .ores import compatibility was lost");
        }
        finally
        {
            Directory.Delete(linkedRoot, recursive: true);
        }

        // The lexer must not treat words in source trivia as code.
        foreach (var source in new[]
        {
            "fnc main():int{return 7;} // Option Result Some None Ok Err trap",
            "fnc main():int{return 7;} /* Option Result Some None Ok Err trap */",
            "fnc main():int{return 7;} /* Option /* Result trap */ Some */",
            "fnc main():int{return 7;} // Result\r",
            "fnc main():int{return 7;} // trap\u2028"
        })
        {
            if (OresParser.Parse(source) is not Literal { Value: 7 })
                throw new Exception("Comment-only tagged keyword altered scalar source");
        }
        // Keywords in longer identifiers are distinct tokens, not matched
        // by substring. The ordinary scalar name validator still rejects them.
        try { _ = OresParser.Parse("fnc main():int{return OptionValue;}"); }
        catch (FormatException error) when
            (!error.Message.Contains(Diagnostic, StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Substring of Option was incorrectly treated as reserved ADT keyword");
    }
}
