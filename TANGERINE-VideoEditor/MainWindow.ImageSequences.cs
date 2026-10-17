using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Imports numbered stills as one local, editable video source.</summary>
public partial class MainWindow
{
    private bool _imageSequenceImportInProgress;

    private void InitializeImageSequenceCommands()
    {
        if (ImportMenuItem.Parent is MenuItem fileMenu)
        {
            var command = new MenuItem { Header = "导入图像序列…" };
            command.Click += async (_, _) => await ImportImageSequenceAsync();
            fileMenu.Items.Insert(fileMenu.Items.IndexOf(ImportMenuItem) + 1, command);
        }
    }

    private async Task ImportImageSequenceAsync()
    {
        if (_imageSequenceImportInProgress || _projectIoInProgress) return;
        var picker = new OpenFileDialog
        {
            Title = "选择图像序列的第一帧",
            Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp|所有文件|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        ProjectDocument target = _project;
        long generation = _projectGeneration;
        double fps = _project.Sequence.FramesPerSecond;
        if (!double.IsFinite(fps) || fps <= 0)
        { StatusText.Text = "图像序列帧率无效 · MED-SEQUENCE-VALIDATE"; return; }
        _imageSequenceImportInProgress = true;
        StatusText.Text = "正在生成图像序列媒体…";
        try
        {
            string source = picker.FileName;
            string[] frames = await Task.Run(() => DiscoverImageSequence(source));
            if (!ReferenceEquals(target, _project) || generation != _projectGeneration) return;
            if (frames.Length < 2)
            { StatusText.Text = "至少需要两个连续编号的图像 · MED-SEQUENCE-VALIDATE"; return; }
            // Encoding creates a temporary video before the project archive
            // receives it. Check that staging volume without blocking WPF.
            await ProjectFileService.EnsureImportCapacityAsync(frames);
            string output = Path.Combine(Path.GetTempPath(),
                $"tangerine-image-sequence-{Guid.NewGuid():N}.mp4");
            await ImageSequenceEncoder.EncodeAsync(frames, output, fps);
            if (!ReferenceEquals(target, _project) || generation != _projectGeneration)
            {
                await Task.Run(() => File.Delete(output));
                return;
            }
            IReadOnlyList<MediaAsset> imported = await ImportMediaPathsAsync([output]);
            if (imported.Count == 0)
            {
                // No asset owns this temporary file when the regular import
                // pipeline declines the result (for example, insufficient space).
                await Task.Run(() => File.Delete(output));
                StatusText.Text = "图像序列已生成，但未加入媒体区 · MED-SEQUENCE-IMPORT";
                return;
            }
            MediaAsset asset = imported[0];
            asset.Name = $"{Path.GetFileNameWithoutExtension(source)} · 图像序列";
            asset.Description = $"{frames.Length} 帧 · {fps.ToString("0.###", CultureInfo.CurrentCulture)} fps";
            asset.Tags.Add("图像序列");
            MarkProjectDirty();
            RefreshLibrary();
            if (_projectPath is not null && !await SaveProjectToAsync(_projectPath))
            { StatusText.Text = "图像序列已加入当前会话，但项目尚未保存 · MED-SEQUENCE-SAVE"; return; }
            StatusText.Text = $"已导入图像序列：{frames.Length} 帧";
        }
        catch (OperationCanceledException) { StatusText.Text = "图像序列导入已取消"; }
        catch (ProjectFileException exception)
        {
            ShowProjectError(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or ImageSequenceException)
        {
            string stage = exception is ImageSequenceException sequence
                ? $"MED-SEQUENCE-{sequence.StageCode}" : "MED-SEQUENCE-IMPORT";
            StatusText.Text = $"图像序列导入失败 · {stage}：{exception.Message}";
        }
        finally { _imageSequenceImportInProgress = false; }
    }

    private static string[] DiscoverImageSequence(string firstFrame)
    {
        string name = Path.GetFileNameWithoutExtension(firstFrame);
        Match match = Regex.Match(name, @"^(.*?)(\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success) return [];
        string prefix = match.Groups[1].Value;
        string extension = Path.GetExtension(firstFrame);
        int digits = match.Groups[2].Length;
        if (!long.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out long start))
            throw new ImageSequenceException(ImageSequenceStageCode.Validate,
                "文件名末尾的帧编号超出支持范围。");
        string directory = Path.GetDirectoryName(firstFrame)!;
        var frames = new List<string>();
        const int maximumFrames = 100000;
        for (int offset = 0; offset < maximumFrames; offset++)
        {
            if (start > long.MaxValue - offset) break;
            long number = start + offset;
            string path = Path.Combine(directory, prefix + number.ToString($"D{digits}", CultureInfo.InvariantCulture) + extension);
            if (!File.Exists(path)) break;
            frames.Add(path);
        }
        if (frames.Count == maximumFrames)
            throw new ImageSequenceException(ImageSequenceStageCode.Validate,
                "图像序列超过单次导入允许的 100000 帧。");
        // A later numbered image means that the selected run contains a gap.
        // Silently truncating at the first missing frame would discard footage.
        long next = start > long.MaxValue - frames.Count ? long.MaxValue : start + frames.Count;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) continue;
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!stem.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string suffix = stem[prefix.Length..];
            if (suffix.Length != digits || !long.TryParse(suffix, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long index)) continue;
            if (index > next)
                throw new ImageSequenceException(ImageSequenceStageCode.Validate,
                    $"图像序列缺少编号 {next.ToString($"D{digits}", CultureInfo.InvariantCulture)}。");
        }
        return frames.ToArray();
    }
}
