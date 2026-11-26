# Plugin test kit

`PluginCheck` compiles every plugin `.cs` file on its own, the same way Oxide does, and reports compile errors before anything touches a live server.

Needs the .NET 8 SDK (`dotnet --version`).

## Full check (recommended)

This compiles against the real Rust and Oxide API, so it catches wrong method names, wrong prefab types and missing members.

1. On a server with Oxide installed, take the folder `RustDedicated_Data/Managed`. It holds `Assembly-CSharp.dll`, `UnityEngine*.dll`, `Facepunch*.dll`, `Rust*.dll` and `Oxide*.dll`.
2. Run, from the repo root:

```bash
dotnet run --project tools/PluginCheck -c Release -- . --refs /path/to/RustDedicated_Data/Managed
```

The exit code is 0 when every plugin compiles and 1 when any fail.

## Partial check (no game files)

```bash
dotnet run --project tools/PluginCheck -c Release -- .
```

This only checks C# syntax and use of .NET and Newtonsoft. Rust, Oxide and Unity names are skipped because their DLLs aren't present. Lines marked "check by hand" are usually side effects of those missing types, not real errors.

## Options

- `--lang 9` sets the C# version (default 9). Raise it if your Oxide build supports newer C#.

## What it doesn't do

It doesn't run the plugins. Hooks, prefab paths, NPC behaviour and map markers still need a test on a real server. A good order is a full check here, then `lpi install` on a test server.
