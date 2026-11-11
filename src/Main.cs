using System;
using System.IO;
using Godot;
using NovMai.Assets;
using NovMai.Native;
using NovMai.Preview;

public partial class Main : Node2D
{
    [Export] public string ChartPath = "res://assets/charts/sample/maidata.txt";
    [Export] public int Difficulty = 0;
    [Export] public float AppearTime = 2.0f;

    private NoteView[] _notes = Array.Empty<NoteView>();
    private ChartDiagnostic[] _diagnostics = Array.Empty<ChartDiagnostic>();
    private ChartSummary _summary;
    private AudioStreamPlayer _audio;
    private AudioStreamWav _clickTrack;
    private AudioStream _loadedTrack;
    private double _time;
    private double _lastAudioTime = -1.0;
    private bool _playing = true;
    private bool _loaded;
    private string _currentChartPath = "";
    private int _currentDifficulty = 1;
    private bool _updatingDifficulty;
    private FileDialog _fileDialog;
    private FileDialog _folderDialog;
    private OptionButton _difficultyOption;
    private Label _chartLabel;
    private Label _statusLabel;
    private PreviewStage _stage;
    private CanvasLayer _backgroundLayer;
    private TextureRect _backgroundImage;
    private Label _hudLabel;
    private Label _infoLabel;
    private Label _diagLabel;

    public override void _Ready()
    {
        ExternalAssets.Initialize();
        var chartOverride = System.Environment.GetEnvironmentVariable("NOVMAI_CHART");
        if (!string.IsNullOrWhiteSpace(chartOverride))
        {
            ChartPath = chartOverride;
        }
        var difficultyOverride = System.Environment.GetEnvironmentVariable("NOVMAI_DIFFICULTY");
        if (int.TryParse(difficultyOverride, out var forcedDifficulty) &&
            forcedDifficulty >= 1 && forcedDifficulty <= 7)
        {
            Difficulty = forcedDifficulty;
        }
        if (DisplayServer.GetName() != "headless")
        {
            RenderingServer.SetDefaultClearColor(new Color(0.018f, 0.020f, 0.035f));
            _audio = new AudioStreamPlayer { VolumeDb = -6.0f };
            AddChild(_audio);
            BuildPreview();
            BuildUi();
        }
        LoadChart(ChartPath, Difficulty, autoPick: true);
        var seekOverride = System.Environment.GetEnvironmentVariable("NOVMAI_SEEK");
        if (double.TryParse(seekOverride, out var seekTime))
        {
            SeekAudio(Math.Max(0.0, seekTime));
        }
        if (System.Environment.GetEnvironmentVariable("NOVMAI_PAUSE") == "1")
        {
            SetPlaying(false);
        }
    }

    private void LoadChart(string path, int requestedDifficulty = 0, bool autoPick = true)
    {
        try
        {
            var resolvedPath = ResolveChartPath(path);
            LoadChartBackground(resolvedPath);
            var text = ReadChartText(resolvedPath);
            using var chart = NovMaiChart.Parse(text);

            var chosen = 0;
            ChartSummary summary = default;
            if (requestedDifficulty > 0 &&
                chart.TryGetSummary((byte)requestedDifficulty, out summary))
            {
                chosen = requestedDifficulty;
            }
            else if (autoPick)
            {
                for (var difficulty = 7; difficulty >= 1; difficulty--)
                {
                    if (chart.TryGetSummary((byte)difficulty, out summary))
                    {
                        chosen = difficulty;
                        break;
                    }
                }
            }

            if (chosen == 0)
            {
                throw new InvalidOperationException(
                    requestedDifficulty > 0
                        ? $"Difficulty {requestedDifficulty} is not present"
                        : "No difficulty was found in maidata.txt");
            }

            _notes = chart.GetNotes((byte)chosen);
            _diagnostics = chart.GetDiagnostics((byte)chosen);
            _summary = summary;
            _currentChartPath = resolvedPath;
            _currentDifficulty = chosen;
            _time = 0.0;
            _playing = true;
            _loaded = true;
            _stage?.SetChart(_notes);
            SetupAudio();
            UpdateUi($"Loaded {Path.GetFileName(resolvedPath)} — {_notes.Length} notes, {_summary.Duration:F2}s, {_summary.Bpm:F2} BPM");
            UpdateHud();
            GD.Print($"NovMai: loaded {resolvedPath} [diff {chosen}] ({_notes.Length} notes, {_summary.Duration:F3}s, {_summary.Bpm:F2} BPM)");
            foreach (var diagnostic in _diagnostics)
            {
                GD.Print($"NovMai diagnostic: {diagnostic.Message}");
            }
        }
        catch (Exception error)
        {
            UpdateUi($"Load failed: {error.Message}");
            UpdateHud();
            GD.PushError($"NovMai failed to load {path}: {error.Message}");
        }

    }

    private static string ResolveChartPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Empty chart path");
        }

        if (path.StartsWith("res://", StringComparison.Ordinal) ||
            path.StartsWith("user://", StringComparison.Ordinal))
        {
            if (!Godot.FileAccess.FileExists(path))
            {
                throw new FileNotFoundException($"Chart not found: {path}");
            }
            return path;
        }

        if (Directory.Exists(path))
        {
            var candidate = Path.Combine(path, "maidata.txt");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
            throw new FileNotFoundException($"maidata.txt was not found in {path}");
        }

        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        throw new FileNotFoundException($"Chart path does not exist: {path}");
    }

    private static string ReadChartText(string path)
    {
        return path.StartsWith("res://", StringComparison.Ordinal) ||
               path.StartsWith("user://", StringComparison.Ordinal)
            ? Godot.FileAccess.GetFileAsString(path)
            : File.ReadAllText(path);
    }

    private void ReloadCurrentChart()
    {
        if (!string.IsNullOrEmpty(_currentChartPath))
        {
            LoadChart(_currentChartPath, _currentDifficulty);
        }
    }

    private void BuildPreview()
    {
        _backgroundLayer = new CanvasLayer { Layer = -10 };
        AddChild(_backgroundLayer);
        _backgroundImage = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(0.45f, 0.45f, 0.50f, 1.0f),
            Visible = false,
        };
        _backgroundLayer.AddChild(_backgroundImage);
        _backgroundImage.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _stage = new PreviewStage { AppearTime = AppearTime };
        AddChild(_stage);

        var hudLayer = new CanvasLayer { Layer = 5 };
        AddChild(hudLayer);
        _hudLabel = CreateHudLabel(20, Colors.White);
        _infoLabel = CreateHudLabel(14, new Color(0.65f, 0.72f, 0.84f));
        _diagLabel = CreateHudLabel(13, new Color(1.0f, 0.65f, 0.35f));
        hudLayer.AddChild(_hudLabel);
        hudLayer.AddChild(_infoLabel);
        hudLayer.AddChild(_diagLabel);
    }

    private static Label CreateHudLabel(int fontSize, Color color)
    {
        var label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private void LoadChartBackground(string chartPath)
    {
        if (_backgroundImage == null)
        {
            return;
        }

        _backgroundImage.Texture = null;
        _backgroundImage.Visible = false;
        if (chartPath.StartsWith("res://", StringComparison.Ordinal) ||
            chartPath.StartsWith("user://", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.GetDirectoryName(chartPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        foreach (var name in new[] { "bg.jpg", "bg.png", "bg.jpeg", "bg.webp" })
        {
            var fullPath = Path.Combine(directory, name);
            if (!File.Exists(fullPath))
            {
                continue;
            }
            try
            {
                using var image = Image.LoadFromFile(fullPath);
                if (image == null || image.IsEmpty())
                {
                    continue;
                }
                _backgroundImage.Texture = ImageTexture.CreateFromImage(image);
                _backgroundImage.Visible = true;
                return;
            }
            catch (Exception error)
            {
                GD.PushWarning($"NovMai: failed to load chart background {fullPath}: {error.Message}");
            }
        }
    }

    private void BuildUi()
    {
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);

        var panel = new PanelContainer
        {
            Position = new Vector2(16, 16),
            CustomMinimumSize = new Vector2(620, 0),
        };
        layer.AddChild(panel);

        var box = new VBoxContainer();
        panel.AddChild(box);

        var row = new HBoxContainer();
        box.AddChild(row);

        var openFile = new Button { Text = "Open chart…" };
        openFile.Pressed += ShowOpenChartDialog;
        row.AddChild(openFile);

        var openFolder = new Button { Text = "Open folder…" };
        openFolder.Pressed += ShowOpenFolderDialog;
        row.AddChild(openFolder);

        var reload = new Button { Text = "Reload" };
        reload.Pressed += ReloadCurrentChart;
        row.AddChild(reload);

        _difficultyOption = new OptionButton();
        for (var difficulty = 1; difficulty <= 7; difficulty++)
        {
            _difficultyOption.AddItem($"Diff {difficulty}", difficulty);
        }
        _difficultyOption.ItemSelected += OnDifficultySelected;
        row.AddChild(_difficultyOption);

        _chartLabel = new Label { Text = "No chart loaded" };
        _chartLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        box.AddChild(_chartLabel);

        _statusLabel = new Label { Text = "" };
        _statusLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        box.AddChild(_statusLabel);

        _fileDialog = new FileDialog
        {
            Title = "Open maidata.txt",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.txt, *.maidata ; Simai chart" },
        };
        _fileDialog.FileSelected += OnChartFileSelected;
        AddChild(_fileDialog);

        _folderDialog = new FileDialog
        {
            Title = "Open song folder",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenDir,
        };
        _folderDialog.DirSelected += OnFolderSelected;
        AddChild(_folderDialog);
    }

    private void ShowOpenChartDialog()
    {
        if (_fileDialog == null)
        {
            return;
        }
        if (!string.IsNullOrEmpty(_currentChartPath) && !_currentChartPath.StartsWith("res://", StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(_currentChartPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _fileDialog.CurrentDir = directory;
            }
        }
        _fileDialog.PopupCenteredRatio(0.8f);
    }

    private void ShowOpenFolderDialog()
    {
        if (_folderDialog == null)
        {
            return;
        }
        if (!string.IsNullOrEmpty(_currentChartPath) && !_currentChartPath.StartsWith("res://", StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(_currentChartPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _folderDialog.CurrentDir = directory;
            }
        }
        _folderDialog.PopupCenteredRatio(0.6f);
    }

    private void OnChartFileSelected(string path)
    {
        LoadChart(path, 0, autoPick: true);
    }

    private void OnFolderSelected(string path)
    {
        LoadChart(path, 0, autoPick: true);
    }

    private void OnDifficultySelected(long index)
    {
        if (_updatingDifficulty || string.IsNullOrEmpty(_currentChartPath))
        {
            return;
        }
        var difficulty = (int)_difficultyOption.GetItemId((int)index);
        LoadChart(_currentChartPath, difficulty, autoPick: false);
    }

    private void UpdateDifficultySelection()
    {
        if (_difficultyOption == null)
        {
            return;
        }
        _updatingDifficulty = true;
        for (var index = 0; index < _difficultyOption.ItemCount; index++)
        {
            if (_difficultyOption.GetItemId(index) == _currentDifficulty)
            {
                _difficultyOption.Select(index);
                break;
            }
        }
        _updatingDifficulty = false;
    }

    private void UpdateUi(string status)
    {
        if (_chartLabel != null)
        {
            _chartLabel.Text = string.IsNullOrEmpty(_currentChartPath) ? "No chart loaded" : _currentChartPath;
        }
        if (_statusLabel != null)
        {
            _statusLabel.Text = status;
        }
        UpdateDifficultySelection();
    }

    private AudioStream TryLoadTrack()
    {
        if (string.IsNullOrEmpty(_currentChartPath) ||
            _currentChartPath.StartsWith("res://", StringComparison.Ordinal) ||
            _currentChartPath.StartsWith("user://", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(_currentChartPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        foreach (var name in new[] { "track.mp3", "track.wav", "track.ogg" })
        {
            var fullPath = Path.Combine(directory, name);
            if (!File.Exists(fullPath))
            {
                continue;
            }
            var stream = ExternalAssets.TryLoadAudioFile(fullPath);
            if (stream != null)
            {
                GD.Print($"NovMai: using chart audio {fullPath}");
                return stream;
            }
        }
        return null;
    }

    public override void _ExitTree()
    {
        if (_audio != null)
        {
            _audio.Stop();
            _audio.Stream = null;
        }
        _clickTrack?.Dispose();
        _clickTrack = null;
        _loadedTrack?.Dispose();
        _loadedTrack = null;
    }

    public override void _Process(double delta)
    {
        if (_loaded && _playing)
        {
            var audioTime = GetAudioTime();
            if (audioTime >= 0.0 && (audioTime > _lastAudioTime + 1e-6 || _lastAudioTime < 0.0))
            {
                _time = audioTime;
            }
            else
            {
                // Audio can be absent (headless / Dummy driver) or report the
                // same chunk twice; keep the visual clock moving in that case.
                _time += delta;
            }
            _lastAudioTime = audioTime;

            var end = Math.Max(_summary.Duration, 1.0) + 1.0;
            if (_time > end)
            {
                _time = 0.0;
                _lastAudioTime = -1.0;
                if (_audio != null && _audio.Stream != null)
                {
                    _audio.Play(0.0f);
                }
            }
        }

        UpdateStage();
        UpdateHud();
    }

    private void UpdateStage()
    {
        if (_stage == null)
        {
            return;
        }

        var viewport = GetViewportRect().Size;
        var scale = Mathf.Min(viewport.X, viewport.Y) * 0.42f / PreviewStage.RingRadius;
        _stage.Position = viewport * 0.5f;
        _stage.Scale = Vector2.One * scale;
        _stage.AppearTime = AppearTime;
        _stage.Sync(_time);
    }

    private void UpdateHud()
    {
        if (_hudLabel == null)
        {
            return;
        }

        var viewport = GetViewportRect().Size;
        _hudLabel.Position = new Vector2(24, viewport.Y - 96);
        _infoLabel.Position = new Vector2(24, viewport.Y - 66);
        _diagLabel.Position = new Vector2(24, viewport.Y - 38);

        if (!_loaded)
        {
            _hudLabel.Text = "No chart loaded";
            _infoLabel.Text = "";
            _diagLabel.Text = "";
            return;
        }

        _hudLabel.Text = $"t={_time:F2}s / {_summary.Duration:F2}s   notes={_notes.Length}   {(_playing ? "PLAY" : "PAUSE")}";
        _infoLabel.Text = $"BPM {_summary.Bpm:F2}   chart {_currentDifficulty}   Space: play/pause   O: open chart   R: reset   Left/Right: seek";
        _diagLabel.Text = _diagnostics.Length == 0
            ? $"Skin {ExternalAssets.SkinCount} files   SFX {ExternalAssets.SfxCount} files"
            : $"{_diagnostics[0].Message}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_accept"))
        {
            SetPlaying(!_playing);
            return;
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.O)
            {
                ShowOpenChartDialog();
                return;
            }
            if (key.Keycode == Key.R)
            {
                SeekAudio(0.0);
            }
            else if (key.Keycode == Key.Left)
            {
                SeekAudio(Math.Max(0.0, _time - 1.0));
            }
            else if (key.Keycode == Key.Right)
            {
                SeekAudio(_time + 1.0);
            }
        }
    }

    private void SetupAudio()
    {
        if (_audio == null)
        {
            return;
        }

        _audio.Stop();
        _audio.Stream = null;
        _clickTrack?.Dispose();
        _clickTrack = null;
        _loadedTrack?.Dispose();
        _loadedTrack = null;

        var track = TryLoadTrack();
        if (track != null)
        {
            _loadedTrack = track;
            _audio.Stream = track;
        }
        else
        {
            var bpm = _summary.Bpm > 0.0 ? _summary.Bpm : 120.0;
            var duration = Math.Max(_summary.Duration, 1.0) + 1.0;
            _clickTrack = CreateClickTrack(duration, bpm);
            _audio.Stream = _clickTrack;
        }

        _lastAudioTime = -1.0;
        if (_playing)
        {
            _audio.Play((float)_time);
        }
    }

    /// <summary>
    /// Generates a metronome-only track when the selected chart has no
    /// track.mp3 / track.wav / track.ogg next to maidata.txt.
    /// </summary>
    private static AudioStreamWav CreateClickTrack(double duration, double bpm)
    {
        const int sampleRate = 22050;
        var totalSamples = Math.Max(1, (int)(duration * sampleRate));
        var data = new byte[totalSamples * 2];
        var beat = 60.0 / Math.Max(bpm, 1.0);

        for (var index = 0; index < totalSamples; index++)
        {
            var time = (double)index / sampleRate;
            var phase = time % beat;
            var value = 0.0;
            if (phase < 0.035)
            {
                var beatIndex = (int)Math.Floor(time / beat);
                var frequency = beatIndex % 4 == 0 ? 1760.0 : 880.0;
                var envelope = 1.0 - phase / 0.035;
                value = Math.Sin(2.0 * Math.PI * frequency * phase) * envelope * 0.28;
            }

            var sample = (short)(value * short.MaxValue);
            data[index * 2] = (byte)(sample & 0xFF);
            data[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Data = data,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false,
        };
    }

    private double GetAudioTime()
    {
        if (_audio == null || _audio.Stream == null || !_audio.Playing || _audio.StreamPaused)
        {
            return -1.0;
        }

        var position = (double)_audio.GetPlaybackPosition()
            + AudioServer.GetTimeSinceLastMix()
            - AudioServer.GetOutputLatency();
        return Math.Max(0.0, position);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        if (_audio == null || _audio.Stream == null)
        {
            return;
        }

        if (playing)
        {
            _audio.StreamPaused = false;
            if (!_audio.Playing)
            {
                _audio.Play((float)_time);
            }
            _lastAudioTime = -1.0;
        }
        else
        {
            _audio.StreamPaused = true;
        }
    }

    private void SeekAudio(double seconds)
    {
        var end = Math.Max(_summary.Duration, 1.0) + 1.0;
        _time = Math.Clamp(seconds, 0.0, end);
        _lastAudioTime = -1.0;

        if (_audio == null || _audio.Stream == null)
        {
            return;
        }

        _audio.Seek((float)_time);
        if (_playing)
        {
            _audio.StreamPaused = false;
            if (!_audio.Playing)
            {
                _audio.Play((float)_time);
            }
        }
    }
}
