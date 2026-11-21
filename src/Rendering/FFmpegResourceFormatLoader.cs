using Godot;

namespace NovMai.Rendering;

/// <summary>
/// Registers FFmpeg-backed video containers with Godot's resource system, so
/// <c>ResourceLoader.Load&lt;VideoStream&gt;("res://clip.mp4")</c> (and
/// <c>VideoStreamPlayer.Stream</c> assignment in the editor, once installed)
/// work without a conversion step.
/// </summary>
[GlobalClass]
public partial class FFmpegResourceFormatLoader : ResourceFormatLoader
{
    /// <summary>Video containers handled by the FFmpeg loader.</summary>
    /// <remarks>
    /// <c>ogv</c> is intentionally absent: Godot ships a built-in Theora
    /// <see cref="VideoStream"/> implementation that keeps working untouched.
    /// </remarks>
    public static readonly string[] Extensions =
    {
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv",
        "mpg", "mpeg", "m2v", "ts", "m2ts", "mts", "3gp", "3g2",
    };

    public override string[] _GetRecognizedExtensions()
    {
        return Extensions;
    }

    public override string _GetResourceType(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension))
        {
            extension = extension.TrimStart('.').ToLowerInvariant();
            foreach (var recognized in Extensions)
            {
                if (recognized == extension)
                {
                    return "VideoStream";
                }
            }
        }
        return string.Empty;
    }

    public override bool _HandlesType(StringName type)
    {
        return type == "VideoStream" || ClassDB.IsParentClass(type, "VideoStream");
    }

    public override bool _Exists(string path)
    {
        return Godot.FileAccess.FileExists(path);
    }

    public override Variant _Load(string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            return default;
        }

        return new FFmpegVideoStream { File = path };
    }
}
