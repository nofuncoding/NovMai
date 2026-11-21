using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.Log;
using Godot;

namespace NovMai.Assets;

/// <summary>
/// Resolves user-provided Skin/SFX directories outside the Godot project and
/// loads their files through absolute paths. This intentionally does not use
/// res:// for external content: an exported res:// is the read-only PCK.
/// </summary>
public static class ExternalAssets
{
    private static readonly ILog Log = Logging.Get(nameof(ExternalAssets));
    private static readonly List<string> SkinFiles = new();
    private static readonly List<string> SfxFiles = new();

    public static string Root { get; private set; } = "";
    public static string SkinRoot { get; private set; } = "";
    public static string SfxRoot { get; private set; } = "";
    public static int SkinCount => SkinFiles.Count;
    public static int SfxCount => SfxFiles.Count;

    public static void Initialize()
    {
        Root = ResolveRoot();
        SkinRoot = FindDirectory(Root, "Skin", "skin") ?? Path.Combine(Root, "Skin");
        SfxRoot = FindDirectory(Root, "SFX", "sfx") ?? Path.Combine(Root, "SFX");

        SkinFiles.Clear();
        SfxFiles.Clear();
        Collect(SkinRoot, SkinFiles, ".png", ".jpg", ".jpeg", ".webp");
        Collect(SfxRoot, SfxFiles, ".wav", ".ogg", ".mp3");

        Log.Print($"NovMai: external asset root = {Root}");
        Log.Print($"NovMai: Skin = {SkinRoot} ({SkinFiles.Count} files)");
        Log.Print($"NovMai: SFX  = {SfxRoot} ({SfxFiles.Count} files)");

        if (SkinFiles.Count == 0)
        {
            Log.Print("NovMai: no external Skin files found; using built-in vector visuals.");
        }
        if (SfxFiles.Count == 0)
        {
            Log.Print("NovMai: no external SFX files found; using the generated metronome.");
        }

        ProbeFirst();
    }

    public static Texture2D TryLoadTexture(string relativePath)
    {
        if (!TryResolve(SkinRoot, relativePath, out var fullPath))
        {
            return null;
        }

        try
        {
            using var image = Image.LoadFromFile(fullPath);
            if (image == null || image.IsEmpty())
            {
                Log.Warn($"NovMai: could not decode skin image: {fullPath}");
                return null;
            }
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception error)
        {
            Log.Warn($"NovMai: failed to load skin image {fullPath}: {error.Message}");
            return null;
        }
    }

    /// <summary>Loads the first image found under a skin subdirectory.</summary>
    public static Texture2D TryLoadFirstSkin(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory) || string.IsNullOrEmpty(SkinRoot))
        {
            return null;
        }

        var directory = Path.GetFullPath(Path.Combine(SkinRoot, relativeDirectory));
        var prefix = Path.GetFullPath(SkinRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var file in SkinFiles)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var relative = Path.GetRelativePath(SkinRoot, file);
            if (relative.StartsWith(relativeDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return TryLoadTexture(relative);
            }
        }
        return null;
    }

    public static AudioStream TryLoadAudio(string relativePath)
    {
        if (!TryResolve(SfxRoot, relativePath, out var fullPath))
        {
            return null;
        }
        return TryLoadAudioFile(fullPath);
    }

    /// <summary>Loads an audio file from any absolute or external path.</summary>
    public static AudioStream TryLoadAudioFile(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            return Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".wav" => AudioStreamWav.LoadFromFile(fullPath),
                ".ogg" => AudioStreamOggVorbis.LoadFromFile(fullPath),
                ".mp3" => AudioStreamMP3.LoadFromFile(fullPath),
                _ => null,
            };
        }
        catch (Exception error)
        {
            Log.Warn($"NovMai: failed to load audio {fullPath}: {error.Message}");
            return null;
        }
    }

    private static void ProbeFirst()
    {
        if (SkinFiles.Count > 0)
        {
            var relative = Path.GetRelativePath(SkinRoot, SkinFiles[0]);
            var texture = TryLoadTexture(relative);
            Log.Print(texture == null
                ? $"NovMai: skin loader probe failed for {relative}"
                : $"NovMai: skin loader probe ok for {relative}");
        }

        if (SfxFiles.Count > 0)
        {
            var relative = Path.GetRelativePath(SfxRoot, SfxFiles[0]);
            var audio = TryLoadAudio(relative);
            Log.Print(audio == null
                ? $"NovMai: SFX loader probe failed for {relative}"
                : $"NovMai: SFX loader probe ok for {relative}");
        }
    }

    private static string ResolveRoot()
    {
        var candidates = new List<string>();
        var environmentRoot = System.Environment.GetEnvironmentVariable("NOVMAI_EXTERNAL_ASSETS");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            candidates.Add(Path.GetFullPath(environmentRoot));
        }

        if (OS.HasFeature("editor"))
        {
            var projectDir = ProjectSettings.GlobalizePath("res://");
            var projectParent = Path.GetDirectoryName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(projectParent))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(projectParent, "assets")));
                candidates.Add(Path.GetFullPath(projectParent));
            }
            candidates.Add(Path.GetFullPath(Path.Combine(projectDir, "assets")));
            candidates.Add(Path.GetFullPath(projectDir));
        }
        else
        {
            var executable = OS.GetExecutablePath();
            var executableDir = Path.GetDirectoryName(executable);
            if (!string.IsNullOrEmpty(executableDir))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(executableDir, "assets")));
                candidates.Add(Path.GetFullPath(executableDir));
                var parent = Path.GetDirectoryName(executableDir);
                if (!string.IsNullOrEmpty(parent))
                {
                    candidates.Add(Path.GetFullPath(Path.Combine(parent, "assets")));
                    candidates.Add(Path.GetFullPath(parent));
                    candidates.Add(Path.GetFullPath(Path.Combine(parent, "Resources", "assets")));
                    candidates.Add(Path.GetFullPath(Path.Combine(parent, "Resources")));
                }
            }
        }

        candidates.Add(ProjectSettings.GlobalizePath("user://assets"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string first = "";
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }
            if (first.Length == 0 && Directory.Exists(candidate))
            {
                first = candidate;
            }
            if (HasAssetDirectory(candidate))
            {
                return candidate;
            }
        }

        return first.Length > 0 ? first : Directory.GetCurrentDirectory();
    }

    private static bool HasAssetDirectory(string root)
    {
        return FindDirectory(root, "Skin", "skin") != null
            || FindDirectory(root, "SFX", "sfx") != null;
    }

    private static string FindDirectory(string root, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            if (Directory.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private static void Collect(string root, List<string> target, params string[] extensions)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var allowed = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (allowed.Contains(Path.GetExtension(file)))
            {
                target.Add(file);
            }
        }
        target.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryResolve(string root, string relativePath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
