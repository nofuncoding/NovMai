using System;
using System.Threading.Tasks;
using Chickensoft.Log;
using Godot;

namespace NovMai.Rendering;

/// <summary>
/// Headless end-to-end probe for the FFmpeg video format. Enabled by
/// <c>NOVMAI_VIDEO_SELFTEST</c>; it opens a video through
/// <see cref="FFmpegVideoStream"/>, plays it through a real
/// <see cref="VideoStreamPlayer"/>, and verifies length, position, texture
/// pixels, seeking and pausing before quitting the process with a status code.
///
/// Optional companion variables:
/// <list type="bullet">
///   <item><c>NOVMAI_VIDEO_SELFTEST_EXPECT_SECONDS</c>: expected container duration.</item>
///   <item><c>NOVMAI_VIDEO_LOADER_TEST</c>: a second path (usually <c>res://</c>)
///     loaded through <see cref="ResourceLoader"/> to verify the registered
///     <see cref="FFmpegResourceFormatLoader"/>.</item>
///   <item><c>NOVMAI_VIDEO_STALL_PROBE=1</c>: blocks the main thread during
///     playback (simulating an editor pause/breakpoint) and verifies that the
///     picture clock catches up with the audio timeline.</item>
///   <item><c>NOVMAI_VIDEO_SLOW_FRAME_PROBE=1</c>: simulates heavy 160 ms frames
///     and verifies that the picture clock still follows the audio timeline
///     instead of falling into slow motion.</item>
/// </list>
/// </summary>
public static class FFmpegVideoSelfTest
{
    private static readonly ILog Log = Logging.Get(nameof(FFmpegVideoSelfTest));

    private const string ProbeBusName = "FFmpegVideoProbe";
    private static AudioEffectCapture _capture;

    private const string ProbeVariable = "NOVMAI_VIDEO_SELFTEST";
    private const string ExpectedSecondsVariable = "NOVMAI_VIDEO_SELFTEST_EXPECT_SECONDS";
    private const string LoaderTestVariable = "NOVMAI_VIDEO_LOADER_TEST";

    public static bool TryGetRequestedPath(out string path)
    {
        path = System.Environment.GetEnvironmentVariable(ProbeVariable);
        return !string.IsNullOrWhiteSpace(path);
    }

    public static async void Run(Node host, string path)
    {
        var failures = 0;
        try
        {
            failures = await RunChecks(host, path);
        }
        catch (Exception error)
        {
            Log.Err($"NovMai: ffmpeg video probe failed with an exception: {error}");
            failures++;
        }

        if (failures == 0)
        {
            Log.Print("NovMai: ffmpeg video probe ok");
        }
        else
        {
            Log.Err($"NovMai: ffmpeg video probe failed: {failures} check(s) failed");
        }
        host.GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private static async Task<int> RunChecks(Node host, string path)
    {
        var failures = 0;
        if (!FFmpegVideoRuntime.Install())
        {
            Fail("native libraries", FFmpegNative.Problem);
            return failures + 1;
        }

        Log.Print($"NovMai: ffmpeg video probe version = {FFmpegNative.Version}");
        Log.Print($"NovMai: ffmpeg video probe root = '{(FFmpegNative.RootPath.Length == 0 ? "<system>" : FFmpegNative.RootPath)}'");

        // 1. Decode through the low-level source first: codec/audio facts and
        //    the first frame are verified without involving the engine.
        var media = FFmpegMediaSource.Open(new GodotFileInput(Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)));
        var hasAudio = media.HasAudio;
        try
        {
            Log.Print($"NovMai: ffmpeg media probe {path}: {media.VideoWidth}x{media.VideoHeight}, " +
                     $"{media.Duration:F3}s @ {media.FrameRate:F2}fps, audio={media.HasAudio} " +
                     $"{media.AudioChannels}ch@{media.AudioSampleRate}Hz tracks={media.AudioTrackCount}");
            if (media.VideoWidth <= 0 || media.VideoHeight <= 0)
            {
                Fail("media dimensions", "no video size");
                failures++;
            }
            if (media.Duration <= 0.1)
            {
                Fail("media duration", $"{media.Duration:F3}s");
                failures++;
            }
            if (!media.TryDequeueVideo(out var rgba, out var width, out var height, out _))
            {
                Fail("media first frame", "no frame decoded");
                failures++;
            }
            else
            {
                var range = ByteRange(rgba, width * height * 4);
                if (range < 8)
                {
                    Fail("media first frame", $"uniform pixels (range {range})");
                    failures++;
                }
            }
        }
        finally
        {
            media.Dispose();
        }

        failures += CheckPlaybackFrames(path);

        var expected = ReadExpectedSeconds();
        var stream = FFmpegVideoStream.Create(path);
        failures += await PlayAndCheck(host, stream, "direct stream", expected, hasAudio);

        var loaderPath = System.Environment.GetEnvironmentVariable(LoaderTestVariable);
        if (!string.IsNullOrWhiteSpace(loaderPath))
        {
            var loaded = ResourceLoader.Load<VideoStream>(loaderPath);
            if (loaded is not FFmpegVideoStream)
            {
                Fail("resource loader", $"{loaderPath} returned {loaded?.GetType().Name ?? "null"}");
                failures++;
            }
            else
            {
                failures += await PlayAndCheck(host, loaded, "resource loader", expected, hasAudio);
            }
        }

        return failures;
    }

    /// <summary>
    /// Drives a playback object directly and checks the frame-to-image path:
    /// the decoded image must contain real pixels and must change over time.
    /// (Headless video textures keep only the image they were created from, so
    /// texture readback alone cannot prove that later frames are uploaded.)
    /// </summary>
    private static int CheckPlaybackFrames(string path)
    {
        var failures = 0;
        var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            Fail("playback file", path);
            return 1;
        }

        var playback = new FFmpegVideoPlayback(file, path);
        try
        {
            playback._Play();
            for (var index = 0; index < 6; index++)
            {
                playback._Update(1.0 / 30.0);
            }
            var first = playback.GetCurrentFrameImage();
            var firstRange = first == null ? 0 : ByteRange(first.GetData(), first.GetWidth() * first.GetHeight() * 4);
            Log.Print($"NovMai: ffmpeg playback frame range = {firstRange}");
            if (first == null || firstRange < 8)
            {
                Fail("playback frame content", first == null ? "no image" : $"uniform pixels (range {firstRange})");
                failures++;
            }

            var firstData = first?.GetData();
            for (var index = 0; index < 40; index++)
            {
                playback._Update(1.0 / 30.0);
            }
            var later = playback.GetCurrentFrameImage();
            var laterData = later?.GetData();
            if (firstData == null || laterData == null || laterData.Length != firstData.Length)
            {
                Fail("playback frame progression", "missing frame data");
                failures++;
            }
            else
            {
                var changed = false;
                for (var index = 0; index < laterData.Length; index += 97)
                {
                    if (laterData[index] != firstData[index])
                    {
                        changed = true;
                        break;
                    }
                }
                if (!changed)
                {
                    Fail("playback frame progression", "image did not change after 40 updates");
                    failures++;
                }
            }
        }
        finally
        {
            playback._Stop();
            playback.Dispose();
        }

        return failures;
    }

    private static async Task<int> PlayAndCheck(Node host, VideoStream stream, string label, double expectedSeconds, bool expectAudio)
    {
        var failures = 0;
        var busIndex = EnsureProbeBus();
        var player = new VideoStreamPlayer
        {
            Stream = stream,
            Expand = true,
            Loop = false,
            Volume = 1.0f,
            Autoplay = false,
            Bus = ProbeBusName,
        };
        host.AddChild(player);

        var length = player.GetStreamLength();
        Log.Print($"NovMai: ffmpeg {label} length = {length:F3}s");
        if (length <= 0.1)
        {
            Fail($"{label} length", $"{length:F3}s");
            failures++;
        }
        if (expectedSeconds > 0.0 && Math.Abs(length - expectedSeconds) > 0.35)
        {
            Fail($"{label} expected length", $"{length:F3}s != {expectedSeconds:F3}s");
            failures++;
        }

        player.Play();
        await WaitSeconds(host, 0.4);

        if (!player.IsPlaying())
        {
            Fail($"{label} is playing", "playback stopped immediately");
            failures++;
        }

        var position = player.GetStreamPosition();
        if (position <= 0.05)
        {
            Fail($"{label} position advanced", $"{position:F3}s");
            failures++;
        }

        var texture = player.GetVideoTexture();
        var image = texture is ImageTexture imageTexture ? imageTexture.GetImage() : null;
        if (image == null || image.IsEmpty())
        {
            Fail($"{label} texture", "no texture image");
            failures++;
        }
        else
        {
            texture = player.GetVideoTexture();
            image = texture is ImageTexture firstTexture ? firstTexture.GetImage() : null;
            var range = image == null ? 0 : ByteRange(image.GetData(), image.GetWidth() * image.GetHeight() * 4);
            Log.Print($"NovMai: ffmpeg {label} texture = {image?.GetWidth()}x{image?.GetHeight()} range={range}");
            if (range < 8)
            {
                Fail($"{label} texture", $"uniform pixels (range {range})");
                failures++;
            }
        }

        // Seek to the middle and confirm the reported position follows.
        var target = Math.Max(0.2, length * 0.5);
        player.StreamPosition = target;
        await WaitSeconds(host, 0.15);
        var seeked = player.GetStreamPosition();
        if (Math.Abs(seeked - target) > 0.4)
        {
            Fail($"{label} seek", $"{seeked:F3}s for target {target:F3}s");
            failures++;
        }
        else
        {
            Log.Print($"NovMai: ffmpeg {label} seek ok = {seeked:F3}s");
        }

        // Pause must freeze the clock; unpause must resume it.
        player.Paused = true;
        await WaitSeconds(host, 0.2);
        var pausedAt = player.GetStreamPosition();
        await WaitSeconds(host, 0.3);
        var stillPausedAt = player.GetStreamPosition();
        if (Math.Abs(stillPausedAt - pausedAt) > 0.05)
        {
            Fail($"{label} pause", $"{pausedAt:F3}s -> {stillPausedAt:F3}s");
            failures++;
        }
        player.Paused = false;
        await WaitSeconds(host, 0.3);
        var resumedAt = player.GetStreamPosition();
        if (resumedAt <= stillPausedAt)
        {
            Fail($"{label} resume", $"{stillPausedAt:F3}s -> {resumedAt:F3}s");
            failures++;
        }

        if (expectAudio)
        {
            failures += await CheckAudio(host, label, busIndex);
        }

        if (expectAudio && label == "direct stream" &&
            System.Environment.GetEnvironmentVariable("NOVMAI_VIDEO_STALL_PROBE") == "1")
        {
            failures += await ProbeExternalStall(host, player, label);
        }

        if (expectAudio && label == "direct stream" &&
            System.Environment.GetEnvironmentVariable("NOVMAI_VIDEO_SLOW_FRAME_PROBE") == "1")
        {
            failures += await ProbeSlowFrames(host, player, label);
        }

        player.Stop();
        player.QueueFree();
        await Frames(host, 2);
        return failures;
    }

    /// <summary>
    /// Simulates a main-thread suspension (editor pause/breakpoint, GC stall) by
    /// blocking the main thread while the audio thread keeps mixing, then reports
    /// how far the playback position advanced versus the audio that leaked.
    /// </summary>
    private static async Task<int> ProbeExternalStall(Node host, VideoStreamPlayer player, string label)
    {
        if (_capture == null)
        {
            return 0;
        }

        await WaitSeconds(host, 0.3);
        var mixRate = Math.Max(1, AudioServer.GetMixRate());
        _capture.ClearBuffer();
        var positionBefore = player.GetStreamPosition();
        var usecBefore = Time.GetTicksUsec();

        // Simulate an editor pause/breakpoint: the main thread blocks while the
        // audio thread keeps mixing (and the audio-server clock keeps running).
        System.Threading.Thread.Sleep(1200);

        var sleepSeconds = (Time.GetTicksUsec() - usecBefore) / 1_000_000.0;
        await WaitSeconds(host, 0.1);
        var positionAfter = player.GetStreamPosition();
        var advanced = positionAfter - positionBefore;

        var capturedFrames = _capture.GetFramesAvailable();
        var buffer = capturedFrames > 0 ? _capture.GetBuffer(capturedFrames) : Array.Empty<Vector2>();
        var mixRateF = Math.Max(1, mixRate);
        const int silenceWindow = 512;
        var silentRun = 0;
        var audibleFrames = buffer.Length;
        var peak = 0.0f;
        for (var index = 0; index < buffer.Length; index++)
        {
            var sample = Math.Max(Math.Abs(buffer[index].X), Math.Abs(buffer[index].Y));
            peak = Math.Max(peak, sample);
            if (sample <= 0.001f)
            {
                silentRun++;
                if (silentRun >= silenceWindow)
                {
                    audibleFrames = index + 1 - silentRun;
                    break;
                }
            }
            else
            {
                silentRun = 0;
            }
        }

        var leakedSeconds = audibleFrames / (double)mixRateF;
        GD.Print($"NovMai: ffmpeg {label} stall probe: advanced={advanced:F3}s leaked={leakedSeconds:F3}s " +
                 $"peak={peak:F3} sleep={sleepSeconds:F3}s frames={capturedFrames}");

        var failures = 0;
        // The picture must jump forward with the audio timeline over the
        // suspension (the old wall-clock code only advanced by the audio that
        // leaked out of the player's resampler).
        if (advanced < sleepSeconds * 0.6 || advanced > sleepSeconds * 1.5 + 0.25)
        {
            Fail($"{label} stall clock", $"video advanced {advanced:F3}s over a {sleepSeconds:F3}s suspension");
            failures++;
        }

        return failures;
    }

    /// <summary>
    /// Simulates a main loop that runs well below 60 fps (160 ms frames) without
    /// any engine-delta correction, as happens when the game scene is heavy. Such
    /// frames are not a suspension: the playback must not move the video clock.
    /// </summary>
    private static async Task<int> ProbeSlowFrames(Node host, VideoStreamPlayer player, string label)
    {
        var resyncsBefore = FFmpegVideoPlayback.ResyncCount;
        var positionBefore = player.GetStreamPosition();
        var usecBefore = Time.GetTicksUsec();
        for (var frame = 0; frame < 10; frame++)
        {
            System.Threading.Thread.Sleep(160);
            await WaitSeconds(host, 0.01);
        }

        var corrections = FFmpegVideoPlayback.ResyncCount - resyncsBefore;
        var advanced = player.GetStreamPosition() - positionBefore;
        var wallSeconds = (Time.GetTicksUsec() - usecBefore) / 1_000_000.0;
        GD.Print($"NovMai: ffmpeg {label} slow-frame probe: advanced={advanced:F3}s wall={wallSeconds:F3}s " +
                 $"resyncs={corrections}");

        var failures = 0;
        if (corrections > 0)
        {
            Fail($"{label} slow-frame resync", $"ordinary slow frames triggered {corrections} resync correction(s)");
            failures++;
        }
        if (advanced < wallSeconds * 0.5)
        {
            Fail($"{label} slow-frame clock", $"video clock advanced only {advanced:F3}s in {wallSeconds:F3}s");
            failures++;
        }
        return failures;
    }

    /// <summary>
    /// Verifies that PCM pushed through <c>MixAudio</c> reaches the audio server:
    /// the probe bus must show a non-zero peak and its AudioEffectCapture ring
    /// must contain audible samples. This works even with the headless Dummy
    /// audio driver.
    /// </summary>
    private static async Task<int> CheckAudio(Node host, string label, int busIndex)
    {
        if (_capture == null)
        {
            return 0;
        }

        _capture.ClearBuffer();
        await WaitSeconds(host, 0.3);

        var framesAvailable = _capture.GetFramesAvailable();
        var buffer = framesAvailable > 0 ? _capture.GetBuffer(Math.Min(framesAvailable, 8192)) : Array.Empty<Vector2>();
        var peak = 0.0f;
        foreach (var sample in buffer)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(sample.X), Math.Abs(sample.Y)));
        }

        var busPeak = AudioServer.GetBusPeakVolumeLeftDb(busIndex, 0);
        Log.Print($"NovMai: ffmpeg {label} audio frames={framesAvailable} peak={peak:F4} busPeak={busPeak:F2}dB");
        if (peak < 0.005f && busPeak < -60.0f)
        {
            Fail($"{label} audio", $"no audible PCM (peak {peak:F4}, bus {busPeak:F2}dB)");
            return 1;
        }
        return 0;
    }

    private static int EnsureProbeBus()
    {
        for (var index = 0; index < AudioServer.GetBusCount(); index++)
        {
            if (AudioServer.GetBusName(index) == ProbeBusName)
            {
                _capture ??= AudioServer.GetBusEffect(index, 0) as AudioEffectCapture;
                return index;
            }
        }

        AudioServer.AddBus();
        var busIndex = AudioServer.GetBusCount() - 1;
        AudioServer.SetBusName(busIndex, ProbeBusName);
        AudioServer.SetBusSend(busIndex, "Master");
        _capture = new AudioEffectCapture { BufferLength = 8.0f };
        AudioServer.AddBusEffect(busIndex, _capture);
        return busIndex;
    }

    private static async Task Frames(Node host, int count)
    {
        var tree = host.GetTree();
        for (var index = 0; index < count; index++)
        {
            await host.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>Waits in real time (the audio-server clock advances with wall time).</summary>
    private static async Task WaitSeconds(Node host, double seconds)
    {
        var timer = host.GetTree().CreateTimer(seconds);
        await host.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    private static double ReadExpectedSeconds()
    {
        var raw = System.Environment.GetEnvironmentVariable(ExpectedSecondsVariable);
        return double.TryParse(raw, out var value) ? value : 0.0;
    }

    private static int ByteRange(byte[] data, int length)
    {
        if (data == null || data.Length == 0)
        {
            return 0;
        }

        var min = 255;
        var max = 0;
        var step = Math.Max(1, Math.Min(length, data.Length) / 4096);
        for (var index = 0; index < Math.Min(length, data.Length); index += step)
        {
            var value = data[index];
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return max - min;
    }

    private static void Fail(string check, string detail)
    {
        Log.Err($"NovMai: ffmpeg video probe check failed [{check}] {detail}");
    }
}
