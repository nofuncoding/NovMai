using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

namespace NovMai.Native;

/// <summary>
/// Resolves the cimai parser library from locations that work in the editor
/// and in an exported package.
/// </summary>
internal static class NativeLibraryResolver
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

        // The cimai P/Invoke declarations live in the Cimai package assembly,
        // not in this assembly: without a resolver for it, .NET falls back to
        // the NuGet RID asset, which may be built for a newer CPU baseline
        // (e.g. AVX2) than the machine running the game. Register the same
        // resolver so a locally built libcimai.so (just cimai) wins.
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Cimai.SimaiFile).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Another resolver was already registered for the cimai assembly.
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "libcimai" or "cimai")
        {
            return ResolveFile(CimaiFileName());
        }
        return IntPtr.Zero;
    }

    private static IntPtr ResolveFile(string fileName)
    {
        foreach (var candidate in Candidates(fileName))
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(string fileName)
    {
        // Prefer libraries built on/for this machine. The NuGet publish output
        // also drops a libcimai.so into the .NET data directory, but that binary
        // may be built for a newer CPU baseline than the host (e.g. AVX2), so it
        // must stay the last resort.
        yield return Path.Combine(ProjectSettings.GlobalizePath("res://.build/cimai"), fileName);
        yield return Path.Combine(ProjectSettings.GlobalizePath("res://native/vendor/cimai/bindings/CSharp/Cimai/runtimes/linux-x64/native"), fileName);
        var executableDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (!string.IsNullOrEmpty(executableDir))
        {
            yield return Path.Combine(executableDir, fileName);
            yield return Path.Combine(executableDir, "native", fileName);
            yield return Path.Combine(executableDir, "assets", "native", fileName);
        }
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string CimaiFileName() => OS.GetName() switch
    {
        "Windows" => "libcimai.dll",
        "macOS" => "libcimai.dylib",
        _ => "libcimai.so",
    };
}
