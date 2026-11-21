using System;
using Chickensoft.Log;
using Godot;

namespace NovMai.Rendering;

/// <summary>
/// A <see cref="VideoStream"/> that plays any container/codec FFmpeg supports
/// (MP4/H.264, WebM/VP9, MKV, MOV, AVI, ...). Assign it to a
/// <see cref="VideoStreamPlayer"/> or load it through
/// <see cref="FFmpegResourceFormatLoader"/>.
/// </summary>
[GlobalClass]
public partial class FFmpegVideoStream : VideoStream
{
    private static readonly ILog Log = Logging.Get<FFmpegVideoStream>();

    /// <summary>Creates a stream for a <c>res://</c>, <c>user://</c> or absolute OS path.</summary>
    public static FFmpegVideoStream Create(string path)
    {
        return new FFmpegVideoStream { File = path };
    }

    public override VideoStreamPlayback _InstantiatePlayback()
    {
        var path = File;
        if (string.IsNullOrEmpty(path))
        {
            Log.Err("FFmpegVideoStream: the 'file' property is empty.");
            return null;
        }

        if (!FFmpegNative.IsAvailable && !FFmpegVideoRuntime.EnsureNative())
        {
            Log.Err($"FFmpegVideoStream: {FFmpegNative.Problem}");
            return null;
        }

        var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            Log.Err($"FFmpegVideoStream: cannot open '{path}' ({Godot.FileAccess.GetOpenError()}).");
            return null;
        }

        try
        {
            return new FFmpegVideoPlayback(file, path);
        }
        catch (Exception error)
        {
            file.Dispose();
            Log.Err($"FFmpegVideoStream: failed to open '{path}': {error.Message}");
            return null;
        }
    }
}
