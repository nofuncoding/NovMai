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
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(ProjectSettings.GlobalizePath("res://.build/cimai"), fileName);
        yield return Path.Combine(ProjectSettings.GlobalizePath("res://native/vendor/cimai/bindings/CSharp/Cimai/runtimes/linux-x64/native"), fileName);
        var executableDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (!string.IsNullOrEmpty(executableDir))
        {
            yield return Path.Combine(executableDir, fileName);
            yield return Path.Combine(executableDir, "native", fileName);
            yield return Path.Combine(executableDir, "assets", "native", fileName);
        }
    }

    private static string CimaiFileName() => OS.GetName() switch
    {
        "Windows" => "libcimai.dll",
        "macOS" => "libcimai.dylib",
        _ => "libcimai.so",
    };
}
