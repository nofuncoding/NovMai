using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace NovMai.Rendering;

/// <summary>
/// Locates and loads the FFmpeg 9 shared libraries that FFmpeg.AutoGen 9 needs.
///
/// FFmpeg.AutoGen 9 resolves natives itself (no [DllImport]): it combines
/// <see cref="ffmpeg.RootPath"/> with a per-library file name
/// (<c>libavcodec.so.63</c> on Linux, <c>avcodec-63.dll</c> on Windows,
/// <c>libavcodec.63.dylib</c> on macOS) and then dlopen/LoadLibrary's it.
/// The versions are pinned by FFmpeg.AutoGen's <c>LibraryVersionMap</c>:
/// avcodec 63, avformat 63, avutil 61, swscale 10, swresample 7.
///
/// Search order:
/// 1. <c>NOVMAI_FFMPEG_ROOT</c> (and the usual sibling subdirectories);
/// 2. caller supplied directories (Godot executable dir, project dir, ...);
/// 3. the directory of the managed assembly;
/// 4. the operating system's loader search path (RootPath = "").
/// </summary>
public static class FFmpegNative
{
    public const string RootEnvironmentVariable = "NOVMAI_FFMPEG_ROOT";

    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static string _rootPath = string.Empty;
    private static string _version = string.Empty;
    private static string _problem = "FFmpeg has not been initialized yet.";

    /// <summary>True once a set of FFmpeg shared libraries has been loaded successfully.</summary>
    public static bool IsAvailable
    {
        get { lock (SyncRoot) { return _initialized; } }
    }

    /// <summary>Directory passed to FFmpeg.AutoGen's loader ("" means the OS search path).</summary>
    public static string RootPath
    {
        get { lock (SyncRoot) { return _rootPath; } }
    }

    /// <summary>libavutil version string reported by the loaded libraries.</summary>
    public static string Version
    {
        get { lock (SyncRoot) { return _version; } }
    }

    /// <summary>Human readable reason when <see cref="IsAvailable"/> is false.</summary>
    public static string Problem
    {
        get { lock (SyncRoot) { return _problem; } }
    }

    /// <summary>
    /// Tries every known location until a complete FFmpeg 9 library set loads.
    /// Safe to call repeatedly; the successful configuration is cached.
    /// </summary>
    public static bool TryInitialize(IEnumerable<string> additionalDirectories = null)
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return true;
            }

            var problems = new List<string>();
            foreach (var directory in EnumerateCandidates(additionalDirectories))
            {
                if (!DirectoryHasLibraries(directory))
                {
                    continue;
                }

                try
                {
                    ffmpeg.RootPath = directory;
                    var version = ffmpeg.av_version_info();
                    var codec = ffmpeg.avcodec_version();
                    var format = ffmpeg.avformat_version();
                    var util = ffmpeg.avutil_version();
                    var swscale = ffmpeg.swscale_version();
                    var swresample = ffmpeg.swresample_version();

                    _rootPath = directory;
                    _version =
                        $"libavutil {VersionString(util)} / libavcodec {VersionString(codec)} / " +
                        $"libavformat {VersionString(format)} / libswscale {VersionString(swscale)} / " +
                        $"libswresample {VersionString(swresample)} ({version})";
                    _initialized = true;
                    _problem = string.Empty;
                    return true;
                }
                catch (Exception error)
                {
                    problems.Add($"{(directory.Length == 0 ? "<system loader path>" : directory)} -> {error.GetType().Name}: {error.Message}");
                }
            }

            _problem =
                "FFmpeg 9 shared libraries were not found. Expected " +
                string.Join(", ", LibraryFileNames()) +
                ". Install FFmpeg 9, set " + RootEnvironmentVariable +
                " to the directory containing those files, or copy them next to the executable." +
                (problems.Count == 0 ? string.Empty : " Attempts: " + string.Join(" | ", problems));
            return false;
        }
    }

    /// <summary>The library file names FFmpeg.AutoGen 9 will dlopen on this platform.</summary>
    public static IReadOnlyList<string> LibraryFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[] { "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll" };
        }
        if (OperatingSystem.IsMacOS())
        {
            return new[] { "libavutil.61.dylib", "libswresample.7.dylib", "libswscale.10.dylib", "libavcodec.63.dylib", "libavformat.63.dylib" };
        }
        return new[] { "libavutil.so.61", "libswresample.so.7", "libswscale.so.10", "libavcodec.so.63", "libavformat.so.63" };
    }

    private static IEnumerable<string> EnumerateCandidates(IEnumerable<string> additionalDirectories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var fromEnvironment = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            foreach (var directory in Expand(fromEnvironment))
            {
                if (seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        if (additionalDirectories != null)
        {
            foreach (var additional in additionalDirectories)
            {
                if (string.IsNullOrWhiteSpace(additional))
                {
                    continue;
                }
                foreach (var directory in Expand(additional))
                {
                    if (seen.Add(directory))
                    {
                        yield return directory;
                    }
                }
            }
        }

        foreach (var directory in Expand(AppDomain.CurrentDomain.BaseDirectory))
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }

        // Last resort: let the operating system loader search its normal paths.
        yield return string.Empty;
    }

    private static IEnumerable<string> Expand(string directory)
    {
        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            yield break;
        }

        yield return full;
        foreach (var subdirectory in new[] { "lib", "lib64", "ffmpeg", "bin", "runtimes/linux-x64/native", "runtimes/win-x64/native", "runtimes/osx-x64/native" })
        {
            yield return Path.Combine(full, subdirectory);
        }
    }

    private static bool DirectoryHasLibraries(string directory)
    {
        if (directory.Length == 0)
        {
            // System loader path: dlopen/LoadLibrary decides. Failure is caught by the caller.
            return true;
        }

        foreach (var library in LibraryFileNames())
        {
            if (!File.Exists(Path.Combine(directory, library)))
            {
                return false;
            }
        }
        return true;
    }

    private static string VersionString(uint version)
    {
        return $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";
    }
}
