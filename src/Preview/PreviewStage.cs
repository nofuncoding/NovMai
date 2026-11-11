using System;
using System.Collections.Generic;
using Godot;
using NovMai.Assets;
using NovMai.Native;

namespace NovMai.Preview;

/// <summary>
/// ViewX-inspired sprite playfield. The geometry is driven by the same note
/// time/distance model as MajdataViewX's note update jobs, while each visual is
/// still a regular Godot 2D Sprite2D/Line2D for a small editable preview.
/// </summary>
public partial class PreviewStage : Node2D
{
    public const float RingRadius = 4.8f;
    public const float InnerRadius = 1.225f;

    public float TapSpeed { get; set; } = 7.5f;
    public float TouchSpeed { get; set; } = 7.5f;
    public float AppearTime { get; set; } = 2.0f;

    private bool _initialized;
    private Sprite2D _ring;
    private Node2D _notesRoot;
    private readonly List<NoteVisual> _visuals = new();

    private Texture2D _tap;
    private Texture2D _tapEach;
    private Texture2D _tapBreak;
    private Texture2D _tapMine;
    private Texture2D _tapEx;

    private Texture2D _hold;
    private Texture2D _holdEach;
    private Texture2D _holdBreak;
    private Texture2D _holdMine;
    private Texture2D _holdEnd;
    private Texture2D _guideLineTexture;

    private Texture2D _slide;
    private Texture2D _slideEach;
    private Texture2D _slideBreak;
    private Texture2D _slideMine;
    private Texture2D _slideFallback;
    private Texture2D _star;
    private Texture2D _starEach;
    private Texture2D _starBreak;
    private Texture2D _starMine;

    private Texture2D _touch;
    private Texture2D _touchEach;
    private Texture2D _touchBreak;
    private Texture2D _touchMine;
    private Texture2D _touchPoint;
    private Texture2D _touchJust;
    private Texture2D _touchFanFallback;
    private Texture2D _touchPointFallback;

    private Texture2D _touchHold;
    private Texture2D _touchHoldEach;
    private Texture2D _touchHoldBreak;
    private Texture2D _touchHoldMine;
    private Texture2D _touchHoldBorder;

    private bool _tapExternal;
    private bool _holdExternal;
    private bool _touchExternal;
    private bool _touchHoldExternal;
    private bool _slideExternal;

    public override void _Ready()
    {
        EnsureInitialized();
    }

    public void SetChart(NoteView[] notes)
    {
        EnsureInitialized();

        if (_notesRoot != null)
        {
            _notesRoot.QueueFree();
        }
        _notesRoot = new Node2D { Name = "Notes" };
        AddChild(_notesRoot);
        _visuals.Clear();

        if (notes == null)
        {
            return;
        }

        foreach (var note in notes)
        {
            if (note.Area == NoteArea.None)
            {
                continue;
            }

            switch (note.Kind)
            {
                case NoteKind.Hold:
                    CreateHold(note);
                    break;
                case NoteKind.Slide:
                    CreateSlide(note);
                    break;
                case NoteKind.Touch:
                case NoteKind.TouchHold:
                    CreateTouch(note);
                    break;
                default:
                    CreateTap(note);
                    break;
            }
        }
    }

    public void Sync(double time)
    {
        if (!_initialized)
        {
            return;
        }

        foreach (var visual in _visuals)
        {
            var note = visual.Note;
            switch (note.Kind)
            {
                case NoteKind.Hold:
                    UpdateHold(visual, time);
                    break;
                case NoteKind.Slide:
                    UpdateSlide(visual, time);
                    break;
                case NoteKind.Touch:
                case NoteKind.TouchHold:
                    UpdateTouch(visual, time);
                    break;
                default:
                    UpdateTap(visual, time);
                    break;
            }
        }
    }

    private void CreateTap(NoteView note)
    {
        var visual = new NoteVisual(note)
        {
            Head = CreateSprite(TextureFor(note), 2),
            Guide = CreateGuide(),
        };
        _notesRoot.AddChild(visual.Guide);
        _notesRoot.AddChild(visual.Head);
        _visuals.Add(visual);
    }

    private void CreateHold(NoteView note)
    {
        var visual = new NoteVisual(note)
        {
            Body = CreateSprite(TextureFor(note), 0),
            End = CreateSprite(_holdEnd, 1),
            Guide = CreateGuide(),
        };
        _notesRoot.AddChild(visual.Guide);
        _notesRoot.AddChild(visual.Body);
        _notesRoot.AddChild(visual.End);
        _visuals.Add(visual);
    }

    private void CreateSlide(NoteView note)
    {
        var path = BuildSlidePath(note, out var lengths, out var totalLength);
        var visual = new NoteVisual(note)
        {
            Path = path,
            PathLengths = lengths,
            PathTotalLength = totalLength,
            Star = CreateSprite(_star, 3),
        };
        visual.Star.Texture = TextureForStar(note);

        if (totalLength > 0.001f)
        {
            var distance = 0.0f;
            var arrowTexture = TextureFor(note);
            while (distance < totalLength - 0.2f)
            {
                var point = SamplePath(visual, distance / totalLength, out var tangent);
                if (distance > 0.35f)
                {
                    var arrow = CreateSprite(arrowTexture, -2);
                    arrow.Position = point;
                    arrow.Rotation = Mathf.Atan2(tangent.Y, tangent.X) + Mathf.Pi;
                    arrow.Scale = Vector2.One * (0.72f / TextureSize(arrow.Texture));
                    _notesRoot.AddChild(arrow);
                    visual.Arrows.Add(arrow);
                }
                distance += 0.47f;
            }
        }

        _notesRoot.AddChild(visual.Star);
        _visuals.Add(visual);
    }

    private void CreateTouch(NoteView note)
    {
        var visual = new NoteVisual(note)
        {
            Fans = new Sprite2D[4],
            Point = CreateSprite(_touchPoint, 1),
            Just = CreateSprite(_touchJust, 2),
        };
        if (note.Kind == NoteKind.TouchHold)
        {
            visual.Border = CreateSprite(_touchHoldBorder, 0);
            _notesRoot.AddChild(visual.Border);
        }
        for (var index = 0; index < 4; index++)
        {
            visual.Fans[index] = CreateSprite(TextureForTouch(note, index), 1);
            _notesRoot.AddChild(visual.Fans[index]);
        }
        _notesRoot.AddChild(visual.Point);
        _notesRoot.AddChild(visual.Just);
        _visuals.Add(visual);
    }

    private void UpdateTap(NoteVisual visual, double time)
    {
        var note = visual.Note;
        var timing = (float)(time - note.Time);
        var speed = TapSpeed * Math.Max(0.01f, note.HSpeed);
        var rawDistance = timing * speed + RingRadius;
        var clampedDistance = Math.Max(rawDistance, InnerRadius);
        var scale = Math.Min(rawDistance * 0.4f + 0.51f, 1.0f);
        if (scale <= 0.0f || timing > 0.1f)
        {
            Hide(visual);
            return;
        }

        visual.Head.Visible = true;
        visual.Head.Position = Polar(note.Position, Math.Min(clampedDistance, RingRadius));
        visual.Head.Rotation = PolarAngle(note.Position);
        visual.Head.Scale = Vector2.One * (1.2f * scale / TextureSize(visual.Head.Texture));
        visual.Head.Modulate = TintFor(note, scale);

        var lineScale = Math.Min(clampedDistance / RingRadius, 1.0f);
        visual.Guide.Visible = scale > 0.3f;
        visual.Guide.SetPointPosition(0, Vector2.Zero);
        visual.Guide.SetPointPosition(1, Polar(note.Position, RingRadius * lineScale));
        visual.Guide.DefaultColor = new Color(1, 1, 1, 0.07f * scale);
    }

    private void UpdateHold(NoteVisual visual, double time)
    {
        var note = visual.Note;
        var timing = (float)(time - note.Time);
        var duration = (float)Math.Max(0.0, note.Duration);
        var speed = TapSpeed * Math.Max(0.01f, note.HSpeed);
        var headDistance = timing * speed + RingRadius;
        var tailTiming = timing - duration;
        var tailDistance = tailTiming * speed + RingRadius;
        if (tailTiming > 0.35f || headDistance < -1.275f)
        {
            Hide(visual);
            return;
        }

        float headClamped = Math.Min(headDistance, RingRadius);
        float tailClamped = Mathf.Clamp(tailDistance, InnerRadius, RingRadius);
        float barLength = Math.Max(headClamped - tailClamped, 0.0f);
        float mid = (headClamped + tailClamped) * 0.5f;
        var angle = PolarAngle(note.Position);
        var destination = Polar(note.Position, mid);
        const float width = 1.22f;
        float height = Math.Max(0.2f, barLength + 1.4f);

        visual.Body.Visible = barLength > 0.01f;
        visual.Body.Position = destination;
        visual.Body.Rotation = angle - Mathf.Pi * 0.5f;
        visual.Body.Scale = new Vector2(
            width / Mathf.Max(1, visual.Body.Texture.GetWidth()),
            height / Mathf.Max(1, visual.Body.Texture.GetHeight()));
        visual.Body.Modulate = TintFor(note, 0.95f);

        visual.End.Visible = tailDistance >= InnerRadius;
        visual.End.Position = Polar(note.Position, (float)tailClamped);
        visual.End.Rotation = angle;
        visual.End.Scale = Vector2.One * (0.95f / TextureSize(visual.End.Texture));
        visual.End.Modulate = TintFor(note, 1.0f);

        var lineScale = Math.Min(Math.Max(headDistance, InnerRadius) / RingRadius, 1.0f);
        visual.Guide.Visible = headDistance > 1.9f;
        visual.Guide.SetPointPosition(0, Vector2.Zero);
        visual.Guide.SetPointPosition(1, Polar(note.Position, RingRadius * lineScale));
        visual.Guide.DefaultColor = new Color(1, 1, 1, 0.18f);
    }

    private void UpdateSlide(NoteVisual visual, double time)
    {
        var note = visual.Note;
        var duration = Math.Max(0.001f, note.Duration);
        var shootTime = note.Time + note.ShootDelay;
        var tapTiming = (float)(time - note.Time);
        var timing = (float)(time - shootTime);
        var process = Mathf.Clamp((float)(timing / duration), 0.0f, 1.0f);

        var visible = tapTiming >= -0.35f && timing <= duration + 0.35f;
        if (!visible)
        {
            Hide(visual);
            return;
        }

        var alpha = tapTiming < 0.0f
            ? Mathf.Clamp(1.0f + tapTiming / 0.35f, 0.0f, 1.0f)
            : Mathf.Clamp(1.0f - Math.Max((float)timing - (float)duration, 0.0f) / 0.35f, 0.0f, 1.0f);

        visual.Star.Visible = alpha > 0.01f;
        var starPosition = SamplePath(visual, process, out var starTangent);
        visual.Star.Position = starPosition;
        visual.Star.Rotation = Mathf.Atan2(starTangent.Y, starTangent.X) - Mathf.Pi * 0.5f;
        visual.Star.Scale = Vector2.One * (0.9f / TextureSize(visual.Star.Texture));
        visual.Star.Modulate = TintFor(note, alpha);

        for (var index = 0; index < visual.Arrows.Count; index++)
        {
            var arrow = visual.Arrows[index];
            var arrowT = (index + 1) / (float)(visual.Arrows.Count + 1);
            arrow.Visible = alpha > 0.01f && arrowT >= process - 0.02f;
            arrow.Modulate = TintFor(note, alpha * 0.8f);
        }
    }

    private void UpdateTouch(NoteVisual visual, double time)
    {
        var note = visual.Note;
        var timing = (float)(time - note.Time);
        var speed = TouchSpeed * Math.Max(0.01f, note.HSpeed);
        var wholeDuration = 3.209385682f * Mathf.Pow(Math.Max(0.01f, speed), -0.9549621752f);
        var moveDuration = 0.8f * wholeDuration;
        var displayDuration = 0.2f * wholeDuration;
        if (-timing > wholeDuration || timing > 0.4f)
        {
            Hide(visual);
            return;
        }

        var fanDistance = Mathf.Clamp(
            -Mathf.Exp(8.0f * (timing * 0.43f / moveDuration) - 0.85f) + 0.42f,
            0.0f,
            0.4f);
        var alpha = 1.0f;
        if (-timing >= moveDuration)
        {
            alpha = Mathf.Clamp(1.0f - (-timing - moveDuration) / displayDuration, 0.0f, 1.0f);
            fanDistance = 0.4f;
        }

        var center = AreaPosition(note.Area, note.Position);
        var radius = 0.226f + fanDistance;
        var diagonal = radius * 0.70710678f;
        var positions = note.Kind == NoteKind.TouchHold
            ? new[]
            {
                center + new Vector2(diagonal, diagonal),
                center + new Vector2(diagonal, -diagonal),
                center + new Vector2(-diagonal, -diagonal),
                center + new Vector2(-diagonal, diagonal),
            }
            : new[]
            {
                center + new Vector2(radius, 0),
                center + new Vector2(0, radius),
                center + new Vector2(-radius, 0),
                center + new Vector2(0, -radius),
            };

        for (var index = 0; index < 4; index++)
        {
            var fan = visual.Fans[index];
            fan.Visible = alpha > 0.01f;
            fan.Position = positions[index];
            fan.Rotation = Mathf.DegToRad(180.0f + 90.0f * (index + 1));
            fan.Scale = Vector2.One * (0.8f / TextureSize(fan.Texture));
            var color = TintFor(note, alpha);
            color.A *= 0.9f;
            fan.Modulate = color;
        }

        visual.Point.Visible = alpha > 0.01f;
        visual.Point.Position = center;
        visual.Point.Scale = Vector2.One * (0.7f / TextureSize(visual.Point.Texture));
        visual.Point.Modulate = TintFor(note, alpha);

        visual.Just.Visible = timing > -0.02f && alpha > 0.01f;
        visual.Just.Position = center;
        visual.Just.Scale = Vector2.One * (0.75f / TextureSize(visual.Just.Texture));
        visual.Just.Modulate = TintFor(note, alpha);

        if (visual.Border != null)
        {
            visual.Border.Visible = alpha > 0.01f;
            visual.Border.Position = center;
            visual.Border.Scale = Vector2.One * (1.5f / TextureSize(visual.Border.Texture));
            var borderColor = TintFor(note, alpha);
            borderColor.A *= 0.65f;
            visual.Border.Modulate = borderColor;
        }
    }

    private static void Hide(NoteVisual visual)
    {
        if (visual.Head != null) visual.Head.Visible = false;
        if (visual.Body != null) visual.Body.Visible = false;
        if (visual.End != null) visual.End.Visible = false;
        if (visual.Guide != null) visual.Guide.Visible = false;
        if (visual.Star != null) visual.Star.Visible = false;
        foreach (var arrow in visual.Arrows) arrow.Visible = false;
        if (visual.Fans != null)
        {
            foreach (var fan in visual.Fans) fan.Visible = false;
        }
        if (visual.Point != null) visual.Point.Visible = false;
        if (visual.Just != null) visual.Just.Visible = false;
        if (visual.Border != null) visual.Border.Visible = false;
    }

    private Texture2D TextureFor(NoteView note)
    {
        if (note.Kind == NoteKind.Tap)
        {
            if (note.IsMine) return note.IsBreak ? _tapMine : _tapMine;
            if (note.IsBreak) return _tapBreak;
            if (note.IsEach) return _tapEach;
            if (note.IsEx) return _tapEx;
            return _tap;
        }
        if (note.Kind == NoteKind.Hold)
        {
            if (note.IsMine) return _holdMine;
            if (note.IsBreak) return _holdBreak;
            if (note.IsEach) return _holdEach;
            return _hold;
        }
        if (note.Kind == NoteKind.Slide)
        {
            if (note.IsMine) return _slideMine;
            if (note.IsBreak) return _slideBreak;
            if (note.IsEach) return _slideEach;
            return _slide;
        }
        return _touch;
    }

    private Texture2D TextureForStar(NoteView note)
    {
        if (note.IsMine) return _starMine;
        if (note.IsBreak) return _starBreak;
        if (note.IsEach) return _starEach;
        return _star;
    }

    private Texture2D TextureForTouch(NoteView note, int index)
    {
        if (note.Kind == NoteKind.TouchHold)
        {
            if (note.IsMine) return _touchHoldMine;
            if (note.IsBreak) return _touchHoldBreak;
            if (note.IsEach) return _touchHoldEach;
            return _touchHold;
        }
        if (note.IsMine) return _touchMine;
        if (note.IsBreak) return _touchBreak;
        if (note.IsEach) return _touchEach;
        return _touch;
    }

    private Color TintFor(NoteView note, float alpha)
    {
        var external = note.Kind switch
        {
            NoteKind.Tap => _tapExternal,
            NoteKind.Hold => _holdExternal,
            NoteKind.Touch => _touchExternal,
            NoteKind.TouchHold => _touchHoldExternal,
            NoteKind.Slide => _slideExternal,
            _ => false,
        };
        var color = external ? Colors.White : ColorFor(note);
        color.A *= alpha;
        return color;
    }

    private static Color ColorFor(NoteView note)
    {
        if (note.IsMine) return new Color(0.8f, 0.2f, 0.8f);
        if (note.IsBreak) return new Color(1.0f, 0.75f, 0.25f);
        return note.Kind switch
        {
            NoteKind.Hold => new Color(0.25f, 0.85f, 1.0f),
            NoteKind.Touch => new Color(1.0f, 0.85f, 0.35f),
            NoteKind.TouchHold => new Color(1.0f, 0.55f, 0.25f),
            NoteKind.Slide => new Color(1.0f, 0.45f, 0.75f),
            _ => new Color(0.9f, 0.95f, 1.0f),
        };
    }

    private Sprite2D CreateSprite(Texture2D texture, int zIndex)
    {
        return new Sprite2D
        {
            Texture = texture,
            Centered = true,
            ZIndex = zIndex,
        };
    }

    private Line2D CreateGuide()
    {
        var line = new Line2D
        {
            Width = 0.035f,
            DefaultColor = new Color(1, 1, 1, 0.18f),
            Antialiased = true,
            ZIndex = -4,
        };
        line.AddPoint(Vector2.Zero);
        line.AddPoint(Vector2.Zero);
        return line;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        _ring = new Sprite2D
        {
            Texture = MakeRingTexture(512),
            Centered = true,
            ZIndex = -20,
        };
        _ring.Scale = Vector2.One * (RingRadius / (512 * 0.48f));
        AddChild(_ring);

        LoadTextures();
        _notesRoot = new Node2D { Name = "Notes" };
        AddChild(_notesRoot);
    }

    private void LoadTextures()
    {
        var tapFallback = MakeDiscTexture(128, 0.56f);
        _tap = Load("TapSkins/tap.png", tapFallback);
        _tapEach = Load("TapSkins/tap_each.png", _tap);
        _tapBreak = Load("TapSkins/tap_break.png", _tap);
        _tapMine = Load("TapSkins/tap_mine.png", _tap);
        _tapEx = Load("TapSkins/tap_ex.png", _tap);
        _tapExternal = _tap != tapFallback;

        var holdFallback = MakeHoldBodyTexture(122, 200);
        _hold = Load("HoldSkins/hold.png", holdFallback);
        _holdEach = Load("HoldSkins/hold_each.png", _hold);
        _holdBreak = Load("HoldSkins/hold_break.png", _hold);
        _holdMine = Load("HoldSkins/hold_mine.png", _hold);
        _holdEnd = Load("NoteGuideSkins/Hold_End.png", MakeDiscTexture(128, 0.45f));
        _holdExternal = _hold != holdFallback;

        var slideFallback = MakeArrowTexture(128);
        _slide = Load("SlideSkins/slide.png", slideFallback);
        _slideEach = Load("SlideSkins/slide_each.png", _slide);
        _slideBreak = Load("SlideSkins/slide_break.png", _slide);
        _slideMine = Load("SlideSkins/slide_mine.png", _slide);
        _slideExternal = _slide != slideFallback;

        _star = Load("StarSkins/star.png", MakeStarTexture(128));
        _starEach = Load("StarSkins/star_each.png", _star);
        _starBreak = Load("StarSkins/star_break.png", _star);
        _starMine = Load("StarSkins/star_mine.png", _star);

        _touchFanFallback = MakeFanTexture(128);
        _touchPointFallback = MakeDiscTexture(64, 0.8f);
        _touch = Load("TouchSkins/touch.png", _touchFanFallback);
        _touchEach = Load("TouchSkins/touch_each.png", _touch);
        _touchBreak = Load("TouchSkins/touch_break.png", _touch);
        _touchMine = Load("TouchSkins/touch_mine.png", _touch);
        _touchPoint = Load("TouchSkins/touch_point.png", _touchPointFallback);
        _touchJust = Load("TouchSkins/touch_just.png", MakeDiscTexture(64, 0.9f));
        _touchExternal = _touch != _touchFanFallback;

        _touchHold = Load("TouchHoldSkins/touchhold_0.png", _touchFanFallback);
        _touchHoldEach = Load("TouchHoldSkins/touchhold_0.png", _touchHold);
        _touchHoldBreak = Load("TouchHoldSkins/touchhold_break_0.png", _touchHold);
        _touchHoldMine = Load("TouchHoldSkins/touchhold_mine_0.png", _touchHold);
        _touchHoldBorder = Load("TouchHoldSkins/touchhold_border.png", _touchPointFallback);
        _touchHoldExternal = _touchHold != _touchFanFallback;
    }

    private static Texture2D Load(string path, Texture2D fallback)
    {
        return ExternalAssets.TryLoadTexture(path) ?? fallback;
    }

    private Vector2[] BuildSlidePath(NoteView note, out float[] lengths, out float totalLength)
    {
        Vector2[] points;
        if (note.SlideShape is SlideShape.CircleLeft or SlideShape.CircleRight &&
            !(note.Area == NoteArea.C && note.Position == 0) &&
            !(note.SlideEndArea == NoteArea.C && note.SlideEndPosition == 0))
        {
            var start = PolarAngle(note.Position);
            var end = PolarAngle(note.SlideEndPosition);
            var sweep = end - start;
            if (note.SlideShape == SlideShape.CircleRight)
            {
                while (sweep <= 0.0f) sweep += Mathf.Tau;
            }
            else
            {
                while (sweep >= 0.0f) sweep -= Mathf.Tau;
            }
            points = new Vector2[65];
            for (var index = 0; index < points.Length; index++)
            {
                var angle = start + sweep * index / (points.Length - 1);
                points[index] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * RingRadius;
            }
        }
        else
        {
            points = new[]
            {
                RingPoint(note.Area, note.Position, RingRadius),
                RingPoint(note.SlideEndArea, note.SlideEndPosition, RingRadius),
            };
        }

        lengths = new float[points.Length];
        for (var index = 1; index < points.Length; index++)
        {
            lengths[index] = lengths[index - 1] + points[index].DistanceTo(points[index - 1]);
        }
        totalLength = lengths[^1];
        return points;
    }

    private static Vector2 SamplePath(NoteVisual visual, float t, out Vector2 tangent)
    {
        var path = visual.Path;
        if (path == null || path.Length < 2)
        {
            tangent = Vector2.Right;
            return Vector2.Zero;
        }
        var target = Mathf.Clamp(t, 0.0f, 1.0f) * visual.PathTotalLength;
        for (var index = 1; index < path.Length; index++)
        {
            if (visual.PathLengths[index] < target && index < path.Length - 1)
            {
                continue;
            }
            var span = Math.Max(0.0001f, visual.PathLengths[index] - visual.PathLengths[index - 1]);
            var local = Mathf.Clamp((target - visual.PathLengths[index - 1]) / span, 0.0f, 1.0f);
            tangent = (path[index] - path[index - 1]).Normalized();
            return path[index - 1].Lerp(path[index], local);
        }
        tangent = (path[^1] - path[^2]).Normalized();
        return path[^1];
    }

    private static Vector2 AreaPosition(NoteArea area, int position)
    {
        if (area == NoteArea.C)
        {
            return Vector2.Zero;
        }
        var radius = area switch
        {
            NoteArea.A => 4.1f,
            NoteArea.B => 2.2f,
            NoteArea.D => 4.1f,
            NoteArea.E => 3.1f,
            _ => RingRadius,
        };
        if (area is NoteArea.D or NoteArea.E)
        {
            var angle = -Mathf.DegToRad((6.0f - 2.0f * position) * 22.5f);
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        return Polar(position, radius);
    }

    private static Vector2 RingPoint(NoteArea area, int position, float radius)
    {
        if (area == NoteArea.C)
        {
            return Vector2.Zero;
        }
        return Polar(position, radius);
    }

    private static Vector2 Polar(int position, float radius)
    {
        var angle = PolarAngle(position);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    private static float PolarAngle(int position)
    {
        return -Mathf.DegToRad(67.5f - 45.0f * (position - 1));
    }

    private static float TextureSize(Texture2D texture)
    {
        return Mathf.Max(1, Mathf.Max(texture.GetWidth(), texture.GetHeight()));
    }

    private static Texture2D MakeDiscTexture(int size, float outlineFraction)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = (size - 1) * 0.5f;
        var radius = size * 0.42f;
        var outline = radius * (1.0f - outlineFraction);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                if (distance > radius) continue;
                var alpha = Mathf.Clamp(radius - distance + 0.8f, 0.0f, 1.0f);
                var color = distance >= outline
                    ? new Color(1, 1, 1, 0.85f * alpha)
                    : new Color(1, 1, 1, 0.35f * alpha);
                image.SetPixel(x, y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D MakeStarTexture(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = (size - 1) * 0.5f;
        var radius = size * 0.44f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                var angle = Mathf.Atan2(dy, dx);
                var edge = radius * (0.62f + 0.38f * Mathf.Cos(5.0f * angle));
                if (distance > edge) continue;
                var alpha = Mathf.Clamp(edge - distance + 0.8f, 0.0f, 1.0f);
                image.SetPixel(x, y, new Color(1, 1, 1, 0.85f * alpha));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D MakeArrowTexture(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = Mathf.Abs(x - center);
                var dy = Mathf.Abs(y - center);
                if (dx + dy * 0.9f < size * 0.34f)
                {
                    image.SetPixel(x, y, new Color(1, 1, 1, 0.9f));
                }
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D MakeFanTexture(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = (size - 1) * 0.5f;
        var radius = size * 0.44f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > radius) continue;
                if (Mathf.Abs(dx) < size * 0.08f || Mathf.Abs(dy) < size * 0.08f)
                {
                    var alpha = Mathf.Clamp(radius - distance + 0.8f, 0.0f, 1.0f);
                    image.SetPixel(x, y, new Color(1, 1, 1, alpha));
                }
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D MakeHoldBodyTexture(int width, int height)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var radius = height * 0.5f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = Mathf.Max(0.0f, Mathf.Abs(x - (width - 1) * 0.5f) - (width * 0.5f - radius));
                var dy = y - (height - 1) * 0.5f;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > radius) continue;
                var alpha = Mathf.Clamp(radius - distance + 0.8f, 0.0f, 1.0f);
                image.SetPixel(x, y, new Color(1, 1, 1, 0.7f * alpha));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D MakeRingTexture(int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var center = (size - 1) * 0.5f;
        var outer = size * 0.48f;
        var inner = outer - Mathf.Max(2.0f, size * 0.006f);
        var guide = size * 0.255f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                if (distance >= inner && distance <= outer)
                {
                    var alpha = Mathf.Clamp(Mathf.Min(distance - inner, outer - distance) + 0.8f, 0.0f, 1.0f);
                    image.SetPixel(x, y, new Color(0.22f, 0.48f, 0.86f, alpha));
                }
                else if (Mathf.Abs(distance - guide) <= 1.2f)
                {
                    image.SetPixel(x, y, new Color(0.12f, 0.20f, 0.36f, 0.75f));
                }
            }
        }
        for (var position = 1; position <= 8; position++)
        {
            var point = Polar(position, outer);
            PaintDisc(image, point, 4.5f, new Color(0.55f, 0.72f, 1.0f, 0.9f));
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static void PaintDisc(Image image, Vector2 center, float radius, Color color)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(center.X - radius));
        var maxX = Mathf.Min(image.GetWidth() - 1, Mathf.CeilToInt(center.X + radius));
        var minY = Mathf.Max(0, Mathf.FloorToInt(center.Y - radius));
        var maxY = Mathf.Min(image.GetHeight() - 1, Mathf.CeilToInt(center.Y + radius));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (new Vector2(x - center.X, y - center.Y).Length() <= radius)
                {
                    image.SetPixel(x, y, color);
                }
            }
        }
    }

    private sealed class NoteVisual
    {
        public NoteVisual(NoteView note)
        {
            Note = note;
        }

        public NoteView Note { get; }
        public Sprite2D Head { get; set; }
        public Line2D Guide { get; set; }
        public Sprite2D Body { get; set; }
        public Sprite2D End { get; set; }
        public Sprite2D Star { get; set; }
        public Sprite2D Border { get; set; }
        public Sprite2D Point { get; set; }
        public Sprite2D Just { get; set; }
        public Sprite2D[] Fans { get; set; }
        public List<Sprite2D> Arrows { get; } = new();
        public Vector2[] Path { get; set; }
        public float[] PathLengths { get; set; }
        public float PathTotalLength { get; set; }
    }
}
