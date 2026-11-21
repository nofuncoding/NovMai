using Godot;

namespace NovMai.Rendering;

/// <summary>
/// Optional autoload node that installs the FFmpeg video format when the scene
/// tree starts (<see cref="VideoStreamPlayer"/> scenes then only need to load
/// or create an <see cref="FFmpegVideoStream"/>).
/// </summary>
[GlobalClass]
public partial class FFmpegVideoSupport : Node
{
    public override void _Ready()
    {
        FFmpegVideoRuntime.Install();
    }
}
