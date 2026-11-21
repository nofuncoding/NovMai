using System;
using System.Collections.Generic;
using System.Text;
using Cimai;

namespace NovMai.Native;

public enum NoteKind : byte
{
    Tap = 0,
    Hold = 1,
    Touch = 2,
    TouchHold = 3,
    Slide = 4,
}

public enum NoteArea : byte
{
    Key = 0,
    A = 1,
    B = 2,
    C = 3,
    D = 4,
    E = 5,
    None = 255,
}

[Flags]
public enum NoteFlags : uint
{
    None = 0,
    Break = 1 << 0,
    Ex = 1 << 1,
    Mine = 1 << 2,
    Hanabi = 1 << 3,
    Star = 1 << 4,
    StarFakeRotate = 1 << 5,
    SlideNoStarFade = 1 << 6,
    TapHeadSlide = 1 << 7,
}

public enum SlideShape : byte
{
    None = 255,
    Straight = 0,
    CircleLeft = 1,
    CircleRight = 2,
    Corner = 3,
    CurveLeft = 4,
    CurveRight = 5,
    BendLeft = 6,
    BendRight = 7,
    ThunderLeft = 8,
    ThunderRight = 9,
    Fan = 10,
    SkipLeft = 11,
    SkipRight = 12,
}

public readonly struct ChartSummary
{
    public ChartSummary(uint noteCount, uint timingCount, double duration, double bpm)
    {
        NoteCount = noteCount;
        TimingCount = timingCount;
        Duration = duration;
        Bpm = bpm;
    }

    public uint NoteCount { get; }
    public uint TimingCount { get; }
    public double Duration { get; }
    public double Bpm { get; }
}

public readonly struct NoteView
{
    public double Time { get; init; }
    public double Duration { get; init; }
    public double ShootDelay { get; init; }
    public float HSpeed { get; init; }
    public bool IsIgnoreSV { get; init; }
    public NoteFlags Flags { get; init; }
    public uint SpanStart { get; init; }
    public uint SpanEnd { get; init; }
    public short Position { get; init; }
    public short SlideEndPosition { get; init; }
    public NoteKind Kind { get; init; }
    public NoteArea Area { get; init; }
    public SlideShape SlideShape { get; init; }
    public NoteArea SlideEndArea { get; init; }
    public bool IsEach { get; init; }

    public bool IsBreak => (Flags & NoteFlags.Break) != 0;
    public bool IsEx => (Flags & NoteFlags.Ex) != 0;
    public bool IsMine => (Flags & NoteFlags.Mine) != 0;
    public bool IsSlide => Kind == NoteKind.Slide;
    public bool IsHold => Kind is NoteKind.Hold or NoteKind.TouchHold;
}

public readonly struct ChartDiagnostic
{
    public ChartDiagnostic(byte severity, string message, uint spanStart, uint spanEnd)
    {
        Severity = severity;
        Message = message;
        SpanStart = spanStart;
        SpanEnd = spanEnd;
    }

    /// <summary>0 = error, 1 = warning.</summary>
    public byte Severity { get; }
    public string Message { get; }
    public uint SpanStart { get; }
    public uint SpanEnd { get; }

    public bool IsError => Severity == 0;
}

/// <summary>
/// Parses maidata through cimai's C parser. The returned view arrays are managed
/// copies, so no cimai pointers escape this object.
/// </summary>
public sealed unsafe class NovMaiChart : IDisposable
{
    private readonly NoteView[][] _notes = new NoteView[8][];
    private readonly ChartSummary[] _summaries = new ChartSummary[8];
    private readonly bool[] _hasChart = new bool[8];
    private bool _disposed;

    static NovMaiChart()
    {
        NativeLibraryResolver.EnsureInitialized();
    }

    private NovMaiChart()
    {
        for (var index = 0; index < _notes.Length; index++)
        {
            _notes[index] = Array.Empty<NoteView>();
        }
    }

    public static NovMaiChart Parse(string utf8Text)
    {
        var bytes = Encoding.UTF8.GetBytes(utf8Text);
        var result = new NovMaiChart();
        var anyChart = false;

        fixed (byte* data = bytes)
        {
            using var file = Cimai.SimaiFile.Parse(data, bytes.Length);
            for (var level = 0; level < 7; level++)
            {
                var source = file.Charts[level];
                if (source is null)
                {
                    continue;
                }

                var (notes, summary) = MapChart(source, file.Offset);
                result._notes[level + 1] = notes;
                result._summaries[level + 1] = summary;
                result._hasChart[level + 1] = true;
                anyChart = true;
            }
        }

        if (!anyChart)
        {
            throw new InvalidOperationException("cimai: maidata contains no inote_1 .. inote_7 chart");
        }

        return result;
    }

    public ChartSummary GetSummary(byte difficulty)
    {
        ThrowIfDisposed();
        if (difficulty < 1 || difficulty > 7 || !_hasChart[difficulty])
        {
            throw new InvalidOperationException($"Difficulty {difficulty} is not present");
        }
        return _summaries[difficulty];
    }

    public bool TryGetSummary(byte difficulty, out ChartSummary summary)
    {
        summary = default;
        if (_disposed || difficulty < 1 || difficulty > 7 || !_hasChart[difficulty])
        {
            return false;
        }
        summary = _summaries[difficulty];
        return true;
    }

    public NoteView[] GetNotes(byte difficulty)
    {
        ThrowIfDisposed();
        if (difficulty < 1 || difficulty > 7 || !_hasChart[difficulty])
        {
            return Array.Empty<NoteView>();
        }
        return (NoteView[])_notes[difficulty].Clone();
    }

    public ChartDiagnostic[] GetDiagnostics(byte difficulty)
    {
        ThrowIfDisposed();
        return Array.Empty<ChartDiagnostic>();
    }

    private static (NoteView[] Notes, ChartSummary Summary) MapChart(Cimai.SimaiChart source, double chartOffset)
    {
        var timings = source.Timings;
        var nativeNotes = source.Notes;
        var slideParts = source.SlideParts;
        var notes = new List<NoteView>(nativeNotes.Length);
        var bpm = 0.0f;
        var duration = 0.0;

        for (var timingIndex = 0; timingIndex < timings.Length; timingIndex++)
        {
            var timing = timings[timingIndex];
            if (bpm <= 0.0f && timing.Bpm > 0.0f)
            {
                bpm = timing.Bpm;
            }
            duration = Math.Max(duration, timing.Time + chartOffset);

            var first = (int)timing.NotesFirst;
            var count = (int)timing.NotesCount;
            var end = Math.Min(first + count, nativeNotes.Length);
            for (var noteIndex = Math.Max(0, first); noteIndex < end; noteIndex++)
            {
                var native = nativeNotes[noteIndex];
                if (native.Type == SimaiNoteType.NONE)
                {
                    continue;
                }

                var (area, position) = MapPosition(native.StartPos);
                var kind = MapKind(native.Type);
                var endArea = NoteArea.None;
                short endPosition = 0;
                var slideShape = SlideShape.None;

                if (kind == NoteKind.Slide)
                {
                    var partFirst = (int)native.SlidePartsFirst;
                    var partCount = (int)native.SlidePartsCount;
                    if (partCount > 0 && partFirst >= 0 && partFirst < slideParts.Length)
                    {
                        slideShape = MapSlide(slideParts[partFirst].Type);
                        var last = Math.Min(partFirst + partCount - 1, slideParts.Length - 1);
                        (endArea, endPosition) = MapPosition(slideParts[last].End);
                    }
                }

                notes.Add(new NoteView
                {
                    Time = timing.Time + chartOffset,
                    Duration = native.Duration,
                    ShootDelay = native.SlideShootDelay,
                    HSpeed = timing.HSpeed > 0.0f ? timing.HSpeed : 1.0f,
                    IsIgnoreSV = native.IsIgnoreSV,
                    Flags = BuildFlags(native),
                    SpanStart = 0,
                    SpanEnd = 0,
                    Position = position,
                    SlideEndPosition = endPosition,
                    Kind = kind,
                    Area = area,
                    SlideShape = slideShape,
                    SlideEndArea = endArea,
                    IsEach = native.IsEach,
                });

                duration = Math.Max(duration, timing.Time + chartOffset + Math.Max(0.0, native.Duration));
            }
        }

        return (
            notes.ToArray(),
            new ChartSummary(
                (uint)notes.Count,
                (uint)timings.Length,
                duration,
                bpm > 0.0f ? bpm : 120.0));
    }

    private static NoteFlags BuildFlags(SimaiNote note)
    {
        var flags = NoteFlags.None;
        if (note.IsBreak) flags |= NoteFlags.Break;
        if (note.IsEx) flags |= NoteFlags.Ex;
        if (note.IsMine) flags |= NoteFlags.Mine;
        if (note.IsHanabi) flags |= NoteFlags.Hanabi;
        if (note.IsStar) flags |= NoteFlags.Star;
        if (note.IsStarFakeRotate) flags |= NoteFlags.StarFakeRotate;
        if (note.IsSlideNoStarFade) flags |= NoteFlags.SlideNoStarFade;
        return flags;
    }

    private static NoteKind MapKind(SimaiNoteType type) => type switch
    {
        SimaiNoteType.HOLD => NoteKind.Hold,
        SimaiNoteType.SLIDE => NoteKind.Slide,
        SimaiNoteType.TOUCH => NoteKind.Touch,
        SimaiNoteType.TOUCHHOLD => NoteKind.TouchHold,
        _ => NoteKind.Tap,
    };

    private static SlideShape MapSlide(SlideType type) => type switch
    {
        SlideType.STRAIGHT => SlideShape.Straight,
        SlideType.CIRCLE_L => SlideShape.CircleLeft,
        SlideType.CIRCLE_R => SlideShape.CircleRight,
        SlideType.CORNER => SlideShape.Corner,
        SlideType.CURVE_L => SlideShape.CurveLeft,
        SlideType.CURVE_R => SlideShape.CurveRight,
        SlideType.BEND_L => SlideShape.BendLeft,
        SlideType.BEND_R => SlideShape.BendRight,
        SlideType.THUNDER_L => SlideShape.ThunderLeft,
        SlideType.THUNDER_R => SlideShape.ThunderRight,
        SlideType.FAN => SlideShape.Fan,
        SlideType.SKIP_L => SlideShape.SkipLeft,
        SlideType.SKIP_R => SlideShape.SkipRight,
        _ => SlideShape.None,
    };

    private static (NoteArea Area, short Position) MapPosition(SimaiNotePos native)
    {
        var value = (int)native;
        if (value >= (int)SimaiNotePos.BTN0 && value <= (int)SimaiNotePos.BTN9)
        {
            var position = value - (int)SimaiNotePos.BTN0;
            return (NoteArea.Key, (short)Math.Clamp(position == 0 ? 1 : position, 1, 8));
        }
        if (value >= (int)SimaiNotePos.A1 && value <= (int)SimaiNotePos.A8)
        {
            return (NoteArea.A, (short)(value - (int)SimaiNotePos.A1 + 1));
        }
        if (value >= (int)SimaiNotePos.B1 && value <= (int)SimaiNotePos.B8)
        {
            return (NoteArea.B, (short)(value - (int)SimaiNotePos.B1 + 1));
        }
        if (value >= (int)SimaiNotePos.C1 && value <= (int)SimaiNotePos.C2)
        {
            return (NoteArea.C, 0);
        }
        if (value >= (int)SimaiNotePos.D1 && value <= (int)SimaiNotePos.D8)
        {
            return (NoteArea.D, (short)(value - (int)SimaiNotePos.D1 + 1));
        }
        if (value >= (int)SimaiNotePos.E1 && value <= (int)SimaiNotePos.E8)
        {
            return (NoteArea.E, (short)(value - (int)SimaiNotePos.E1 + 1));
        }
        return (NoteArea.None, 0);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NovMaiChart));
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
