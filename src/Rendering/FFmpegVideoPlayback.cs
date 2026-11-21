using System;
using Chickensoft.Log;
using Godot;

namespace NovMai.Rendering;

/// <summary>
/// Godot <see cref="VideoStreamPlayback"/> backed by FFmpeg.
///
/// Godot 4.7's VideoStreamPlayer drives the playback like this:
///   1. <c>_InstantiatePlayback</c> opens the file, so <c>_GetTexture</c>,
///      <c>_GetChannels</c> and <c>_GetMixRate</c> must be valid immediately;
///   2. the player installs an audio mix callback when <c>_GetChannels() &gt; 0</c>;
///   3. every process frame it calls <c>_Update(delta)</c>; we advance our clock,
///      present due video frames into an ImageTexture and push decoded PCM with
///      <see cref="VideoStreamPlayback.MixAudio"/>;
///   4. when <c>_IsPlaying()</c> turns false the player flushes its resampler and
///      emits <c>finished</c> (or loops, which calls <c>_Play</c> again).
/// </summary>
public partial class FFmpegVideoPlayback : VideoStreamPlayback
{
    private static readonly ILog Log = Logging.Get<FFmpegVideoPlayback>();

    private const double FrameAheadSeconds = 0.25;
    private const int VideoQueueTarget = 3;
    private const int AudioAheadFrames = 2048;

    private readonly FFmpegMediaSource _source;
    private readonly string _path;
    private ImageTexture _texture;
    private Image _image;
    private float[] _pushBuffer = Array.Empty<float>();

    private double _time;
    private double _delayCompensation;
    private double _duration;
    private bool _isPlaying;
    private bool _paused;
    private bool _finished;
    private bool _disposed;

    public FFmpegVideoPlayback(Godot.FileAccess file, string path)
    {
        _path = path;
        var input = new GodotFileInput(file);
        try
        {
            _source = FFmpegMediaSource.Open(input);
        }
        catch
        {
            input.Dispose();
            throw;
        }

        _duration = _source.Duration;
        _delayCompensation = ReadDelayCompensation();

        if (_source.HasAudio)
        {
            _pushBuffer = new float[AudioAheadFrames * _source.AudioChannels];
        }

        // The first frame was decoded by FFmpegMediaSource.Open; show it right away
        // so VideoStreamPlayer gets a correctly sized, non-empty texture during
        // set_stream(). Creating the texture from the real frame (instead of
        // updating an empty one) also keeps headless readback useful.
        if (_source.TryDequeueVideo(out var rgba, out var width, out var height, out _))
        {
            _image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba.AsSpan(0, width * height * 4));
        }
        else
        {
            _image = Image.CreateEmpty(_source.VideoWidth, _source.VideoHeight, false, Image.Format.Rgba8);
        }
        _texture = ImageTexture.CreateFromImage(_image);

        Log.Print($"NovMai FFmpeg: opened {_path} ({_source.VideoWidth}x{_source.VideoHeight}, " +
                 $"{_duration:F2}s, {_source.FrameRate:F2} fps, " +
                 (_source.HasAudio ? $"{_source.AudioChannels}ch @ {_source.AudioSampleRate}Hz" : "no audio") + ")");
    }

    public override Texture2D _GetTexture()
    {
        return _texture;
    }

    /// <summary>
    /// Diagnostics/thumbnail helper: a copy of the most recently uploaded frame,
    /// or null before the first frame. The returned image is decoupled from the
    /// playback and safe to keep.
    /// </summary>
    public Image GetCurrentFrameImage()
    {
        return _image?.Duplicate() as Image;
    }

    public override double _GetLength()
    {
        return _duration;
    }

    public override double _GetPlaybackPosition()
    {
        var position = _time - _delayCompensation;
        if (position < 0.0)
        {
            position = 0.0;
        }
        if (_duration > 0.0 && position > _duration)
        {
            position = _duration;
        }
        return position;
    }

    public override bool _IsPlaying() => _isPlaying;

    public override bool _IsPaused() => _paused;

    public override int _GetChannels() => _source.HasAudio ? _source.AudioChannels : 0;

    public override int _GetMixRate() => _source.HasAudio ? _source.AudioSampleRate : 0;

    public override void _Play()
    {
        if (_disposed)
        {
            return;
        }

        if (_finished)
        {
            _source.Seek(0.0);
            _time = 0.0;
            _finished = false;
        }

        _delayCompensation = ReadDelayCompensation();
        _isPlaying = true;
    }

    public override void _Stop()
    {
        if (_disposed)
        {
            return;
        }

        _isPlaying = false;
        _finished = false;
        _time = 0.0;
        _source.Seek(0.0);
    }

    public override void _SetPaused(bool paused)
    {
        _paused = paused;
    }

    public override void _SetAudioTrack(int idx)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _source.SelectAudioTrack(idx);
            if (_source.HasAudio)
            {
                _pushBuffer = new float[AudioAheadFrames * _source.AudioChannels];
            }
        }
        catch (Exception error)
        {
            Log.Err($"NovMai FFmpeg: failed to switch audio track {idx} for {_path}: {error.Message}");
        }
    }

    public override void _Seek(double time)
    {
        if (_disposed)
        {
            return;
        }

        if (_duration > 0.0)
        {
            time = Math.Clamp(time, 0.0, _duration);
        }
        else if (time < 0.0)
        {
            time = 0.0;
        }

        try
        {
            _source.Seek(time);
        }
        catch (Exception error)
        {
            Log.Err($"NovMai FFmpeg: seek to {time:F3}s failed for {_path}: {error.Message}");
        }

        _time = time;
        _finished = false;
        PresentDueFrames();
        PushAudio();
    }

    public override void _Update(double delta)
    {
        if (_disposed || !_isPlaying || _paused)
        {
            return;
        }

        _time += Math.Max(0.0, delta);
        var compensationTime = _time - _delayCompensation;

        try
        {
            DecodeAhead();
            PresentDueFrames();
            PushAudio();
        }
        catch (Exception error)
        {
            Log.Err($"NovMai FFmpeg: playback of {_path} failed: {error.Message}");
            _isPlaying = false;
            return;
        }

        if (_source.IsDrained && compensationTime >= _source.EndTime - 0.001)
        {
            _isPlaying = false;
            _finished = true;
        }
    }

    private void DecodeAhead()
    {
        var pumps = 0;
        while (pumps++ < 32)
        {
            var needsVideo = _source.VideoQueueCount < VideoQueueTarget;
            var needsAudio = _source.HasAudio && _source.AudioFramesAvailable < AudioAheadFrames;
            if (!needsVideo && !needsAudio)
            {
                break;
            }
            if (!_source.Pump())
            {
                break;
            }
        }
    }

    private void PresentDueFrames()
    {
        var compensationTime = _time - _delayCompensation;
        byte[] rgba = null;
        var width = 0;
        var height = 0;
        var deadline = compensationTime + 0.0005;

        while (_source.TryPeekVideoTime(out var frameTime) && frameTime <= deadline)
        {
            if (!_source.TryDequeueVideo(out var frameData, out var frameWidth, out var frameHeight, out _))
            {
                break;
            }
            rgba = frameData;
            width = frameWidth;
            height = frameHeight;
        }

        if (rgba != null)
        {
            UploadFrame(rgba, width, height);
        }
    }

    private void PushAudio()
    {
        if (!_source.HasAudio || _source.AudioChannels <= 0)
        {
            return;
        }

        var channels = _source.AudioChannels;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var framesAvailable = _source.AudioFramesAvailable;
            if (framesAvailable <= 0)
            {
                return;
            }

            var frames = Math.Min(framesAvailable, _pushBuffer.Length / channels);
            if (frames <= 0)
            {
                return;
            }

            var floats = _source.PeekAudio(_pushBuffer.AsSpan(0, frames * channels));
            if (floats <= 0)
            {
                return;
            }

            var offered = floats / channels;
            var consumed = MixAudio(offered, _pushBuffer, 0);
            if (consumed <= 0)
            {
                return;
            }

            _source.ConsumeAudio(consumed);
            if (consumed < offered)
            {
                return;
            }
        }
    }

    private void UploadFrame(byte[] rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0)
        {
            return;
        }

        var byteCount = width * height * 4;
        if (rgba.Length < byteCount)
        {
            return;
        }

        if (_image == null || _image.GetWidth() != width || _image.GetHeight() != height)
        {
            _image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba.AsSpan(0, byteCount));
            _texture = ImageTexture.CreateFromImage(_image);
            return;
        }

        _image.SetData(width, height, false, Image.Format.Rgba8, rgba.AsSpan(0, byteCount));
        _texture.Update(_image);
    }

    private static double ReadDelayCompensation()
    {
        var setting = ProjectSettings.GetSetting("audio/video/video_delay_compensation_ms", 0.0);
        return setting.AsDouble() / 1000.0;
    }

    public override void _Notification(int what)
    {
        if (what == (int)NotificationPredelete)
        {
            ReleaseResources();
        }
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseResources();
        base.Dispose(disposing);
    }

    private void ReleaseResources()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _isPlaying = false;
        _texture = null;
        _image = null;
        _source?.Dispose();
    }
}
