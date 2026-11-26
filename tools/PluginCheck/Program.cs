using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Compiles every plugin .cs file on its own, the way Oxide does, and reports errors.
//
//   Full check (real Rust + Oxide API):
//     dotnet run --project tools/PluginCheck -- <repo-root> --refs <RustDedicated_Data/Managed>
//
//   Partial check (no game DLLs): C# syntax plus .NET and Newtonsoft usage only.
//     dotnet run --project tools/PluginCheck -- <repo-root>
internal static class Program
{
    // Without the game DLLs, Rust/Oxide/Unity names can't resolve. These errors are expected in a partial check.
    private static readonly HashSet<string> MissingReferenceCodes = new HashSet<string>
    {
        "CS0246", // type or namespace not found
        "CS0234", // namespace member not found
        "CS0012", // type defined in an unreferenced assembly
        "CS0103", // name not found (base-class members such as timer, Puts, permission)
        "CS0115", // override with no base method (base class is RustPlugin)
    };

    // In a partial check these are usually knock-on effects of a missing game type
    // (e.g. `list.Count` on a list of an unresolved type, or `npc != null` on one).
    // They're listed for a manual look but don't fail the check.
    private static readonly HashSet<string> UncertainCodes = new HashSet<string>
    {
        "CS0019", // operator can't be applied
        "CS0165", // use of unassigned local
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            Console.WriteLine("Usage: PluginCheck <repo-root> [--refs <RustDedicated_Data/Managed>] [--exclude a.dll,b.dll] [--lang <C# version, e.g. 9>]");
            return 2;
        }

        string root = Path.GetFullPath(args[0]);
        string refsDir = null;
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LanguageVersion lang = LanguageVersion.CSharp9;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--refs" && i + 1 < args.Length)
                refsDir = Path.GetFullPath(args[++i]);
            else if (args[i] == "--exclude" && i + 1 < args.Length)
            {
                foreach (string name in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    exclude.Add(name);
            }
            else if (args[i] == "--lang" && i + 1 < args.Length)
            {
                if (!LanguageVersionFacts.TryParse(args[++i], out lang))
                {
                    Console.Error.WriteLine($"Unknown C# version '{args[i]}'.");
                    return 2;
                }
            }
            else
            {
                Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                return 2;
            }
        }

        bool partial = refsDir == null;
        // Oxide ships its own Json.NET inside Oxide.References.dll, and plugins compile against that.
        // Rust's separate Newtonsoft.Json.dll would make every JSON type ambiguous, so leave it out.
        if (!partial && File.Exists(Path.Combine(refsDir, "Oxide.References.dll")) && exclude.Add("Newtonsoft.Json.dll"))
            Console.WriteLine("Note: using Json.NET from Oxide.References.dll (Newtonsoft.Json.dll excluded).");

        List<MetadataReference> references = partial ? DefaultReferences() : LoadReferences(refsDir, exclude);
        if (references.Count == 0)
        {
            Console.Error.WriteLine($"No .NET assemblies found in {refsDir}");
            return 2;
        }

        List<string> files = PluginFiles(root);
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No plugin .cs files found under {root}");
            return 2;
        }

        Console.WriteLine(partial
            ? "Partial check: C# syntax plus .NET/Newtonsoft usage. Rust, Oxide and Unity names are NOT checked."
            : $"Full check against {references.Count} assemblies in {refsDir}");
        Console.WriteLine($"C# language version: {lang.ToDisplayString()}");
        Console.WriteLine();

        var parseOptions = new CSharpParseOptions(lang);
        var compileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        int failed = 0;

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, file);
            CSharpCompilation compilation = CSharpCompilation.Create(
                Path.GetFileNameWithoutExtension(file), new[] { tree }, references, compileOptions);

            List<Diagnostic> errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            List<Diagnostic> uncertain = new List<Diagnostic>();
            List<Diagnostic> shown = errors;
            if (partial)
            {
                uncertain = errors.Where(d => UncertainCodes.Contains(d.Id)).ToList();
                shown = errors.Where(d => !MissingReferenceCodes.Contains(d.Id) && !UncertainCodes.Contains(d.Id)).ToList();
            }
            int skipped = errors.Count - shown.Count - uncertain.Count;

            if (shown.Count == 0)
            {
                Console.WriteLine(partial
                    ? $"OK    {relative}  ({skipped} expected missing-reference errors skipped)"
                    : $"OK    {relative}");
                foreach (Diagnostic d in uncertain)
                {
                    FileLinePositionSpan span = d.Location.GetLineSpan();
                    Console.WriteLine($"      check by hand, line {span.StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}");
                }
                continue;
            }

            failed++;
            Console.WriteLine($"FAIL  {relative}: {shown.Count} error(s)");
            foreach (Diagnostic d in shown)
            {
                FileLinePositionSpan span = d.Location.GetLineSpan();
                Console.WriteLine($"      line {span.StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? $"All {files.Count} plugin(s) passed." : $"{failed} of {files.Count} plugin(s) failed.");
        return failed == 0 ? 0 : 1;
    }

    // Plugin files are every .cs in the repo except this tool and build output.
    private static List<string> PluginFiles(string root)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                string rel = sep + Path.GetRelativePath(root, f);
                return !rel.Contains(sep + "tools" + sep) && !rel.Contains(sep + "bin" + sep)
                       && !rel.Contains(sep + "obj" + sep) && !rel.Contains(sep + ".git" + sep);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MetadataReference> DefaultReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        paths.Add(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location);
        return paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    // Every managed DLL in the folder; native DLLs are skipped.
    private static List<MetadataReference> LoadReferences(string dir, HashSet<string> exclude)
    {
        var references = new List<MetadataReference>();
        if (!Directory.Exists(dir))
            return references;

        foreach (string path in Directory.GetFiles(dir, "*.dll"))
        {
            if (exclude.Contains(Path.GetFileName(path)))
                continue;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var pe = new PEReader(stream))
                {
                    if (!pe.HasMetadata)
                        continue;
                }
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (BadImageFormatException)
            {
                // native library, not a reference
            }
        }
        return references;
    }
}
