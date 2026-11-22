using System;
using System.Globalization;
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

    private const int VideoQueueTarget = 3;
    private const int AudioAheadFrames = 2048;

    // Audio is pushed at most this far ahead of the video clock. A small lead
    // keeps the player's resampler fed without buffering an audible backlog
    // that would keep playing if the main thread is suspended (editor pause,
    // breakpoint, GC hitch).
    private const double InitialAudioLeadSeconds = 0.2;
    private const double MaxAudioLeadSeconds = 0.5;

    // Minimum interval between two "resynced" log lines (corrections themselves
    // are applied every frame; only logging is rate limited).
    private const double ResyncCooldownSeconds = 0.75;

    private FFmpegMediaSource _source;
    private string _path = string.Empty;
    private ImageTexture _texture;
    private Image _image;
    private float[] _pushBuffer = Array.Empty<float>();

    private double _time;
    private double _delayCompensation;
    private double _audioLatencySeconds;
    private AudioEffectCapture _audioClockCapture;
    private int _audioClockBus = -1;
    private bool _audioClockActive;
    private bool _audioClockUseCapture;
    private int _audioClockMixRate = 1;
    private long _audioClockMixedFrames;
    private long _audioClockOriginFrames;
    private double _audioClockOriginTime;
    private long _audioClockLastMixedValue;
    private ulong _audioClockLastAdvanceUsec;
    private double _configuredAudioLatency = -1.0;
    private bool _audioLatencyMeasured;
    private int _latencyProbeUpdates;
    private double _latencyProbeSum;
    private int _latencyProbeCount;
    private double _duration;
    private double _audioLeadSeconds = InitialAudioLeadSeconds;
    private double _audioContentTime;
    private ulong _lastUpdateUsec;
    private ulong _lastResyncUsec;
    private bool _stallResyncEnabled = true;
    private int _lastDroppedFrames;
    private bool _isPlaying;
    private bool _paused;
    private bool _finished;
    private bool _disposed;
    private bool _lostSourceLogged;

    /// <summary>Number of stall resynchronisations applied in this process (diagnostics/tests).</summary>
    internal static int ResyncCount;

    private static readonly bool DebugFrames = IsEnvironmentFlagSet("NOVMAI_VIDEO_DEBUG");
    private long _debugFrames;
    private double _debugWallSum;
    private double _debugDeltaSum;
    private long _debugDropped;
    private int _debugResyncs;
    private long _debugPushAllowed;
    private long _debugPushAccepted;
    private int _debugRingMin = int.MaxValue;
    private int _debugPushFullFrames;
    private int _debugPushPartialFrames;
    private ulong _debugLastLogUsec;

    /// <summary>
    /// Parameterless constructor required by Godot when it recreates the managed
    /// instance for a native playback object that is still alive (for example
    /// after a finalizer/GC race). Such an instance has no decoder state and
    /// stays inert; <see cref="FFmpegVideoStream"/> always uses the constructor
    /// below.
    /// </summary>
    private FFmpegVideoPlayback()
    {
    }

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
        ResetAudioLatency();
        _stallResyncEnabled = ReadStallResyncEnabled();
        AttachAudioClock();

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

    public override int _GetChannels() => _source != null && _source.HasAudio ? _source.AudioChannels : 0;

    public override int _GetMixRate() => _source != null && _source.HasAudio ? _source.AudioSampleRate : 0;

    public override void _Play()
    {
        if (_disposed || !RequireSource("play"))
        {
            return;
        }

        if (_finished)
        {
            _source.Seek(0.0);
            _time = 0.0;
            _audioContentTime = 0.0;
            _finished = false;
        }

        _lastUpdateUsec = 0;
        _lastResyncUsec = 0;
        _audioLeadSeconds = InitialAudioLeadSeconds;
        _delayCompensation = ReadDelayCompensation();
        ResetAudioLatency();
        _stallResyncEnabled = ReadStallResyncEnabled();
        AttachAudioClock();
        ResetAudioClockOrigin(_time);
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
        _audioContentTime = 0.0;
        _lastUpdateUsec = 0;
        _lastResyncUsec = 0;
        _audioLeadSeconds = InitialAudioLeadSeconds;
        DetachAudioClock();
        _source?.Seek(0.0);
        _audioClockOriginTime = 0.0;
        _audioClockOriginFrames = 0;
        _audioClockMixedFrames = 0;
        _audioClockUseCapture = false;
    }

    public override void _SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused)
        {
            // The master bus keeps mixing while the video is paused; drop the
            // accumulated frames and restart the audio clock from where the
            // picture froze.
            _lastUpdateUsec = Time.GetTicksUsec();
            ResetAudioClockOrigin(_time);
        }
    }

    public override void _SetAudioTrack(int idx)
    {
        if (_disposed || !RequireSource("audio track switch"))
        {
            return;
        }

        try
        {
            _source.SelectAudioTrack(idx);
            if (_source.HasAudio)
            {
                _pushBuffer = new float[AudioAheadFrames * _source.AudioChannels];
                // The decoder ring was reset for the new track: restart the audio
                // clock at the current video position.
                _audioContentTime = _time;
                _audioLeadSeconds = InitialAudioLeadSeconds;
            }
        }
        catch (Exception error)
        {
            Log.Err($"NovMai FFmpeg: failed to switch audio track {idx} for {_path}: {error.Message}");
        }
    }

    public override void _Seek(double time)
    {
        if (_disposed || !RequireSource("seek"))
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
        _audioContentTime = time;
        _lastUpdateUsec = Time.GetTicksUsec();
        _lastResyncUsec = 0;
        ResetAudioClockOrigin(time);
        _finished = false;
        PresentDueFrames();
        PushAudio();
    }

    public override void _Update(double delta)
    {
        if (_disposed || !_isPlaying || _paused || !RequireSource("update"))
        {
            return;
        }

        var nowUsec = Time.GetTicksUsec();
        var wallGap = _lastUpdateUsec == 0 ? 0.0 : (nowUsec - _lastUpdateUsec) / 1_000_000.0;
        _lastUpdateUsec = nowUsec;

        ProbeAudioLatency();
        var step = Math.Max(0.0, delta);
        var frameAdvance = 0.0;
        if (_audioClockUseCapture && _audioClockActive)
        {
            DrainAudioClock();
            if (_audioClockMixedFrames != _audioClockLastMixedValue)
            {
                _audioClockLastMixedValue = _audioClockMixedFrames;
                _audioClockLastAdvanceUsec = nowUsec;
            }
            else if (_audioClockLastAdvanceUsec != 0 &&
                     nowUsec - _audioClockLastAdvanceUsec > 2_000_000UL)
            {
                // The audio server stopped mixing (driver paused/unplugged). Do not
                // freeze the video forever: fall back to the wall clock.
                Log.Warn("NovMai FFmpeg: audio clock stopped advancing; falling back to the wall clock.");
                DetachAudioClock();
                frameAdvance = AdvanceClock(wallGap, step, nowUsec);
            }

            if (_audioClockActive)
            {
                var clock = _audioClockOriginTime +
                            (_audioClockMixedFrames - _audioClockOriginFrames) / (double)_audioClockMixRate;
                if (clock > _time)
                {
                    frameAdvance = clock - _time;
                    _time = clock;
                }
                else
                {
                    frameAdvance = 0.0;
                }
            }
        }
        else
        {
            frameAdvance = AdvanceClock(wallGap, step, nowUsec);
        }
        var compensationTime = _time - _delayCompensation - _audioLatencySeconds;

        try
        {
            DecodeAhead(frameAdvance);
            PresentDueFrames();
            PushAudio();
        }
        catch (Exception error)
        {
            Log.Err($"NovMai FFmpeg: playback of {_path} failed: {error.Message}");
            _isPlaying = false;
            return;
        }

        if (DebugFrames)
        {
            RecordDebugFrame(wallGap, step);
        }

        if (_source.IsDrained && compensationTime >= _source.EndTime - 0.001)
        {
            _isPlaying = false;
            _finished = true;
            DetachAudioClock();
        }
    }

    private void DecodeAhead(double frameAdvance)
    {
        // Keep enough decoded audio for the current clock advance plus the lead;
        // on a slow main loop a single update may need several frames of audio.
        var audioTarget = AudioAheadFrames;
        if (_source.HasAudio && _source.AudioSampleRate > 0)
        {
            var needed = (int)((_audioLeadSeconds + frameAdvance + 0.05) * _source.AudioSampleRate);
            audioTarget = Math.Max(audioTarget, needed);
        }

        var pumps = 0;
        while (pumps++ < 64)
        {
            // Keep decoded video close to the clock, not just a fixed frame count:
            // when the main loop is slow the clock advances several frames per
            // update and the picture would otherwise fall into slow motion.
            var decodedAhead = _source.VideoQueueCount > 0 && _source.NewestVideoTime >= _time + 0.02;
            var needsVideo = _source.VideoQueueCount < VideoQueueTarget || !decodedAhead;
            var needsAudio = _source.HasAudio && _source.AudioFramesAvailable < audioTarget;
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
        var compensationTime = _time - _delayCompensation - _audioLatencySeconds;
        byte[] rgba = null;
        var width = 0;
        var height = 0;
        var deadline = compensationTime + 0.0005;
        _lastDroppedFrames = _source.DropVideoFramesBefore(deadline);

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

    /// <summary>
    /// Pushes decoded PCM through the player's mix callback while keeping the
    /// audio content at most <see cref="_audioLeadSeconds"/> ahead of the video
    /// clock. Returns the number of frames accepted by the resampler.
    /// </summary>
    private int PushAudio()
    {
        if (!_source.HasAudio || _source.AudioChannels <= 0)
        {
            return 0;
        }

        var channels = _source.AudioChannels;
        var rate = Math.Max(1, _source.AudioSampleRate);
        var totalAccepted = 0;
        if (DebugFrames)
        {
            _debugRingMin = Math.Min(_debugRingMin, _source.AudioFramesAvailable);
        }

        for (var iteration = 0; iteration < 32; iteration++)
        {
            var framesAvailable = _source.AudioFramesAvailable;
            var allowedByClock = (int)Math.Floor((_time + _audioLeadSeconds - _audioContentTime) * rate);
            if (DebugFrames && allowedByClock > 0)
            {
                _debugPushAllowed += allowedByClock;
            }
            if (allowedByClock <= 0)
            {
                return FinishPush(totalAccepted);
            }

            if (framesAvailable <= 0)
            {
                // The decoded ring starved while we still wanted to push audio:
                // grow the lead so slow main-thread frames do not underrun.
                if (_audioContentTime < _time)
                {
                    _audioLeadSeconds = Math.Min(MaxAudioLeadSeconds, _audioLeadSeconds + 0.05);
                }
                return FinishPush(totalAccepted);
            }

            var frames = Math.Min(Math.Min(framesAvailable, allowedByClock), _pushBuffer.Length / channels);
            if (frames <= 0)
            {
                return FinishPush(totalAccepted);
            }

            var floats = _source.PeekAudio(_pushBuffer.AsSpan(0, frames * channels));
            if (floats <= 0)
            {
                return FinishPush(totalAccepted);
            }

            var offered = floats / channels;
            var consumed = MixAudio(offered, _pushBuffer, 0);
            if (consumed <= 0)
            {
                // 0: the player's resampler is full; negative: no mix callback is
                // installed (playback not driven by a VideoStreamPlayer).
                if (DebugFrames && consumed == 0)
                {
                    _debugPushFullFrames++;
                }
                return FinishPush(totalAccepted);
            }

            _source.ConsumeAudio(consumed);
            _audioContentTime += consumed / (double)rate;
            totalAccepted += consumed;
            if (!_audioClockUseCapture && _audioClockActive)
            {
                _audioClockUseCapture = true;
                ResetAudioClockOrigin(_time);
            }
            if (consumed < offered)
            {
                if (DebugFrames)
                {
                    _debugPushPartialFrames++;
                }
                return FinishPush(totalAccepted);
            }
        }

        return FinishPush(totalAccepted);
    }

    private int FinishPush(int accepted)
    {
        if (DebugFrames)
        {
            _debugPushAccepted += accepted;
        }
        return accepted;
    }

    /// <summary>
    /// Advances the video clock. The wall clock is used instead of the reported
    /// frame delta: when a frame runs long Godot can report a smaller delta than
    /// the real elapsed time, which used to drift the picture behind the sound on
    /// slow frames. Audio stays the master clock: the picture never advances past
    /// the PCM already handed to the player's resampler, so after a suspension
    /// (editor pause/breakpoint, GC) it catches up only by the audio that played.
    /// </summary>
    private double AdvanceClock(double wallGap, double step, ulong nowUsec)
    {
        if (!_stallResyncEnabled)
        {
            _time += step;
            return step;
        }

        var realAdvance = Math.Max(wallGap, step);
        if (realAdvance <= 0.0)
        {
            return 0.0;
        }

        if (!_source.HasAudio || _source.AudioChannels <= 0 || _source.IsAudioDrained)
        {
            _time += realAdvance;
            return realAdvance;
        }

        // Never advance past the audio content that the output thread could have
        // played since the last update.
        var runway = Math.Max(0.0, _audioContentTime - _time);
        var advance = Math.Min(realAdvance, Math.Max(runway, step));
        if (advance < realAdvance - 0.05)
        {
            ResyncCount++;
            _debugResyncs++;
            if (_lastResyncUsec == 0 ||
                nowUsec - _lastResyncUsec >= (ulong)(ResyncCooldownSeconds * 1_000_000.0))
            {
                _lastResyncUsec = nowUsec;
                Log.Print("NovMai FFmpeg: resynced +" + ((advance - step) * 1000.0).ToString("F0") +
                         "ms after a main-thread stall (wall gap " + wallGap.ToString("F2") +
                         "s, delta " + step.ToString("F2") + "s)");
            }
        }

        _time += advance;
        return advance;
    }

    /// <summary>
    /// Tracks the audio-server mix clock (master bus frames) so the picture can
    /// follow the audio timeline exactly. The wall clock is not enough: the audio
    /// thread can run slower than wall time under load, which used to fill the
    /// player's resampler and leave the sound permanently behind the picture.
    /// </summary>
    private void AttachAudioClock()
    {
        if (_audioClockActive || !_source.HasAudio)
        {
            return;
        }

        try
        {
            var bus = AudioServer.GetBusIndex("Master");
            if (bus < 0)
            {
                return;
            }

            var capture = new AudioEffectCapture { BufferLength = 2.0f };
            AudioServer.AddBusEffect(bus, capture, -1);
            if (!IsBusEffectAttached(bus, capture))
            {
                Log.Warn("NovMai FFmpeg: could not attach the audio clock effect; using the wall clock.");
                return;
            }

            _audioClockCapture = capture;
            _audioClockBus = bus;
            _audioClockMixRate = Math.Max(1, (int)AudioServer.GetMixRate());
            _audioClockMixedFrames = 0;
            _audioClockOriginFrames = 0;
            _audioClockOriginTime = 0.0;
            _audioClockActive = true;
        }
        catch (Exception error)
        {
            Log.Warn($"NovMai FFmpeg: audio clock unavailable ({error.Message}); using the wall clock.");
        }
    }

    private static bool IsBusEffectAttached(int bus, AudioEffect effect)
    {
        var effectId = effect.GetInstanceId();
        for (var index = 0; index < AudioServer.GetBusEffectCount(bus); index++)
        {
            var candidate = AudioServer.GetBusEffect(bus, index);
            if (candidate != null && candidate.GetInstanceId() == effectId)
            {
                return true;
            }
        }
        return false;
    }

    private void DetachAudioClock()
    {
        if (!_audioClockActive)
        {
            return;
        }

        _audioClockActive = false;
        _audioClockUseCapture = false;
        try
        {
            if (_audioClockCapture != null && _audioClockBus >= 0 &&
                GodotObject.IsInstanceValid(_audioClockCapture))
            {
                var captureId = _audioClockCapture.GetInstanceId();
                for (var index = AudioServer.GetBusEffectCount(_audioClockBus) - 1; index >= 0; index--)
                {
                    var effect = AudioServer.GetBusEffect(_audioClockBus, index);
                    if (effect != null && effect.GetInstanceId() == captureId)
                    {
                        AudioServer.RemoveBusEffect(_audioClockBus, index);
                        break;
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Warn($"NovMai FFmpeg: failed to remove the audio clock effect ({error.Message}).");
        }

        _audioClockCapture = null;
        _audioClockBus = -1;
    }

    private void DrainAudioClock()
    {
        if (!_audioClockActive || _audioClockCapture == null ||
            !GodotObject.IsInstanceValid(_audioClockCapture))
        {
            return;
        }

        try
        {
            // Cumulative frames mixed into the bus. Once the capture ring is full,
            // pushed frames stop counting and discarded frames take over, so the
            // sum keeps advancing. No buffer reads (and no per-frame allocations)
            // are needed.
            _audioClockMixedFrames = (long)_audioClockCapture.GetPushedFrames() +
                                     (long)_audioClockCapture.GetDiscardedFrames();
        }
        catch (Exception error)
        {
            Log.Warn($"NovMai FFmpeg: audio clock read failed ({error.Message}); falling back to the wall clock.");
            DetachAudioClock();
        }
    }

    private void ResetAudioClockOrigin(double time)
    {
        if (!_audioClockActive)
        {
            return;
        }

        DrainAudioClock();
        _audioClockOriginFrames = _audioClockMixedFrames;
        _audioClockOriginTime = time;
        _audioClockLastMixedValue = _audioClockMixedFrames;
        _audioClockLastAdvanceUsec = Time.GetTicksUsec();
    }

    private void RecordDebugFrame(double wallGap, double step)
    {
        _debugFrames++;
        _debugWallSum += wallGap;
        _debugDeltaSum += step;
        _debugDropped += _lastDroppedFrames;

        if (_debugLastLogUsec == 0)
        {
            _debugLastLogUsec = _lastUpdateUsec;
            return;
        }

        if (_lastUpdateUsec - _debugLastLogUsec < 2_000_000UL)
        {
            return;
        }

        var seconds = (_lastUpdateUsec - _debugLastLogUsec) / 1_000_000.0;
        var frames = Math.Max(1L, _debugFrames);
        Log.Print("NovMai FFmpeg: stats fps=" + (frames / seconds).ToString("F1") +
                 " avgDelta=" + (_debugDeltaSum / frames * 1000.0).ToString("F1") + "ms" +
                 " avgWall=" + (_debugWallSum / frames * 1000.0).ToString("F1") + "ms" +
                 " dropped=" + _debugDropped + " resyncs=" + _debugResyncs +
                 " time=" + _time.ToString("F2") + "s" +
                 " lead=" + (_audioContentTime - _time).ToString("F2") + "s" +
                 " videoLead=" + ((_source.NewestVideoTime - _time)).ToString("F2") + "s" +
                 " audioDelay=" + ((_delayCompensation + _audioLatencySeconds) * 1000.0).ToString("F1") + "ms" +
                 " outLatency=" + (AudioServer.GetOutputLatency() * 1000.0).ToString("F1") + "ms" +
                 " mixRate=" + AudioServer.GetMixRate() +
                 " audioClock=" + (_audioClockUseCapture && _audioClockActive ? "capture" : "wall") +
                 " mixed=" + ((_audioClockMixedFrames - _audioClockOriginFrames) / (double)Math.Max(1, _audioClockMixRate)).ToString("F2") + "s" +
                 " pushAllowed=" + _debugPushAllowed + " pushAccepted=" + _debugPushAccepted +
                 " ringMin=" + (_debugRingMin == int.MaxValue ? -1 : _debugRingMin) +
                 " full=" + _debugPushFullFrames + " partial=" + _debugPushPartialFrames);

        _debugFrames = 0;
        _debugWallSum = 0.0;
        _debugDeltaSum = 0.0;
        _debugDropped = 0;
        _debugResyncs = 0;
        _debugPushAllowed = 0;
        _debugPushAccepted = 0;
        _debugRingMin = int.MaxValue;
        _debugPushFullFrames = 0;
        _debugPushPartialFrames = 0;
        _debugLastLogUsec = _lastUpdateUsec;
    }

    private static bool ReadStallResyncEnabled()
    {
        var environment = System.Environment.GetEnvironmentVariable("NOVMAI_FFMPEG_STALL_RESYNC");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return !environment.Equals("0", StringComparison.Ordinal) &&
                   !environment.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        return ProjectSettings.GetSetting("audio/video/ffmpeg_stall_resync", true).AsBool();
    }

    private static bool IsEnvironmentFlagSet(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) && value != "0" &&
               !value.Equals("false", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Latency between mixing audio and hearing it (device + driver). The picture
    /// is delayed by this much so it matches the sound instead of running ahead.
    /// A negative project setting disables the compensation; the environment
    /// variable overrides both (e.g. 0 to disable, 80 to tune by ear).
    /// </summary>
    private static double ReadConfiguredAudioLatency()
    {
        var environment = System.Environment.GetEnvironmentVariable("NOVMAI_FFMPEG_AUDIO_LATENCY_MS");
        if (!string.IsNullOrWhiteSpace(environment) &&
            double.TryParse(environment, NumberStyles.Float, CultureInfo.InvariantCulture, out var overrideMs))
        {
            return Math.Clamp(overrideMs / 1000.0, -0.5, 0.5);
        }

        var configured = ProjectSettings.GetSetting("audio/video/ffmpeg_audio_latency_ms", -1.0).AsDouble();
        return configured >= 0.0 ? Math.Clamp(configured / 1000.0, 0.0, 0.5) : -1.0;
    }

    private void ResetAudioLatency()
    {
        _configuredAudioLatency = ReadConfiguredAudioLatency();
        _audioLatencyMeasured = _configuredAudioLatency >= 0.0;
        _audioLatencySeconds = _audioLatencyMeasured ? _configuredAudioLatency : 0.0;
        _latencyProbeUpdates = 0;
        _latencyProbeSum = 0.0;
        _latencyProbeCount = 0;
    }

    /// <summary>
    /// Samples the device output latency during the first ~2 seconds of playback
    /// (the driver may report 0 right at startup) and keeps the smallest stable
    /// value, which approximates the steady-state latency best.
    /// </summary>
    private void ProbeAudioLatency()
    {
        if (_audioLatencyMeasured)
        {
            return;
        }

        _latencyProbeUpdates++;
        var latency = Math.Clamp(AudioServer.GetOutputLatency(), 0.0, 0.3);
        // The driver can still report 0 (or an inflated value) right at startup;
        // skip the first few samples and finalize quickly after that.
        if (_latencyProbeUpdates > 4 && latency > 0.001)
        {
            _latencyProbeSum += latency;
            _latencyProbeCount++;
        }

        if (_latencyProbeUpdates < 16)
        {
            return;
        }

        _audioLatencySeconds = _latencyProbeCount > 0 ? _latencyProbeSum / _latencyProbeCount : 0.0;
        _audioLatencyMeasured = true;
    }

    /// <summary>
    /// True when this instance owns a decoder. It is false for instances that
    /// Godot recreated from the parameterless constructor after losing the
    /// original managed object.
    /// </summary>
    private bool RequireSource(string operation)
    {
        if (_source != null)
        {
            return true;
        }

        if (!_lostSourceLogged)
        {
            _lostSourceLogged = true;
            Log.Warn($"NovMai FFmpeg: {nameof(FFmpegVideoPlayback)} was recreated by Godot " +
                     $"without decoder state ({operation}); ignoring playback requests.");
        }
        return false;
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
        DetachAudioClock();
        _texture = null;
        _image = null;
        _source?.Dispose();
        _source = null;
    }
}
