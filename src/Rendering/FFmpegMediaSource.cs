using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace NovMai.Rendering;

/// <summary>
/// Demuxes and decodes one media file through FFmpeg, and converts the decoded
/// frames into the two shapes a Godot <c>VideoStreamPlayback</c> needs:
/// RGBA8 video frames and interleaved float32 audio.
///
/// All FFmpeg objects live in unmanaged memory owned by this instance; call
/// <see cref="Dispose"/> once the playback is finished. The class is not
/// thread-safe and is driven from the Godot main thread only.
/// </summary>
public sealed unsafe class FFmpegMediaSource : IDisposable
{
    private static readonly long NoPts = ffmpeg.AV_NOPTS_VALUE;
    private const int SeekBackward = 1;
    private const int Eof = -1;

    private readonly IFFmpegInput _input;
    private readonly FFmpegIoContext _io;
    private readonly List<int> _audioStreamIndices = new();
    private readonly Queue<IntPtr> _videoFrames = new();

    private AVFormatContext* _format;
    private AVStream* _videoStream;
    private AVCodecContext* _videoCodec;
    private AVStream* _audioStream;
    private AVCodecContext* _audioCodec;
    private AVPacket* _packet;
    private AVFrame* _decodedFrame;
    private AVFrame* _pendingVideoFrame;
    private SwsContext* _scaler;
    private SwrContext* _resampler;

    private int _scalerWidth;
    private int _scalerHeight;
    private AVPixelFormat _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private byte[] _rgbaBuffer = Array.Empty<byte>();
    private float[] _audioScratch = Array.Empty<float>();

    private float[] _audioRing = new float[4096];
    private int _audioRead;
    private int _audioCount;

    private double _videoTimeBase;
    private double _videoStartTime;
    private int _videoStreamIndex = -1;
    private double _audioTimeBase;
    private double _audioStartTime;
    private int _audioStreamIndex = -1;
    private double _audioDiscardUntil = double.NegativeInfinity;

    private double _newestVideoTime = double.NegativeInfinity;
    private bool _demuxEof;
    private bool _audioEof;
    private bool _disposed;

    private FFmpegMediaSource(IFFmpegInput input, FFmpegIoContext io)
    {
        _input = input;
        _io = io;
    }

    // ---------------------------------------------------------------- info

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public double Duration { get; private set; }
    public double FrameRate { get; private set; }
    public bool HasVideo => _videoStream != null;
    public bool HasAudio => _audioCodec != null && AudioChannels > 0 && AudioSampleRate > 0;
    public int AudioChannels { get; private set; }
    public int AudioSampleRate { get; private set; }
    public int AudioTrackCount => _audioStreamIndices.Count;
    public int AudioTrack { get; private set; }
    public double LastVideoTime { get; private set; }
    public bool IsDemuxEof => _demuxEof;
    public int VideoQueueCount => _videoFrames.Count + (_pendingVideoFrame != null ? 1 : 0);

    /// <summary>Presentation time of the newest decoded frame that has been queued.</summary>
    public double NewestVideoTime => _newestVideoTime;

    /// <summary>Duration to play; falls back to the last decoded frame when the container has no duration.</summary>
    public double EndTime => Duration > 0.0 ? Duration : Math.Max(0.0, LastVideoTime + 0.1);

    /// <summary>True once every packet and decoded frame has been consumed.</summary>
    public bool IsDrained => _demuxEof && _videoFrames.Count == 0 && _pendingVideoFrame == null && _audioCount == 0;

    /// <summary>True when the audio decoder reached EOF and all pending PCM was consumed.</summary>
    public bool IsAudioDrained => _audioCodec == null || (_audioEof && _audioCount == 0);

    // ------------------------------------------------------------- opening

    /// <summary>
    /// Opens <paramref name="input"/> and decodes the first video frame so a
    /// texture can be created immediately. On success this object owns
    /// <paramref name="input"/>; on failure the caller still owns it.
    /// </summary>
    public static FFmpegMediaSource Open(IFFmpegInput input, int audioTrack = 0)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        if (!FFmpegNative.IsAvailable && !FFmpegNative.TryInitialize())
        {
            throw new FFmpegException(Eof, FFmpegNative.Problem);
        }

        var io = new FFmpegIoContext(input);
        var source = new FFmpegMediaSource(input, io);
        try
        {
            source.Initialize(audioTrack);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void Initialize(int audioTrack)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        _format = ffmpeg.avformat_alloc_context();
        if (_format == null)
        {
            throw new FFmpegException(Eof, "avformat_alloc_context failed.");
        }
        _format->pb = _io.Context;
        // Tell libavformat that the AVIOContext (and its buffer) are owned by us
        // so avformat_close_input() must not close/free it.
        _format->flags |= 0x80;

        var openedFormat = _format;
        var openResult = ffmpeg.avformat_open_input(&openedFormat, string.Empty, null, null);
        _format = openedFormat;
        if (openResult < 0 || _format == null)
        {
            throw new FFmpegException(openResult, "avformat_open_input");
        }

        var infoResult = ffmpeg.avformat_find_stream_info(_format, null);
        if (infoResult < 0)
        {
            throw new FFmpegException(infoResult, "avformat_find_stream_info");
        }

        _packet = ffmpeg.av_packet_alloc();
        _decodedFrame = ffmpeg.av_frame_alloc();
        if (_packet == null || _decodedFrame == null)
        {
            throw new OutOfMemoryException("FFmpeg failed to allocate a packet/frame.");
        }

        OpenVideoStream();

        for (var index = 0; index < _format->nb_streams; index++)
        {
            if (_format->streams[index]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                _audioStreamIndices.Add(index);
            }
        }

        AudioTrack = _audioStreamIndices.Count == 0 ? 0 : Math.Clamp(audioTrack, 0, _audioStreamIndices.Count - 1);
        _audioEof = _audioStreamIndices.Count == 0;
        OpenAudioStream();

        Duration = ReadDuration();
        FrameRate = ReadFrameRate();

        // Decode from the very beginning without seeking: a seek to 0 on a fresh
        // Matroska/Opus demuxer can alter its pre-skip trimming and lose samples.
        PrimeVideoQueue(0.0);
    }

    private void OpenVideoStream()
    {
        var streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (streamIndex < 0)
        {
            throw new FFmpegException(streamIndex, "The file contains no decodable video stream.");
        }

        _videoStreamIndex = streamIndex;
        _videoStream = _format->streams[streamIndex];
        _videoCodec = OpenDecoder(_videoStream);
        _videoTimeBase = ffmpeg.av_q2d(_videoStream->time_base);
        _videoStartTime = _videoStream->start_time == NoPts ? 0.0 : _videoStream->start_time * _videoTimeBase;
        VideoWidth = Math.Max(1, _videoCodec->width);
        VideoHeight = Math.Max(1, _videoCodec->height);
    }

    private AVCodecContext* OpenDecoder(AVStream* stream)
    {
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            throw new FFmpegException(Eof, $"FFmpeg has no decoder for codec id {(int)stream->codecpar->codec_id}.");
        }

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
        {
            throw new OutOfMemoryException("avcodec_alloc_context3 failed.");
        }

        var result = ffmpeg.avcodec_parameters_to_context(context, stream->codecpar);
        if (result < 0)
        {
            var failed = context;
            ffmpeg.avcodec_free_context(&failed);
            throw new FFmpegException(result, "avcodec_parameters_to_context");
        }

        context->pkt_timebase = stream->time_base;
        result = ffmpeg.avcodec_open2(context, codec, null);
        if (result < 0)
        {
            var failed = context;
            ffmpeg.avcodec_free_context(&failed);
            throw new FFmpegException(result, "avcodec_open2");
        }

        return context;
    }

    private void OpenAudioStream()
    {
        CloseAudioStream();
        if (_audioStreamIndices.Count == 0)
        {
            return;
        }

        AudioTrack = Math.Clamp(AudioTrack, 0, _audioStreamIndices.Count - 1);
        _audioStreamIndex = _audioStreamIndices[AudioTrack];
        _audioStream = _format->streams[_audioStreamIndex];
        _audioCodec = OpenDecoder(_audioStream);
        _audioTimeBase = ffmpeg.av_q2d(_audioStream->time_base);
        _audioStartTime = _audioStream->start_time == NoPts ? 0.0 : _audioStream->start_time * _audioTimeBase;

        AudioChannels = _audioCodec->ch_layout.nb_channels;
        AudioSampleRate = _audioCodec->sample_rate;
        if (AudioChannels <= 0 || AudioSampleRate <= 0)
        {
            // A track without usable format info behaves like no audio track.
            CloseAudioStream();
            _audioEof = true;
            return;
        }

        AVChannelLayout inputLayout;
        ffmpeg.av_channel_layout_copy(&inputLayout, &_audioCodec->ch_layout);
        if (inputLayout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC || inputLayout.nb_channels <= 0)
        {
            ffmpeg.av_channel_layout_uninit(&inputLayout);
            ffmpeg.av_channel_layout_default(&inputLayout, AudioChannels);
        }

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, AudioChannels);

        SwrContext* resampler = null;
        var result = ffmpeg.swr_alloc_set_opts2(
            &resampler,
            &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, AudioSampleRate,
            &inputLayout, _audioCodec->sample_fmt, AudioSampleRate,
            0, null);
        if (result < 0 || resampler == null)
        {
            throw new FFmpegException(result, "swr_alloc_set_opts2");
        }

        _resampler = resampler;
        result = ffmpeg.swr_init(_resampler);
        if (result < 0)
        {
            throw new FFmpegException(result, "swr_init");
        }

        _audioEof = false;
    }

    private void CloseAudioStream()
    {
        if (_resampler != null)
        {
            var resampler = _resampler;
            ffmpeg.swr_free(&resampler);
            _resampler = null;
        }
        if (_audioCodec != null)
        {
            var codec = _audioCodec;
            ffmpeg.avcodec_free_context(&codec);
            _audioCodec = null;
        }

        _audioStream = null;
        _audioStreamIndex = -1;
        AudioChannels = 0;
        AudioSampleRate = 0;
        _audioRead = 0;
        _audioCount = 0;
        _audioDiscardUntil = double.NegativeInfinity;
    }

    /// <summary>Selects a 0-based audio track (0 = first audio stream in the container).</summary>
    public void SelectAudioTrack(int index)
    {
        ThrowIfDisposed();
        if (_audioStreamIndices.Count == 0)
        {
            AudioTrack = 0;
            _audioEof = true;
            return;
        }

        var clamped = Math.Clamp(index, 0, _audioStreamIndices.Count - 1);
        if (clamped == AudioTrack && _audioCodec != null)
        {
            return;
        }

        CloseAudioStream();
        AudioTrack = clamped;
        OpenAudioStream();
    }

    private double ReadDuration()
    {
        if (_format->duration > 0 && _format->duration != NoPts)
        {
            return _format->duration / (double)ffmpeg.AV_TIME_BASE;
        }
        if (_videoStream != null && _videoStream->duration > 0)
        {
            return _videoStream->duration * _videoTimeBase;
        }
        return 0.0;
    }

    private double ReadFrameRate()
    {
        if (_videoStream == null)
        {
            return 0.0;
        }

        var rate = ffmpeg.av_guess_frame_rate(_format, _videoStream, null);
        if (rate.num > 0 && rate.den > 0)
        {
            return (double)rate.num / rate.den;
        }
        rate = _videoStream->avg_frame_rate;
        if (rate.num > 0 && rate.den > 0)
        {
            return (double)rate.num / rate.den;
        }
        return 0.0;
    }

    // --------------------------------------------------------------- demux

    /// <summary>
    /// Reads one packet and feeds it to the matching decoder, draining decoded
    /// frames into the internal queues. Returns false once the input is exhausted
    /// (the decoders are flushed before returning).
    /// </summary>
    public bool Pump()
    {
        ThrowIfDisposed();
        if (_demuxEof)
        {
            return false;
        }

        var result = ffmpeg.av_read_frame(_format, _packet);
        if (result < 0)
        {
            _demuxEof = true;
            FlushDecoder(ref _videoCodec);
            FlushDecoder(ref _audioCodec);
            return false;
        }

        try
        {
            if (_videoCodec != null && _packet->stream_index == _videoStreamIndex)
            {
                SendPacket(_videoCodec, _packet, video: true);
            }
            else if (_audioCodec != null && _packet->stream_index == _audioStreamIndex)
            {
                SendPacket(_audioCodec, _packet, video: false);
            }
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }

        return true;
    }

    private void SendPacket(AVCodecContext* codec, AVPacket* packet, bool video)
    {
        var result = ffmpeg.avcodec_send_packet(codec, packet);
        if (result == -ffmpeg.EAGAIN)
        {
            if (video)
            {
                DrainVideo();
            }
            else
            {
                DrainAudio();
            }
            result = ffmpeg.avcodec_send_packet(codec, packet);
        }

        if (result < 0 && result != ffmpeg.AVERROR_EOF)
        {
            throw new FFmpegException(result, video ? "avcodec_send_packet(video)" : "avcodec_send_packet(audio)");
        }

        if (video)
        {
            DrainVideo();
        }
        else
        {
            DrainAudio();
        }
    }

    private void FlushDecoder(ref AVCodecContext* codec)
    {
        if (codec == null)
        {
            return;
        }

        var video = codec == _videoCodec;

        // A flush packet can be refused with EAGAIN when the decoder still holds
        // output frames; drain and retry or the last frame(s) would be dropped.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = ffmpeg.avcodec_send_packet(codec, null);
            if (video)
            {
                DrainVideo();
            }
            else
            {
                DrainAudio();
            }

            if (result != -ffmpeg.EAGAIN)
            {
                break;
            }
        }
    }

    private void DrainVideo()
    {
        if (_videoCodec == null)
        {
            return;
        }

        while (true)
        {
            var result = ffmpeg.avcodec_receive_frame(_videoCodec, _decodedFrame);
            if (result == 0)
            {
                var copy = ffmpeg.av_frame_alloc();
                if (copy == null)
                {
                    ffmpeg.av_frame_unref(_decodedFrame);
                    throw new OutOfMemoryException("av_frame_alloc failed while queueing a video frame.");
                }
                var referenceResult = ffmpeg.av_frame_ref(copy, _decodedFrame);
                ffmpeg.av_frame_unref(_decodedFrame);
                if (referenceResult < 0)
                {
                    var failed = copy;
                    ffmpeg.av_frame_free(&failed);
                    throw new FFmpegException(referenceResult, "av_frame_ref");
                }
                _videoFrames.Enqueue((IntPtr)copy);
                var queuedTime = FrameSeconds(copy, _videoTimeBase, _videoStartTime);
                if (!double.IsNaN(queuedTime))
                {
                    _newestVideoTime = Math.Max(_newestVideoTime, queuedTime);
                }
                continue;
            }

            if (result == ffmpeg.AVERROR_EOF)
            {
                return;
            }
            if (result == -ffmpeg.EAGAIN)
            {
                return;
            }
            throw new FFmpegException(result, "avcodec_receive_frame(video)");
        }
    }

    private void DrainAudio()
    {
        if (_audioCodec == null || _resampler == null)
        {
            return;
        }

        while (true)
        {
            var result = ffmpeg.avcodec_receive_frame(_audioCodec, _decodedFrame);
            if (result == 0)
            {
                ConvertAudioFrame(_decodedFrame);
                ffmpeg.av_frame_unref(_decodedFrame);
                continue;
            }
            if (result == ffmpeg.AVERROR_EOF)
            {
                _audioEof = true;
                return;
            }
            if (result == -ffmpeg.EAGAIN)
            {
                return;
            }
            throw new FFmpegException(result, "avcodec_receive_frame(audio)");
        }
    }

    // ------------------------------------------------------- video frames

    public bool TryPeekVideoTime(out double time)
    {
        if (_pendingVideoFrame != null)
        {
            time = FrameSeconds(_pendingVideoFrame, _videoTimeBase, _videoStartTime);
            return true;
        }
        if (_videoFrames.Count == 0)
        {
            time = 0.0;
            return false;
        }
        time = FrameSeconds((AVFrame*)_videoFrames.Peek(), _videoTimeBase, _videoStartTime);
        return true;
    }

    /// <summary>
    /// Frees decoded frames that are already due while a later due frame exists,
    /// so a catch-up update only converts/uploads the newest due frame. Returns
    /// the number of dropped frames.
    /// </summary>
    public int DropVideoFramesBefore(double deadline)
    {
        ThrowIfDisposed();
        var dropped = 0;
        while (TryPeekSecondVideoTime(out var second) && second <= deadline)
        {
            if (!TryDropVideo(out _))
            {
                break;
            }
            dropped++;
        }
        return dropped;
    }

    private bool TryPeekSecondVideoTime(out double time)
    {
        AVFrame* frame = null;
        if (_pendingVideoFrame != null)
        {
            if (_videoFrames.Count == 0)
            {
                time = 0.0;
                return false;
            }
            frame = (AVFrame*)_videoFrames.Peek();
        }
        else
        {
            if (_videoFrames.Count < 2)
            {
                time = 0.0;
                return false;
            }
            var enumerator = _videoFrames.GetEnumerator();
            enumerator.MoveNext();
            enumerator.MoveNext();
            frame = (AVFrame*)enumerator.Current;
        }

        time = FrameSeconds(frame, _videoTimeBase, _videoStartTime);
        return !double.IsNaN(time);
    }

    private bool TryDropVideo(out double time)
    {
        AVFrame* frame = null;
        if (_pendingVideoFrame != null)
        {
            frame = _pendingVideoFrame;
            _pendingVideoFrame = null;
        }
        else if (_videoFrames.Count > 0)
        {
            frame = (AVFrame*)_videoFrames.Dequeue();
        }

        if (frame == null)
        {
            time = 0.0;
            return false;
        }

        time = FrameSeconds(frame, _videoTimeBase, _videoStartTime);
        if (!double.IsNaN(time))
        {
            LastVideoTime = Math.Max(LastVideoTime, time);
        }
        ffmpeg.av_frame_free(&frame);
        return true;
    }

    /// <summary>
    /// Converts the next decoded frame to RGBA8. The returned array is reused by
    /// the next call; copy it if it must outlive this call.
    /// </summary>
    public bool TryDequeueVideo(out byte[] rgba, out int width, out int height, out double time)
    {
        ThrowIfDisposed();
        AVFrame* frame = null;
        if (_pendingVideoFrame != null)
        {
            frame = _pendingVideoFrame;
            _pendingVideoFrame = null;
        }
        else if (_videoFrames.Count > 0)
        {
            frame = (AVFrame*)_videoFrames.Dequeue();
        }

        if (frame == null)
        {
            rgba = null;
            width = 0;
            height = 0;
            time = 0.0;
            return false;
        }

        try
        {
            width = Math.Max(1, frame->width);
            height = Math.Max(1, frame->height);
            EnsureScaler(width, height, (AVPixelFormat)frame->format);
            EnsureRgbaBuffer(width * height * 4);

            fixed (byte* destination = _rgbaBuffer)
            {
                byte*[] sourceData = frame->data;
                int[] sourceStride = frame->linesize;
                byte*[] destinationData = { destination, null, null, null };
                int[] destinationStride = { width * 4, 0, 0, 0 };
                var scaled = ffmpeg.sws_scale(_scaler, sourceData, sourceStride, 0, height, destinationData, destinationStride);
                if (scaled <= 0)
                {
                    rgba = null;
                    time = 0.0;
                    return false;
                }
            }

            time = FrameSeconds(frame, _videoTimeBase, _videoStartTime);
            if (double.IsNaN(time))
            {
                time = LastVideoTime;
            }
            LastVideoTime = Math.Max(LastVideoTime, time);
            rgba = _rgbaBuffer;
            return true;
        }
        finally
        {
            var freed = frame;
            ffmpeg.av_frame_free(&freed);
        }
    }

    private void EnsureScaler(int width, int height, AVPixelFormat format)
    {
        if (_scaler != null && _scalerWidth == width && _scalerHeight == height && _scalerFormat == format)
        {
            return;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _scaler = ffmpeg.sws_getContext(
            width, height, format,
            width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
            (int)SwsFlags.@SWS_BILINEAR, null, null, null);
        if (_scaler == null)
        {
            throw new FFmpegException(Eof, $"sws_getContext failed for {width}x{height} {format}.");
        }

        _scalerWidth = width;
        _scalerHeight = height;
        _scalerFormat = format;
    }

    private void EnsureRgbaBuffer(int size)
    {
        if (_rgbaBuffer.Length < size)
        {
            _rgbaBuffer = new byte[size];
        }
    }

    // ------------------------------------------------------- audio frames

    public int AudioFramesAvailable => AudioChannels > 0 ? _audioCount / AudioChannels : 0;

    /// <summary>Copies interleaved float samples into <paramref name="destination"/>; returns floats copied.</summary>
    public int PeekAudio(Span<float> destination)
    {
        ThrowIfDisposed();
        var count = Math.Min(destination.Length, _audioCount);
        if (count <= 0)
        {
            return 0;
        }

        var ringLength = _audioRing.Length;
        var first = Math.Min(count, ringLength - _audioRead);
        _audioRing.AsSpan(_audioRead, first).CopyTo(destination);
        if (first < count)
        {
            _audioRing.AsSpan(0, count - first).CopyTo(destination.Slice(first));
        }
        return count;
    }

    /// <summary>Marks <paramref name="frames"/> frames as consumed after a successful MixAudio call.</summary>
    public void ConsumeAudio(int frames)
    {
        if (AudioChannels <= 0 || frames <= 0)
        {
            return;
        }

        var floats = Math.Min(frames * AudioChannels, _audioCount);
        _audioRead = (_audioRead + floats) % _audioRing.Length;
        _audioCount -= floats;
    }

    private void ConvertAudioFrame(AVFrame* frame)
    {
        if (_resampler == null || AudioChannels <= 0)
        {
            return;
        }

        var rate = frame->sample_rate > 0 ? frame->sample_rate : AudioSampleRate;
        var maxOutputFrames = frame->nb_samples + (int)ffmpeg.swr_get_delay(_resampler, rate) + 64;
        var required = Math.Max(1, maxOutputFrames) * AudioChannels;
        if (_audioScratch.Length < required)
        {
            _audioScratch = new float[required];
        }

        fixed (float* output = _audioScratch)
        {
            byte** outputData = stackalloc byte*[1];
            outputData[0] = (byte*)output;

            var produced = ffmpeg.swr_convert(_resampler, outputData, maxOutputFrames, frame->extended_data, frame->nb_samples);
            if (produced < 0)
            {
                throw new FFmpegException(produced, "swr_convert");
            }
            if (produced == 0)
            {
                return;
            }

            var samples = new ReadOnlySpan<float>(output, produced * AudioChannels);
            var time = FrameSeconds(frame, _audioTimeBase, _audioStartTime);
            if (double.IsNaN(time))
            {
                AppendAudio(samples);
                return;
            }

            var frameDuration = produced / (double)Math.Max(1, rate);
            if (time + frameDuration <= _audioDiscardUntil)
            {
                return;
            }

            var skipFrames = 0;
            if (time < _audioDiscardUntil)
            {
                skipFrames = (int)Math.Floor((_audioDiscardUntil - time) * rate);
            }

            _audioDiscardUntil = double.NegativeInfinity;
            if (skipFrames > 0)
            {
                var skipSamples = Math.Min(skipFrames * AudioChannels, samples.Length);
                samples = samples.Slice(skipSamples);
            }
            AppendAudio(samples);
        }
    }

    private void AppendAudio(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var required = _audioCount + samples.Length;
        if (required > _audioRing.Length)
        {
            var replacement = new float[Math.Max(required, _audioRing.Length * 2)];
            var first = Math.Min(_audioCount, _audioRing.Length - _audioRead);
            _audioRing.AsSpan(_audioRead, first).CopyTo(replacement);
            if (first < _audioCount)
            {
                _audioRing.AsSpan(0, _audioCount - first).CopyTo(replacement.AsSpan(first));
            }
            _audioRing = replacement;
            _audioRead = 0;
        }

        var write = (_audioRead + _audioCount) % _audioRing.Length;
        var head = Math.Min(samples.Length, _audioRing.Length - write);
        samples.Slice(0, head).CopyTo(_audioRing.AsSpan(write));
        if (head < samples.Length)
        {
            samples.Slice(head).CopyTo(_audioRing.AsSpan(0));
        }
        _audioCount += samples.Length;
    }

    // ------------------------------------------------------------- seeking

    /// <summary>Seeks to <paramref name="seconds"/> and primes the first due frame.</summary>
    public void Seek(double seconds)
    {
        ThrowIfDisposed();
        if (_format == null || _videoStream == null)
        {
            return;
        }

        var limit = Duration > 0.0 ? Duration : seconds;
        var target = Math.Clamp(seconds, 0.0, Math.Max(0.0, limit));

        ClearQueues();
        _demuxEof = false;
        _audioEof = _audioCodec == null;
        _audioDiscardUntil = target;

        if (_videoCodec != null)
        {
            ffmpeg.avcodec_flush_buffers(_videoCodec);
        }
        if (_audioCodec != null)
        {
            ffmpeg.avcodec_flush_buffers(_audioCodec);
        }
        if (_resampler != null)
        {
            ffmpeg.swr_convert(_resampler, null, 0, null, 0);
        }

        var timestamp = (long)Math.Round((target + _videoStartTime) / _videoTimeBase);
        var result = ffmpeg.avformat_seek_file(_format, _videoStreamIndex, long.MinValue, timestamp, long.MaxValue, SeekBackward);
        if (result < 0)
        {
            ffmpeg.av_seek_frame(_format, -1, (long)(target * ffmpeg.AV_TIME_BASE), SeekBackward);
        }
        ffmpeg.avformat_flush(_format);

        PrimeVideoQueue(target);
    }

    private void PrimeVideoQueue(double target)
    {
        const double epsilon = 0.002;
        AVFrame* lastBefore = null;
        var guard = 0;

        while (guard++ < 20000)
        {
            if (_videoFrames.Count > 0)
            {
                var frame = (AVFrame*)_videoFrames.Peek();
                var time = FrameSeconds(frame, _videoTimeBase, _videoStartTime);
                if (double.IsNaN(time) || time >= target - epsilon)
                {
                    break;
                }

                _videoFrames.Dequeue();
                if (lastBefore != null)
                {
                    var stale = lastBefore;
                    ffmpeg.av_frame_free(&stale);
                }
                lastBefore = frame;
                continue;
            }

            if (_demuxEof || !Pump())
            {
                break;
            }
        }

        if (_videoFrames.Count == 0 && lastBefore != null)
        {
            _pendingVideoFrame = lastBefore;
            lastBefore = null;
        }

        if (lastBefore != null)
        {
            var stale = lastBefore;
            ffmpeg.av_frame_free(&stale);
        }
    }

    // ------------------------------------------------------------- helpers

    private void ClearQueues()
    {
        while (_videoFrames.Count > 0)
        {
            var frame = (AVFrame*)_videoFrames.Dequeue();
            ffmpeg.av_frame_free(&frame);
        }
        if (_pendingVideoFrame != null)
        {
            var frame = _pendingVideoFrame;
            _pendingVideoFrame = null;
            ffmpeg.av_frame_free(&frame);
        }
        _audioRead = 0;
        _audioCount = 0;
        _newestVideoTime = double.NegativeInfinity;
    }

    private static double FrameSeconds(AVFrame* frame, double timeBase, double startTime)
    {
        var pts = frame->best_effort_timestamp;
        if (pts == NoPts)
        {
            pts = frame->pts;
        }
        if (pts == NoPts)
        {
            return double.NaN;
        }
        return pts * timeBase - startTime;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FFmpegMediaSource));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        ClearQueues();

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        CloseAudioStream();

        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
        }
        if (_decodedFrame != null)
        {
            var decodedFrame = _decodedFrame;
            ffmpeg.av_frame_free(&decodedFrame);
        }

        if (_videoCodec != null)
        {
            var codec = _videoCodec;
            ffmpeg.avcodec_free_context(&codec);
            _videoCodec = null;
        }

        if (_format != null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        _io.Dispose();
        _input.Dispose();
    }
}

/// <summary>FFmpeg error code paired with a readable message.</summary>
public sealed unsafe class FFmpegException : Exception
{
    public FFmpegException(int errorCode, string operation)
        : this(errorCode, operation, null)
    {
    }

    private FFmpegException(int errorCode, string operation, string extra)
        : base(BuildMessage(errorCode, operation, extra))
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }

    private static string BuildMessage(int errorCode, string operation, string extra)
    {
        var text = Describe(errorCode);
        var message = errorCode < 0
            ? $"{operation} failed with FFmpeg error {errorCode} ({text})."
            : $"{operation}: {text}.";
        return string.IsNullOrEmpty(extra) ? message : $"{message} {extra}";
    }

    /// <summary>Formats an FFmpeg error code without requiring an initialized FFmpeg instance.</summary>
    public static string Describe(int errorCode)
    {
        if (errorCode >= 0)
        {
            return errorCode.ToString();
        }

        try
        {
            var buffer = stackalloc byte[256];
            var result = ffmpeg.av_strerror(errorCode, buffer, 256);
            if (result < 0)
            {
                return errorCode.ToString();
            }
            var text = Marshal.PtrToStringUTF8((IntPtr)buffer);
            return string.IsNullOrEmpty(text) ? errorCode.ToString() : text;
        }
        catch (Exception)
        {
            return errorCode.ToString();
        }
    }
}
