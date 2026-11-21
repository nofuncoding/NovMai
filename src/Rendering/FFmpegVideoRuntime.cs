using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace NovMai.Rendering;

/// <summary>
/// One-call setup for the FFmpeg video format:
/// <list type="bullet">
///   <item>loads the FFmpeg 9 shared libraries (see <see cref="FFmpegNative"/>);</item>
///   <item>registers <see cref="FFmpegResourceFormatLoader"/> with Godot's ResourceLoader.</item>
/// </list>
/// Call <see cref="Install"/> early (for example from an autoload or the main
/// scene's <c>_Ready</c>); <see cref="FFmpegVideoStream"/> also calls it lazily
/// when a playback is instantiated.
/// </summary>
public static class FFmpegVideoRuntime
{
    private static readonly object SyncRoot = new();
    private static FFmpegResourceFormatLoader _loader;
    private static bool _nativeReady;
    private static bool _installed;

    /// <summary>True when the resource loader has been registered.</summary>
    public static bool IsInstalled
    {
        get { lock (SyncRoot) { return _installed; } }
    }

    /// <summary>True when FFmpeg shared libraries are loaded.</summary>
    public static bool NativeReady
    {
        get { lock (SyncRoot) { return _nativeReady; } }
    }

    /// <summary>
    /// Loads FFmpeg and registers the format loader. Never throws; failures are
    /// logged and reported through <see cref="FFmpegNative.Problem"/>.
    /// </summary>
    public static bool Install(bool verbose = true)
    {
        lock (SyncRoot)
        {
            EnsureNativeLocked(verbose);

            if (!_installed)
            {
                _loader = new FFmpegResourceFormatLoader();
                ResourceLoader.AddResourceFormatLoader(_loader, true);
                _installed = true;
                if (verbose)
                {
                    GD.Print("NovMai FFmpeg: video format loader registered (" +
                             string.Join(", ", FFmpegResourceFormatLoader.Extensions) + ").");
                }
            }

            return _nativeReady;
        }
    }

    /// <summary>Unregisters the format loader (the native libraries stay loaded).</summary>
    public static void Uninstall()
    {
        lock (SyncRoot)
        {
            if (!_installed)
            {
                return;
            }
            ResourceLoader.RemoveResourceFormatLoader(_loader);
            _loader = null;
            _installed = false;
        }
    }

    /// <summary>Loads FFmpeg shared libraries only (used by <see cref="FFmpegVideoStream"/>).</summary>
    public static bool EnsureNative(bool verbose = true)
    {
        lock (SyncRoot)
        {
            return EnsureNativeLocked(verbose);
        }
    }

    private static bool EnsureNativeLocked(bool verbose)
    {
        if (_nativeReady)
        {
            return true;
        }

        if (FFmpegNative.TryInitialize(SearchDirectories()))
        {
            _nativeReady = true;
            if (verbose)
            {
                var origin = FFmpegNative.RootPath.Length == 0 ? "system loader path" : FFmpegNative.RootPath;
                GD.Print($"NovMai FFmpeg: {FFmpegNative.Version} [{origin}]");
            }
            return true;
        }

        if (verbose)
        {
            GD.PushError($"NovMai FFmpeg: {FFmpegNative.Problem}");
        }
        return false;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var executable = OS.GetExecutablePath();
        if (!string.IsNullOrEmpty(executable))
        {
            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrEmpty(directory))
            {
                yield return directory;
            }
        }

        var project = ProjectSettings.GlobalizePath("res://");
        if (!string.IsNullOrEmpty(project))
        {
            yield return project;
        }
    }
}
