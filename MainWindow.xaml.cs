using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml.Linq;

namespace AssetViewer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".tga" };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".wma", ".ogg", ".oga", ".opus", ".flac", ".aac", ".m4a", ".aif", ".aiff", ".mid", ".midi", ".caf", ".amr" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".webm", ".mkv" };
    private static readonly HashSet<string> ModelExt = new(StringComparer.OrdinalIgnoreCase) { ".obj", ".stl", ".ply", ".off", ".gltf", ".glb", ".dae" };
    private static readonly HashSet<string> UnityYamlExt = new(StringComparer.OrdinalIgnoreCase) { ".asset", ".meta", ".prefab", ".unity", ".mat", ".controller", ".anim", ".overridecontroller", ".rendertexture", ".mixer", ".playable", ".mask", ".guiskin", ".physicmaterial", ".terrainlayer", ".lighting", ".preset", ".spriteatlas", ".inputactions" };
    private static readonly HashSet<string> InfoExt = new(StringComparer.OrdinalIgnoreCase) { ".fbx", ".blend", ".unitypackage", ".bundle", ".assets", ".resource", ".ress", ".bank", ".bytes", ".dat", ".3ds" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".c", ".h", ".cpp", ".lua", ".py", ".js", ".jsx", ".ts", ".tsx", ".java", ".kt", ".go", ".rs", ".rb", ".json", ".xml", ".xaml", ".csproj", ".sln", ".asmdef", ".asmref", ".shader", ".shadergraph", ".compute", ".cginc", ".glsl", ".hlsl", ".txt", ".md", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".bat", ".ps1", ".html", ".css", ".sql", ".log", ".properties", ".vue", ".uxml", ".uss", ".srt", ".vtt", ".cmake", ".conf" };

    private readonly List<AssetEntry> allAssets = new();
    private readonly HashSet<string> allDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer gifTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer audioTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer videoTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private List<BitmapFrame> gifFrames = new();
    private List<TimeSpan> gifDelays = new();
    private int gifFrameIndex;
    private bool audioSeeking, gifSeeking, gifPlaying, videoSeeking, modelDragging;
    private Point lastModelPoint;
    private double modelYaw, modelPitch, modelZoom = 5;
    private string? rootFolder;
    private string? searchScopePath;
    private bool rebuildingTree;
    private AssetEntry? currentAsset;
    private MeshGeometry3D? currentMesh;
    private string? currentMeshPath;

    public MainWindow()
    {
        InitializeComponent();
        gifTimer.Tick += (_, _) => AdvanceGif();
        audioTimer.Tick += (_, _) => UpdateAudioTime();
        videoTimer.Tick += (_, _) => UpdateVideoTime();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); ApplyFilters(); };
        AudioPlayer.MediaFailed += AudioPlayer_MediaFailed;
        UpdateCount();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an asset folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) LoadFolder(dialog.FolderName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var folder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
        if (!string.IsNullOrWhiteSpace(folder)) LoadFolder(folder);
    }

    private void LoadFolder(string folder)
    {
        StopPreviews();
        rootFolder = Path.GetFullPath(folder);
        searchScopePath = rootFolder;
        currentAsset = null;
        currentMesh = null;
        currentMeshPath = null;
        allAssets.Clear();
        allDirectories.Clear();
        FolderLabel.Text = rootFolder;
        allDirectories.Add(rootFolder);
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var path in Directory.GetFiles(current))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        allAssets.Add(new AssetEntry(path, rootFolder, info.Length, info.LastWriteTime));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                foreach (var path in Directory.GetDirectories(current))
                {
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        var fullPath = Path.GetFullPath(path);
                        allDirectories.Add(fullPath);
                        pending.Push(fullPath);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        ApplyFilters();
        if (allAssets.Count == 0) ShowEmpty("This folder is empty", "Choose another folder or drop one onto the window.");
        else if (!HasVisibleFiles()) ShowEmpty("No files match this filter", "Change the search or file type filter.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) searchScopePath = rootFolder;
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();
    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) { searchTimer.Stop(); ApplyFilters(); } }

    private void ApplyFilters()
    {
        if (AssetTree is null || rebuildingTree) return;
        try
        {
            var query = SearchBox.Text.Trim();
            var mode = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All files";
            var scope = query.Length == 0 ? rootFolder : (searchScopePath ?? rootFolder);
            if (rootFolder is null || scope is null) { AssetTree.ItemsSource = null; return; }
            if (!scope.Equals(rootFolder, StringComparison.OrdinalIgnoreCase) && !IsInside(scope, rootFolder)) scope = rootFolder;

            var matches = allAssets.Where(asset => IsInside(asset.Path, scope)
                    && (query.Length == 0 || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                    && MatchesFilter(asset, mode))
                .OrderBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

            var root = new ExplorerNode(Path.GetFileName(scope.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), scope, null, true) { IsExpanded = true };
            var nodeByPath = new Dictionary<string, ExplorerNode>(StringComparer.OrdinalIgnoreCase) { [scope] = root };
            var visibleFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in matches)
            {
                var parent = Path.GetDirectoryName(asset.Path);
                while (parent is not null && !parent.Equals(scope, StringComparison.OrdinalIgnoreCase) && IsInside(parent, scope))
                {
                    visibleFolders.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }

            var showEmpty = query.Length == 0 && mode == "All files" && scope.Equals(rootFolder, StringComparison.OrdinalIgnoreCase);
            var folders = allDirectories.Where(path => !path.Equals(scope, StringComparison.OrdinalIgnoreCase) && IsInside(path, scope)
                    && (showEmpty || visibleFolders.Contains(path)))
                .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in folders)
            {
                var parentPath = Path.GetDirectoryName(path);
                if (parentPath is null || !nodeByPath.TryGetValue(parentPath, out var parent)) continue;
                var node = new ExplorerNode(Path.GetFileName(path), path, null)
                {
                    IsExpanded = query.Length > 0 && matches.Count <= 20
                };
                parent.Children.Add(node);
                nodeByPath[path] = node;
            }
            foreach (var asset in matches)
            {
                var parentPath = Path.GetDirectoryName(asset.Path);
                if (parentPath is not null && nodeByPath.TryGetValue(parentPath, out var parent))
                    parent.Children.Add(new ExplorerNode(asset.Name, asset.Path, asset));
            }
            foreach (var node in nodeByPath.Values) node.SortChildren();

            rebuildingTree = true;
            AssetTree.ItemsSource = new[] { root };
            rebuildingTree = false;
            TreeCount.Text = $"{matches.Count:N0} matches  ·  {(scope == rootFolder ? "root" : Path.GetFileName(scope))}";
            StatusText.Text = $"{matches.Count:N0} of {allAssets.Count:N0} files";
            currentAsset = null;
            RevealButton.IsEnabled = false;
            if (matches.Count == 0 && query.Length > 0) ShowEmpty("No matching files", $"Nothing found under {Path.GetFileName(scope)}.");
            else if (query.Length > 0) ShowEmpty("Search results", $"{matches.Count:N0} files found under {Path.GetFileName(scope)}. Expand a folder to browse.");
        }
        catch (Exception ex)
        {
            rebuildingTree = false;
            StatusText.Text = "Search could not be applied";
            PreviewSubTitle.Text = $"Search error: {ex.Message}";
        }
    }

    private bool HasVisibleFiles()
    {
        var mode = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All files";
        var query = SearchBox.Text.Trim();
        return allAssets.Any(asset => (query.Length == 0 || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) && MatchesFilter(asset, mode));
    }

    private static bool IsInside(string filePath, string directoryPath) =>
        filePath.StartsWith(directoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFilter(AssetEntry asset, string mode) => mode switch
    {
        "Images" => asset.Kind == AssetKind.Image,
        "Audio" => asset.Kind == AssetKind.Audio,
        "Video" => asset.Kind == AssetKind.Video,
        "3D models" => asset.Kind == AssetKind.Model,
        "Scripts" => asset.Kind == AssetKind.Text,
        _ => true
    };

    private void UpdateCount()
    {
        if (StatusText is not null) StatusText.Text = $"{allAssets.Count:N0} file{(allAssets.Count == 1 ? "" : "s")} in folder";
    }

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (rebuildingTree) return;
        if (AssetTree.SelectedItem is ExplorerNode node && node.Asset is not null)
        {
            searchScopePath = rootFolder;
            currentAsset = node.Asset;
            RevealButton.IsEnabled = true;
            ShowAsset(node.Asset);
        }
        else if (AssetTree.SelectedItem is ExplorerNode folder)
        {
            searchScopePath = folder.FullPath;
            currentAsset = null;
            RevealButton.IsEnabled = false;
            ShowEmpty(folder.Name, folder.IsRoot ? "Asset library root" : $"Folder  ·  {folder.Children.Count:N0} items");
            if (SearchBox.Text.Length > 0) { searchTimer.Stop(); searchTimer.Start(); }
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (currentAsset is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentAsset.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't open the file location.\n\n{ex.Message}", "File location", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowAsset(AssetEntry asset)
    {
        StopPreviews();
        PreviewTitle.Text = asset.Name;
        PreviewSubTitle.Text = $"{asset.RelativePath}  ·  {AssetEntry.FormatSize(asset.Size)}  ·  {asset.Modified:g}";
        FormatBadge.Text = asset.Extension.TrimStart('.').ToUpperInvariant();
        try
        {
            switch (asset.Kind)
            {
                case AssetKind.Image: ShowImage(asset); break;
                case AssetKind.Audio: ShowAudio(asset); break;
                case AssetKind.Video: ShowVideo(asset); break;
                case AssetKind.Model:
                    if (currentMeshPath != asset.Path) { currentMesh = ModelReader.Load(asset.Path); currentMeshPath = asset.Path; }
                    PreviewSubTitle.Text += $"  ·  {currentMesh!.TriangleIndices.Count / 3:N0} triangles";
                    ShowModel(asset);
                    break;
                case AssetKind.Text: ShowText(asset); break;
                default: ShowUnsupported(asset); break;
            }
        }
        catch (Exception ex) { ShowUnsupported(asset); UnsupportedText.Text = ex.Message; }
    }

    private void ShowImage(AssetEntry asset)
    {
        GifControls.Visibility = Visibility.Collapsed;
        if (asset.Extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
            ImagePreview.Source = TgaReader.Load(asset.Path);
        else
        {
            var decoder = BitmapDecoder.Create(new Uri(asset.Path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (asset.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) && decoder.Frames.Count > 1)
            {
                gifFrames = decoder.Frames.ToList();
                gifDelays = gifFrames.Select(GetGifFrameDelay).ToList();
                gifFrameIndex = 0;
                gifPlaying = true;
                GifSlider.Maximum = gifFrames.Count - 1;
                GifSlider.Value = 0;
                GifFrameLabel.Text = $"Frame 1 / {gifFrames.Count}";
                GifPlayButton.Content = "Ⅱ  Pause";
                GifControls.Visibility = Visibility.Visible;
                UpdateGifInterval();
                gifTimer.Start();
                PreviewSubTitle.Text += $"  ·  {gifFrames.Count} frames · animated GIF";
            }
            ImagePreview.Source = decoder.Frames[0];
        }
        ShowOnly(ImageSurface);
        FormatBadge.Text = asset.Extension.TrimStart('.').ToUpperInvariant();
    }

    private void AdvanceGif()
    {
        if (gifFrames.Count == 0 || !gifPlaying) return;
        SetGifFrame((gifFrameIndex + 1) % gifFrames.Count);
    }

    private static TimeSpan GetGifFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                var hundredths = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"), CultureInfo.InvariantCulture);
                if (hundredths > 0) return TimeSpan.FromMilliseconds(Math.Max(20, hundredths * 10));
            }
        }
        catch { }
        return TimeSpan.FromMilliseconds(100);
    }

    private void SetGifFrame(int index)
    {
        if (gifFrames.Count == 0) return;
        gifFrameIndex = Math.Clamp(index, 0, gifFrames.Count - 1);
        ImagePreview.Source = gifFrames[gifFrameIndex];
        GifFrameLabel.Text = $"Frame {gifFrameIndex + 1} / {gifFrames.Count}";
        gifSeeking = false;
        GifSlider.Value = gifFrameIndex;
        gifSeeking = true;
        UpdateGifInterval();
    }

    private void UpdateGifInterval()
    {
        if (gifDelays.Count == 0) return;
        var speed = GifSpeed.SelectedIndex switch { 0 => 0.5, 2 => 2.0, _ => 1.0 };
        gifTimer.Interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(20).Ticks, (long)(gifDelays[gifFrameIndex].Ticks / speed)));
    }

    private void GifPlay_Click(object sender, RoutedEventArgs e)
    {
        gifPlaying = !gifPlaying;
        GifPlayButton.Content = gifPlaying ? "Ⅱ  Pause" : "▶  Play";
        if (gifPlaying) { UpdateGifInterval(); gifTimer.Start(); } else gifTimer.Stop();
    }

    private void GifRestart_Click(object sender, RoutedEventArgs e)
    {
        SetGifFrame(0);
        gifPlaying = true;
        GifPlayButton.Content = "Ⅱ  Pause";
        gifTimer.Start();
    }

    private void GifSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (gifSeeking && gifFrames.Count > 0) SetGifFrame((int)Math.Round(GifSlider.Value));
    }

    private void GifSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => gifSeeking = true;
    private void GifSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e) => gifSeeking = false;
    private void GifSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateGifInterval(); }

    private void AudioPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        AudioStatus.Text = "Windows has no decoder for this format. Try an external player.";
        ExternalAudioButton.Visibility = Visibility.Visible;
        PlayButton.Content = "▶   Play";
    }

    private void ShowAudio(AssetEntry asset)
    {
        AudioName.Text = asset.Name;
        AudioFormat.Text = $"{asset.Extension.TrimStart('.').ToUpperInvariant()} audio · {AssetEntry.FormatSize(asset.Size)}";
        AudioStatus.Text = "Ready to play";
        ExternalAudioButton.Visibility = Visibility.Collapsed;
        AudioPlayer.Source = new Uri(asset.Path);
        AudioSlider.Value = 0;
        AudioTime.Text = "00:00 / --:--";
        ShowOnly(AudioSurface);
        AudioPlayer.Play(); AudioPlayer.Pause();
        PlayButton.Content = "▶   Play";
        audioTimer.Start();
    }

    private void OpenExternalAudio_Click(object sender, RoutedEventArgs e)
    {
        if (currentAsset is null) return;
        try { Process.Start(new ProcessStartInfo(currentAsset.Path) { UseShellExecute = true }); }
        catch (Exception ex) { AudioStatus.Text = $"No external player was found: {ex.Message}"; }
    }

    private void ShowVideo(AssetEntry asset)
    {
        VideoPlayer.Source = new Uri(asset.Path);
        VideoPlayer.Volume = AudioVolumeSlider.Value;
        VideoSlider.Value = 0;
        VideoTime.Text = "00:00 / --:--";
        VideoStatus.Text = "Loading video…";
        ExternalVideoButton.Visibility = Visibility.Collapsed;
        VideoPlayButton.Content = "▶  Play";
        ShowOnly(VideoSurface);
        VideoPlayer.Play();
        VideoPlayer.Pause();
        videoTimer.Start();
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        VideoStatus.Text = "Ready";
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
            VideoSlider.Maximum = Math.Max(1, VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds);
        UpdateVideoTime();
    }

    private void VideoPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        VideoStatus.Text = "Windows could not decode this video. Try an external player.";
        ExternalVideoButton.Visibility = Visibility.Visible;
        VideoPlayButton.Content = "▶  Play";
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e) => VideoPlayButton.Content = "▶  Play";

    private void VideoPlay_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source is null) return;
        if (VideoPlayButton.Content?.ToString()?.Contains("Play") == true) { VideoPlayer.Play(); VideoPlayButton.Content = "Ⅱ  Pause"; }
        else { VideoPlayer.Pause(); VideoPlayButton.Content = "▶  Play"; }
    }

    private void VideoRestart_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source is null) return;
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
        VideoPlayButton.Content = "Ⅱ  Pause";
    }

    private void VideoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (videoSeeking && VideoPlayer.Source is not null) VideoPlayer.Position = TimeSpan.FromSeconds(VideoSlider.Value);
    }
    private void VideoSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => videoSeeking = true;
    private void VideoSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (VideoPlayer.Source is not null) VideoPlayer.Position = TimeSpan.FromSeconds(VideoSlider.Value);
        videoSeeking = false;
    }

    private void UpdateVideoTime()
    {
        if (VideoPlayer.Source is null) return;
        var duration = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = VideoPlayer.Position;
        videoSeeking = false;
        if (duration > TimeSpan.Zero) VideoSlider.Value = Math.Min(VideoSlider.Maximum, position.TotalSeconds);
        VideoTime.Text = $"{FormatTime(position)} / {(duration > TimeSpan.Zero ? FormatTime(duration) : "--:--")}";
        videoSeeking = true;
    }

    private void OpenExternalVideo_Click(object sender, RoutedEventArgs e)
    {
        if (currentAsset is null) return;
        try { Process.Start(new ProcessStartInfo(currentAsset.Path) { UseShellExecute = true }); }
        catch (Exception ex) { VideoStatus.Text = $"No external player was found: {ex.Message}"; }
    }

    private void AudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioPlayer is not null) AudioPlayer.Volume = AudioVolumeSlider.Value;
        if (VideoPlayer is not null) VideoPlayer.Volume = AudioVolumeSlider.Value;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (AudioPlayer.Source is null) return;
        if (PlayButton.Content?.ToString()?.Contains("Play") == true) { AudioPlayer.Play(); PlayButton.Content = "Ⅱ   Pause"; }
        else { AudioPlayer.Pause(); PlayButton.Content = "▶   Play"; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AudioPlayer.Stop(); PlayButton.Content = "▶   Play"; AudioSlider.Value = 0; UpdateAudioTime();
    }

    private void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!AudioPlayer.NaturalDuration.HasTimeSpan) return;
        AudioSlider.Maximum = Math.Max(1, AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds);
        UpdateAudioTime();
    }
    private void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e) => PlayButton.Content = "▶   Play";
    private void AudioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (audioSeeking && AudioPlayer.Source is not null) AudioPlayer.Position = TimeSpan.FromSeconds(AudioSlider.Value);
    }
    private void AudioSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => audioSeeking = true;
    private void AudioSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (AudioPlayer.Source is not null) AudioPlayer.Position = TimeSpan.FromSeconds(AudioSlider.Value);
        audioSeeking = false;
    }

    private void UpdateAudioTime()
    {
        if (AudioPlayer.Source is null) return;
        var duration = AudioPlayer.NaturalDuration.HasTimeSpan ? AudioPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = AudioPlayer.Position;
        audioSeeking = false;
        if (duration > TimeSpan.Zero) AudioSlider.Value = Math.Min(AudioSlider.Maximum, position.TotalSeconds);
        AudioTime.Text = $"{FormatTime(position)} / {(duration > TimeSpan.Zero ? FormatTime(duration) : "--:--")}";
        audioSeeking = true;
    }
    private static string FormatTime(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    private void ShowText(AssetEntry asset)
    {
        const int maxBytes = 512 * 1024;
        var bytes = new byte[(int)Math.Min(asset.Size, maxBytes)];
        using (var stream = File.OpenRead(asset.Path)) stream.ReadExactly(bytes);
        var sampleLength = Math.Min(bytes.Length, 8192);
        var binary = bytes.Take(sampleLength).Any(value => value == 0) || bytes.Take(sampleLength).Count(value => value < 9 || (value > 13 && value < 32)) > sampleLength / 30;
        var content = binary ? HexPreview(bytes) : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var type = GetFileDescription(asset);
        var info = new StringBuilder()
            .AppendLine("FILE INFORMATION")
            .AppendLine($"Type: {type}")
            .AppendLine($"Format: {asset.Extension.TrimStart('.').ToUpperInvariant()} {(binary ? "binary" : "text")}")
            .AppendLine($"Path: {asset.RelativePath}")
            .AppendLine($"Size: {AssetEntry.FormatSize(asset.Size)}")
            .AppendLine($"Modified: {asset.Modified:g}");

        if (!binary && UnityYamlExt.Contains(asset.Extension))
        {
            var records = Regex.Matches(content, @"(?m)^--- !u!\d+ &\d+").Count;
            var name = Regex.Match(content, @"(?m)^\s*m_Name:\s*(.+?)\s*$").Groups[1].Value;
            var guid = Regex.Match(content, @"(?m)^guid:\s*([a-fA-F0-9]+)").Groups[1].Value;
            var references = Regex.Matches(content, @"(?m)^\s*guid:\s*[a-fA-F0-9]{32}\s*$").Count;
            if (!string.IsNullOrWhiteSpace(name)) info.AppendLine($"Object name: {name}");
            if (!string.IsNullOrWhiteSpace(guid)) info.AppendLine($"GUID: {guid}");
            if (records > 0) info.AppendLine($"Serialized objects: {records}");
            if (references > 0) info.AppendLine($"GUID references in preview: {references}");
            if (asset.Extension.Equals(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var stop = Regex.Match(content, @"(?m)^\s*m_StopTime:\s*([0-9.eE+-]+)").Groups[1].Value;
                var rate = Regex.Match(content, @"(?m)^\s*m_SampleRate:\s*([0-9.eE+-]+)").Groups[1].Value;
                var keyframes = Regex.Matches(content, @"(?m)^\s*time:\s*[-+0-9.eE]+").Count;
                var channels = Regex.Matches(content, @"(?m)^\s*attribute:\s*(.+)$").Cast<Match>().Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (stop.Length > 0) info.AppendLine($"Clip duration: {stop} seconds");
                if (rate.Length > 0) info.AppendLine($"Sample rate: {rate} fps");
                info.AppendLine($"Keyframes in preview: {keyframes}");
                info.AppendLine($"Animated channels: {channels}");
                info.AppendLine("This file contains curve data; a rigged character preview requires its matching model.");
            }
        }

        if (asset.Size > bytes.Length) info.AppendLine($"Preview clipped at {AssetEntry.FormatSize(bytes.Length)}.");
        var text = binary ? HexPreview(bytes) : content;
        TextPreview.Text = info + Environment.NewLine + "CONTENT PREVIEW" + Environment.NewLine + "────────────────────────────────────────" + Environment.NewLine + text;
        LineCount.Text = binary ? $"{bytes.Length:N0} bytes shown as hex" : $"{content.Count(c => c == '\n') + 1:N0} lines shown";
        EncodingLabel.Text = binary ? "HEX PREVIEW" : asset.Extension.TrimStart('.').ToUpperInvariant();
        ShowOnly(TextSurface);
    }

    private static string HexPreview(byte[] bytes)
    {
        const int limit = 160;
        var shown = bytes.Take(limit).ToArray();
        var rows = new List<string>();
        for (var offset = 0; offset < shown.Length; offset += 16)
        {
            var row = shown.Skip(offset).Take(16).ToArray();
            rows.Add($"{offset:X8}  {Convert.ToHexString(row).Chunk(2).Select(pair => new string(pair)).Aggregate((a, b) => a + " " + b)}");
        }
        if (bytes.Length > limit) rows.Add("…");
        return string.Join(Environment.NewLine, rows);
    }

    private static string GetFileDescription(AssetEntry asset) => asset.Extension.ToLowerInvariant() switch
    {
        ".asset" => "Unity serialized asset",
        ".meta" => "Unity asset metadata / import settings",
        ".prefab" => "Unity prefab",
        ".unity" => "Unity scene",
        ".mat" => "Unity material",
        ".controller" => "Unity animator controller",
        ".anim" => "Unity animation clip data",
        ".overridecontroller" => "Unity animator override controller",
        ".inputactions" => "Unity Input System actions",
        ".shadergraph" => "Unity Shader Graph",
        ".mp4" or ".m4v" or ".mov" or ".avi" or ".wmv" or ".mpg" or ".mpeg" or ".webm" or ".mkv" => "Video / animation clip",
        ".asmdef" => "Unity assembly definition",
        ".asmref" => "Unity assembly reference",
        ".fbx" => "FBX model file (text or binary metadata)",
        ".blend" => "Blender project (binary metadata)",
        ".unitypackage" => "Unity package archive (binary metadata)",
        ".bundle" or ".assets" or ".resource" or ".ress" => "Unity build asset / bundle (binary metadata)",
        ".3ds" => "3D Studio model (binary metadata)",
        _ => "Text or game project file"
    };

    private void ShowUnsupported(AssetEntry asset)
    {
        UnsupportedText.Text = $"{asset.Extension.ToUpperInvariant()}  ·  {AssetEntry.FormatSize(asset.Size)}";
        ShowOnly(UnsupportedSurface);
    }

    private void ShowEmpty(string title, string subtitle)
    {
        StopPreviews(); PreviewTitle.Text = title; PreviewSubTitle.Text = subtitle; FormatBadge.Text = "READY"; ShowOnly(EmptyState);
    }

    private void ShowOnly(UIElement element)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ImageSurface.Visibility = Visibility.Collapsed;
        AudioSurface.Visibility = Visibility.Collapsed;
        VideoSurface.Visibility = Visibility.Collapsed;
        ModelSurface.Visibility = Visibility.Collapsed;
        TextSurface.Visibility = Visibility.Collapsed;
        UnsupportedSurface.Visibility = Visibility.Collapsed;
        element.Visibility = Visibility.Visible;
    }

    private void StopPreviews()
    {
        gifTimer.Stop(); gifFrames.Clear(); gifDelays.Clear(); gifPlaying = false; gifSeeking = false;
        GifControls.Visibility = Visibility.Collapsed;
        audioTimer.Stop(); audioSeeking = false;
        videoTimer.Stop(); videoSeeking = false;
        if (AudioPlayer is not null) { AudioPlayer.Stop(); AudioPlayer.Source = null; }
        if (VideoPlayer is not null) { VideoPlayer.Stop(); VideoPlayer.Source = null; }
        if (ImagePreview is not null) ImagePreview.Source = null;
    }
    private void ShowModel(AssetEntry asset)
    {
        var mesh = currentMesh ?? throw new InvalidDataException("No model data was loaded.");
        ModelViewport.Children.Clear();
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(95, 104, 120)));
        root.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, -3)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(140, 178, 173), new Vector3D(2, 1, 3)));
        root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(117, 173, 162))))
        {
            BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(81, 125, 119)))
        });
        var rotate = new Transform3DGroup();
        rotate.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), modelYaw)));
        rotate.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), modelPitch)));
        root.Transform = rotate;
        ModelViewport.Children.Add(new ModelVisual3D { Content = root });
        ModelViewport.Camera = new PerspectiveCamera(new Point3D(0, 0, modelZoom), new Vector3D(0, 0, -modelZoom), new Vector3D(0, 1, 0), 45);
        ModelStats.Text = $"{mesh.TriangleIndices.Count / 3:N0} triangles";
        ShowOnly(ModelSurface);
        FormatBadge.Text = asset.Extension.TrimStart('.').ToUpperInvariant();
    }

    private void ModelViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        modelDragging = true; lastModelPoint = e.GetPosition(ModelViewport); ModelViewport.CaptureMouse();
    }
    private void ModelViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!modelDragging || currentAsset?.Kind != AssetKind.Model) return;
        var point = e.GetPosition(ModelViewport);
        modelYaw += (point.X - lastModelPoint.X) * 0.8;
        modelPitch = Math.Clamp(modelPitch + (point.Y - lastModelPoint.Y) * 0.8, -89, 89);
        lastModelPoint = point;
        ShowModel(currentAsset);
    }
    private void ModelViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        modelDragging = false; ModelViewport.ReleaseMouseCapture();
    }
    private void ModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (currentAsset?.Kind != AssetKind.Model) return;
        modelZoom = Math.Clamp(modelZoom * (e.Delta > 0 ? 0.88 : 1.14), 2, 18);
        ShowModel(currentAsset);
    }

    private sealed class AssetEntry
    {
        public string Path { get; }
        public string Name { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public string Extension { get; }
        public string RelativePath { get; }
        public AssetKind Kind { get; }
        public string Icon => Kind switch { AssetKind.Image => "▧", AssetKind.Audio => "♫", AssetKind.Video => "▶", AssetKind.Model => "⬡", AssetKind.Text => "</>", _ => "•" };
        public string Details => $"{Extension.TrimStart('.').ToUpperInvariant()}  ·  {FormatSize(Size)}";

        public AssetEntry(string path, string root, long size, DateTime modified)
        {
            Path = path; Name = System.IO.Path.GetFileName(path); Size = size; Modified = modified;
            Extension = System.IO.Path.GetExtension(path);
            RelativePath = System.IO.Path.GetRelativePath(root, path);
            Kind = ImageExt.Contains(Extension) ? AssetKind.Image
                : AudioExt.Contains(Extension) ? AssetKind.Audio
                : VideoExt.Contains(Extension) ? AssetKind.Video
                : ModelExt.Contains(Extension) ? AssetKind.Model
                : UnityYamlExt.Contains(Extension) || InfoExt.Contains(Extension) ? AssetKind.Text
                : TextExt.Contains(Extension) && size <= 32 * 1024 * 1024 ? AssetKind.Text
                : AssetKind.Other;
        }

        public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / (1024d * 1024):0.#} MB";
    }

    private enum AssetKind { Image, Audio, Video, Model, Text, Other }

    private sealed class ExplorerNode(string name, string fullPath, AssetEntry? asset, bool isRoot = false)
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public AssetEntry? Asset { get; } = asset;
        public bool IsRoot { get; } = isRoot;
        public bool IsExpanded { get; set; }
        public ObservableCollection<ExplorerNode> Children { get; } = new();
        public string Icon => Asset is null ? "📁" : Asset.Icon;
        public string Details => Asset?.Details ?? (Children.Count == 0 ? "empty folder" : $"{Children.Count} items");

        public void SortChildren()
        {
            var sorted = Children.OrderBy(node => node.Asset is not null).ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Children.Clear();
            foreach (var child in sorted) Children.Add(child);
        }
    }

    private static class TgaReader
    {
        public static BitmapSource Load(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 18) throw new InvalidDataException("The TGA header is incomplete.");
            var idLength = data[0];
            var colorMap = data[1];
            var imageType = data[2];
            var width = BitConverter.ToUInt16(data, 12);
            var height = BitConverter.ToUInt16(data, 14);
            var bits = data[16];
            var topOrigin = (data[17] & 0x20) != 0;
            var rightOrigin = (data[17] & 0x10) != 0;
            if (colorMap != 0 || width == 0 || height == 0 || !(imageType is 2 or 3 or 10 or 11) || !(bits is 8 or 24 or 32)
                || (bits == 8 && imageType is 2 or 10))
                throw new NotSupportedException("TGA preview supports uncompressed or RLE grayscale, RGB and RGBA images.");
            var pixelBytes = bits / 8;
            var offset = 18 + idLength;
            var count = (int)width * height;
            var raw = new byte[count * pixelBytes];
            var outputPixel = 0;
            while (outputPixel < count)
            {
                var runLength = 1;
                var repeated = false;
                if (imageType is 10 or 11)
                {
                    if (offset >= data.Length) throw new InvalidDataException("The TGA pixel stream ended early.");
                    var packet = data[offset++];
                    runLength = (packet & 0x7F) + 1;
                    repeated = (packet & 0x80) != 0;
                }
                runLength = Math.Min(runLength, count - outputPixel);
                if (repeated)
                {
                    if (offset + pixelBytes > data.Length) throw new InvalidDataException("The TGA pixel stream ended early.");
                    for (var i = 0; i < runLength; i++) Buffer.BlockCopy(data, offset, raw, (outputPixel + i) * pixelBytes, pixelBytes);
                    outputPixel += runLength; offset += pixelBytes;
                }
                else
                {
                    var byteCount = runLength * pixelBytes;
                    if (offset + byteCount > data.Length) throw new InvalidDataException("The TGA pixel stream ended early.");
                    Buffer.BlockCopy(data, offset, raw, outputPixel * pixelBytes, byteCount);
                    outputPixel += runLength; offset += byteCount;
                }
            }

            var pixels = new byte[count * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sourceX = rightOrigin ? width - 1 - x : x;
                var sourceY = topOrigin ? y : height - 1 - y;
                var sourceIndex = (sourceY * width + sourceX) * pixelBytes;
                var targetIndex = (y * width + x) * 4;
                if (bits == 8)
                {
                    pixels[targetIndex] = pixels[targetIndex + 1] = pixels[targetIndex + 2] = raw[sourceIndex];
                    pixels[targetIndex + 3] = 255;
                }
                else
                {
                    pixels[targetIndex] = raw[sourceIndex];
                    pixels[targetIndex + 1] = raw[sourceIndex + 1];
                    pixels[targetIndex + 2] = raw[sourceIndex + 2];
                    pixels[targetIndex + 3] = bits == 32 ? raw[sourceIndex + 3] : (byte)255;
                }
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private enum AssetModelPlaceholder { None }
    private static class ModelReader
    {
        private sealed class RawModel
        {
            public List<Point3D> Vertices { get; } = new();
            public List<int> Indices { get; } = new();
        }

        public static MeshGeometry3D Load(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var raw = ext switch
            {
                ".obj" => ReadObj(path),
                ".stl" => ReadStl(path),
                ".ply" => ReadPly(path),
                ".off" => ReadOff(path),
                ".dae" => ReadDae(path),
                ".gltf" => ReadGltf(path),
                ".glb" => ReadGlb(path),
                _ => throw new NotSupportedException($"No model reader for {ext}.")
            };
            return BuildMesh(raw);
        }

        private static MeshGeometry3D BuildMesh(RawModel raw)
        {
            if (raw.Vertices.Count == 0 || raw.Indices.Count < 3) throw new InvalidDataException("No readable triangle geometry was found.");
            var min = new Point3D(raw.Vertices.Min(p => p.X), raw.Vertices.Min(p => p.Y), raw.Vertices.Min(p => p.Z));
            var max = new Point3D(raw.Vertices.Max(p => p.X), raw.Vertices.Max(p => p.Y), raw.Vertices.Max(p => p.Z));
            var center = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            var span = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
            if (span < 1e-6) span = 1;
            var mesh = new MeshGeometry3D();
            for (var i = 0; i + 2 < raw.Indices.Count; i += 3)
            {
                var a = Normalize(raw.Vertices[raw.Indices[i]], center, span);
                var b = Normalize(raw.Vertices[raw.Indices[i + 1]], center, span);
                var c = Normalize(raw.Vertices[raw.Indices[i + 2]], center, span);
                var normal = Vector3D.CrossProduct(b - a, c - a);
                if (normal.LengthSquared < 1e-10) normal = new Vector3D(0, 1, 0);
                normal.Normalize();
                var start = mesh.Positions.Count;
                mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
                mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
                mesh.Normals.Add(normal); mesh.Normals.Add(normal); mesh.Normals.Add(normal);
            }
            return mesh;
        }

        private static Point3D Normalize(Point3D p, Point3D center, double span) =>
            new((p.X - center.X) * 2.8 / span, (p.Y - center.Y) * 2.8 / span, (p.Z - center.Z) * 2.8 / span);

        private static RawModel ReadObj(string path)
        {
            var raw = new RawModel();
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("v ", StringComparison.Ordinal))
                {
                    var fields = Split(trimmed);
                    if (fields.Length >= 4) raw.Vertices.Add(new Point3D(Number(fields[1]), Number(fields[2]), Number(fields[3])));
                }
                else if (trimmed.StartsWith("f ", StringComparison.Ordinal))
                {
                    var face = new List<int>();
                    foreach (var token in Split(trimmed).Skip(1))
                    {
                        if (!int.TryParse(token.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0) continue;
                        index = index > 0 ? index - 1 : raw.Vertices.Count + index;
                        if (index >= 0 && index < raw.Vertices.Count) face.Add(index);
                    }
                    AddFace(raw, face);
                }
            }
            return raw;
        }

        private static RawModel ReadStl(string path)
        {
            var raw = new RawModel();
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length >= 84)
            {
                stream.Position = 80;
                var count = reader.ReadUInt32();
                if (count > 0 && 84L + count * 50L <= stream.Length)
                {
                    stream.Position = 84;
                    for (uint i = 0; i < count; i++)
                    {
                        reader.ReadBytes(12);
                        var start = raw.Vertices.Count;
                        for (var v = 0; v < 3; v++)
                        {
                            raw.Vertices.Add(new Point3D(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                            raw.Indices.Add(start + v);
                        }
                        reader.ReadUInt16();
                    }
                    return raw;
                }
            }

            var points = new List<int>(3);
            foreach (var line in File.ReadLines(path))
            {
                var fields = Split(line.Trim());
                if (fields.Length < 4 || !fields[0].Equals("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                points.Add(raw.Vertices.Count);
                raw.Vertices.Add(new Point3D(Number(fields[1]), Number(fields[2]), Number(fields[3])));
                if (points.Count == 3) { AddFace(raw, points); points.Clear(); }
            }
            return raw;
        }

        private static RawModel ReadOff(string path)
        {
            var tokens = File.ReadLines(path).Select(line => line.Split('#', 2)[0]).SelectMany(Split).ToArray();
            var cursor = 0;
            if (tokens.Length == 0 || !tokens[cursor++].TrimStart('\uFEFF').Equals("OFF", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Expected an OFF header.");
            var vertexCount = Integer(tokens[cursor++]);
            var faceCount = Integer(tokens[cursor++]);
            cursor++;
            var raw = new RawModel();
            for (var i = 0; i < vertexCount; i++)
                raw.Vertices.Add(new Point3D(Number(tokens[cursor++]), Number(tokens[cursor++]), Number(tokens[cursor++])));
            for (var i = 0; i < faceCount; i++)
            {
                var count = Integer(tokens[cursor++]);
                var face = new int[count];
                for (var j = 0; j < count; j++) face[j] = Integer(tokens[cursor++]);
                AddFace(raw, face);
            }
            return raw;
        }

        private static RawModel ReadPly(string path)
        {
            using var reader = new StreamReader(path);
            if (!string.Equals(reader.ReadLine()?.Trim(), "ply", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Expected a PLY header.");
            var ascii = false;
            var vertexCount = 0;
            var faceCount = 0;
            var inVertices = false;
            var xColumn = -1; var yColumn = -1; var zColumn = -1; var column = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var parts = Split(line);
                if (parts.Length == 0) continue;
                if (parts[0] == "format") ascii = parts.Length > 1 && parts[1] == "ascii";
                else if (parts[0] == "element")
                {
                    inVertices = parts.Length > 1 && parts[1] == "vertex";
                    if (inVertices) vertexCount = Integer(parts[2]);
                    if (parts.Length > 2 && parts[1] == "face") faceCount = Integer(parts[2]);
                    column = 0;
                }
                else if (parts[0] == "property" && inVertices && parts.Length >= 3 && parts[1] != "list")
                {
                    if (parts[^1] == "x") xColumn = column;
                    if (parts[^1] == "y") yColumn = column;
                    if (parts[^1] == "z") zColumn = column;
                    column++;
                }
                else if (parts[0] == "end_header") break;
            }
            if (!ascii) throw new NotSupportedException("PLY preview supports ASCII PLY files.");
            if (vertexCount <= 0 || faceCount < 0 || xColumn < 0 || yColumn < 0 || zColumn < 0)
                throw new InvalidDataException("The PLY header is missing vertex coordinates.");
            var raw = new RawModel();
            for (var i = 0; i < vertexCount; i++)
            {
                var parts = Split(reader.ReadLine() ?? throw new InvalidDataException("PLY ended in its vertex table."));
                raw.Vertices.Add(new Point3D(Number(parts[xColumn]), Number(parts[yColumn]), Number(parts[zColumn])));
            }
            for (var i = 0; i < faceCount; i++)
            {
                var parts = Split(reader.ReadLine() ?? throw new InvalidDataException("PLY ended in its face table."));
                var count = Integer(parts[0]);
                var face = new int[count];
                for (var j = 0; j < count; j++) face[j] = Integer(parts[j + 1]);
                AddFace(raw, face);
            }
            return raw;
        }

        private static RawModel ReadDae(string path)
        {
            var document = XDocument.Load(path);
            var sources = new Dictionary<string, (double[] Values, int Stride, int Offset)>(StringComparer.Ordinal);
            foreach (var source in document.Descendants().Where(element => element.Name.LocalName == "source"))
            {
                var id = (string?)source.Attribute("id");
                var array = source.Elements().FirstOrDefault(element => element.Name.LocalName == "float_array");
                if (string.IsNullOrEmpty(id) || array is null) continue;
                var accessor = source.Descendants().FirstOrDefault(element => element.Name.LocalName == "accessor");
                var stride = (int?)accessor?.Attribute("stride") ?? 3;
                var offset = (int?)accessor?.Attribute("offset") ?? 0;
                sources[id] = (Split(array.Value).Select(Number).ToArray(), stride, offset);
            }

            var positionSources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var vertices in document.Descendants().Where(element => element.Name.LocalName == "vertices"))
            {
                var id = (string?)vertices.Attribute("id");
                var input = vertices.Elements().FirstOrDefault(element => element.Name.LocalName == "input" && (string?)element.Attribute("semantic") == "POSITION");
                var source = ((string?)input?.Attribute("source"))?.TrimStart('#');
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(source)) positionSources[id] = source;
            }

            var raw = new RawModel();
            foreach (var primitive in document.Descendants().Where(element => element.Name.LocalName is "triangles" or "polylist"))
            {
                var inputs = primitive.Elements().Where(element => element.Name.LocalName == "input").ToArray();
                if (inputs.Length == 0) continue;
                var stride = inputs.Max(input => (int?)input.Attribute("offset") ?? 0) + 1;
                var positionInput = inputs.FirstOrDefault(input => (string?)input.Attribute("semantic") is "VERTEX" or "POSITION");
                if (positionInput is null) continue;
                var semantic = (string?)positionInput.Attribute("semantic");
                var sourceId = ((string?)positionInput.Attribute("source"))?.TrimStart('#') ?? "";
                if (semantic == "VERTEX" && positionSources.TryGetValue(sourceId, out var resolved)) sourceId = resolved;
                if (!sources.TryGetValue(sourceId, out var positionData)) continue;
                var positionOffset = (int?)positionInput.Attribute("offset") ?? 0;
                var packed = primitive.Elements().Where(element => element.Name.LocalName == "p").SelectMany(element => Split(element.Value).Select(Integer)).ToArray();
                var polygonCounts = primitive.Elements().FirstOrDefault(element => element.Name.LocalName == "vcount");
                var faceSizes = polygonCounts is null
                    ? Enumerable.Repeat(3, packed.Length / (stride * 3)).ToArray()
                    : Split(polygonCounts.Value).Select(Integer).ToArray();
                var cursor = 0;
                foreach (var faceSize in faceSizes)
                {
                    var face = new List<int>(faceSize);
                    for (var vertex = 0; vertex < faceSize; vertex++)
                    {
                        var packedIndex = cursor + vertex * stride + positionOffset;
                        if (packedIndex >= packed.Length) throw new InvalidDataException("A Collada face index list is incomplete.");
                        var sourceIndex = positionData.Offset + packed[packedIndex];
                        var valueIndex = sourceIndex * positionData.Stride;
                        if (valueIndex < 0 || valueIndex + 2 >= positionData.Values.Length) throw new InvalidDataException("A Collada vertex points outside its position array.");
                        face.Add(raw.Vertices.Count);
                        raw.Vertices.Add(new Point3D(positionData.Values[valueIndex], positionData.Values[valueIndex + 1], positionData.Values[valueIndex + 2]));
                    }
                    AddFace(raw, face);
                    cursor += faceSize * stride;
                }
            }
            return raw;
        }

        private static RawModel ReadGlb(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != 0x46546C67) throw new InvalidDataException("Invalid GLB header.");
            var version = reader.ReadUInt32();
            var totalLength = reader.ReadUInt32();
            if (version != 2 || totalLength > stream.Length) throw new NotSupportedException("Only glTF binary version 2 is supported.");
            byte[]? jsonBytes = null;
            byte[]? binary = null;
            while (stream.Position + 8 <= totalLength)
            {
                var length = reader.ReadUInt32();
                var type = reader.ReadUInt32();
                var data = reader.ReadBytes(checked((int)length));
                if (type == 0x4E4F534A) jsonBytes = data;
                else if (type == 0x004E4942) binary = data;
            }
            if (jsonBytes is null) throw new InvalidDataException("The GLB has no JSON chunk.");
            using var json = JsonDocument.Parse(jsonBytes);
            return ReadGltfDocument(json.RootElement, Path.GetDirectoryName(path)!, binary);
        }

        private static RawModel ReadGltf(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            return ReadGltfDocument(json.RootElement, Path.GetDirectoryName(path)!, null);
        }

        private static RawModel ReadGltfDocument(JsonElement root, string folder, byte[]? glbBinary)
        {
            var buffers = new List<byte[]>();
            if (root.TryGetProperty("buffers", out var bufferDefinitions))
            {
                var index = 0;
                foreach (var definition in bufferDefinitions.EnumerateArray())
                {
                    if (definition.TryGetProperty("uri", out var uriElement))
                    {
                        var uri = uriElement.GetString() ?? "";
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            var comma = uri.IndexOf(',');
                            if (comma < 0) throw new InvalidDataException("Invalid embedded glTF buffer URI.");
                            buffers.Add(Convert.FromBase64String(uri[(comma + 1)..]));
                        }
                        else buffers.Add(File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(uri))));
                    }
                    else if (index == 0 && glbBinary is not null) buffers.Add(glbBinary);
                    else throw new InvalidDataException("A glTF buffer is missing its URI or binary data.");
                    index++;
                }
            }
            if (!root.TryGetProperty("bufferViews", out var viewJson) || !root.TryGetProperty("accessors", out var accessorJson) || !root.TryGetProperty("meshes", out var meshJson))
                throw new InvalidDataException("This glTF is missing mesh buffer metadata.");
            var views = viewJson.EnumerateArray().ToArray();
            var accessors = accessorJson.EnumerateArray().ToArray();
            var raw = new RawModel();
            foreach (var mesh in meshJson.EnumerateArray())
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (primitive.TryGetProperty("extensions", out _)) throw new NotSupportedException("Compressed glTF mesh extensions aren't supported.");
                if (!primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var position))
                    continue;
                var first = raw.Vertices.Count;
                raw.Vertices.AddRange(ReadPositions(position.GetInt32(), accessors, views, buffers));
                var indices = primitive.TryGetProperty("indices", out var index) ? ReadIndices(index.GetInt32(), accessors, views, buffers) : Enumerable.Range(0, raw.Vertices.Count - first).ToArray();
                var mode = primitive.TryGetProperty("mode", out var modeJson) ? modeJson.GetInt32() : 4;
                if (mode == 4)
                {
                    for (var i = 0; i + 2 < indices.Length; i += 3) AddTriangle(raw, first, indices[i], indices[i + 1], indices[i + 2]);
                }
                else if (mode == 5)
                {
                    for (var i = 0; i + 2 < indices.Length; i++)
                    {
                        var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
                        if ((i & 1) != 0) (a, b) = (b, a);
                        AddTriangle(raw, first, a, b, c);
                    }
                }
                else if (mode == 6)
                {
                    for (var i = 1; i + 1 < indices.Length; i++) AddTriangle(raw, first, indices[0], indices[i], indices[i + 1]);
                }
            }
            return raw;
        }

        private static IEnumerable<Point3D> ReadPositions(int id, JsonElement[] accessors, JsonElement[] views, List<byte[]> buffers)
        {
            var accessor = accessors[id];
            if (accessor.GetProperty("componentType").GetInt32() != 5126 || accessor.GetProperty("type").GetString() != "VEC3")
                throw new NotSupportedException("glTF positions must be float VEC3 data.");
            var count = accessor.GetProperty("count").GetInt32();
            var (data, offset, stride) = AccessorData(accessor, views, buffers, 12);
            var vertices = new List<Point3D>(count);
            for (var i = 0; i < count; i++)
            {
                var at = offset + i * stride;
                if (at < 0 || at + 12 > data.Length) throw new InvalidDataException("A glTF position buffer is truncated.");
                vertices.Add(new Point3D(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8)));
            }
            return vertices;
        }

        private static int[] ReadIndices(int id, JsonElement[] accessors, JsonElement[] views, List<byte[]> buffers)
        {
            var accessor = accessors[id];
            if (accessor.GetProperty("type").GetString() != "SCALAR") throw new InvalidDataException("glTF indices must be scalar.");
            var componentType = accessor.GetProperty("componentType").GetInt32();
            var width = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException("Unsupported glTF index size.") };
            var count = accessor.GetProperty("count").GetInt32();
            var (data, offset, stride) = AccessorData(accessor, views, buffers, width);
            var indices = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = offset + i * stride;
                if (at < 0 || at + width > data.Length) throw new InvalidDataException("A glTF index buffer is truncated.");
                indices[i] = componentType switch { 5121 => data[at], 5123 => BitConverter.ToUInt16(data, at), 5125 => checked((int)BitConverter.ToUInt32(data, at)), _ => 0 };
            }
            return indices;
        }

        private static (byte[] Data, int Offset, int Stride) AccessorData(JsonElement accessor, JsonElement[] views, List<byte[]> buffers, int size)
        {
            if (!accessor.TryGetProperty("bufferView", out var viewIndex)) throw new NotSupportedException("Sparse glTF accessors aren't supported.");
            var view = views[viewIndex.GetInt32()];
            var data = buffers[view.GetProperty("buffer").GetInt32()];
            var viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            var accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            var stride = view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : size;
            return (data, viewOffset + accessorOffset, stride);
        }

        private static void AddTriangle(RawModel raw, int first, int a, int b, int c)
        {
            raw.Indices.Add(first + a); raw.Indices.Add(first + b); raw.Indices.Add(first + c);
        }

        private static void AddFace(RawModel raw, IReadOnlyList<int> face)
        {
            if (face.Any(index => index < 0 || index >= raw.Vertices.Count)) throw new InvalidDataException("A model face references a missing vertex.");
            for (var i = 1; i + 1 < face.Count; i++)
            {
                raw.Indices.Add(face[0]); raw.Indices.Add(face[i]); raw.Indices.Add(face[i + 1]);
            }
        }

        private static string[] Split(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        private static int Integer(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
