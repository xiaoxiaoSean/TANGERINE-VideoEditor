using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Opens local EDL/XML exchange operations from the export workspace.</summary>
public partial class MainWindow
{
    private async Task ExportProjectExchangeAsync()
    {
        if (_projectIoInProgress || _exportInProgress || _autoSaveInProgress) return;
        var picker = new SaveFileDialog
        {
            Title = "导出项目交换文件",
            Filter = "TANGERINE XML 交换文件 (*.xml)|*.xml|CMX3600 EDL (*.edl)|*.edl",
            DefaultExt = ".xml", AddExtension = true,
            FileName = _project.Name + ".xml"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _projectIoInProgress = true;
            ProjectDocument source = _project;
            long revision = _projectRevision;
            long generation = _projectGeneration;
            ProjectDocument snapshot = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))
                ?? throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "无法复制当前项目。"));
            if (!ReferenceEquals(source, _project) || revision != _projectRevision || generation != _projectGeneration)
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                    "准备交换文件期间项目已修改，请重新导出。");
            if (Path.GetExtension(picker.FileName).Equals(".edl", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ProjectExchangeService.ExportEdlAsync(snapshot, picker.FileName));
            else await Task.Run(() => ProjectExchangeService.ExportXmlAsync(snapshot, picker.FileName));
            StatusText.Text = $"已导出交换文件：{Path.GetFileName(picker.FileName)}";
        }
        catch (OperationCanceledException) { StatusText.Text = "项目交换导出已取消"; }
        catch (ProjectExchangeException exception) { ShowExchangeError(exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { ShowExchangeError(new ProjectExchangeException(ProjectExchangeStageCode.Write, "导出项目交换文件失败。", exception)); }
        finally { _projectIoInProgress = false; }
    }

    private async Task ImportProjectExchangeAsync()
    {
        if (_projectIoInProgress || _exportInProgress || _autoSaveInProgress) return;
        var picker = new OpenFileDialog
        {
            Title = "导入项目交换文件",
            Filter = "交换文件 (*.xml;*.edl)|*.xml;*.edl|TANGERINE XML (*.xml)|*.xml|CMX3600 EDL (*.edl)|*.edl",
            CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (!ConfirmDiscardChanges()) return;
        _projectIoInProgress = true;
        try
        {
            ProjectDocument imported = Path.GetExtension(picker.FileName).Equals(".edl", StringComparison.OrdinalIgnoreCase)
                ? await Task.Run(() => ProjectExchangeService.ImportEdlAsync(picker.FileName))
                : await Task.Run(() => ProjectExchangeService.ImportXmlAsync(picker.FileName));
            await Task.Run(() =>
            {
                string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(picker.FileName))!;
                foreach (MediaAsset asset in imported.Media)
                {
                    if (!Path.IsPathFullyQualified(asset.SourcePath))
                        asset.SourcePath = Path.GetFullPath(Path.Combine(baseDirectory, asset.SourcePath));
                    if (!string.IsNullOrWhiteSpace(asset.ProxyPath) && !Path.IsPathFullyQualified(asset.ProxyPath))
                        asset.ProxyPath = Path.GetFullPath(Path.Combine(baseDirectory, asset.ProxyPath));
                }
            });
            ProjectDocument outgoing = _project;
            _project = imported;
            _projectPath = null;
            _projectGeneration++;
            _projectRevision++;
            _isProjectDirty = true;
            _sourceMonitor?.Close();
            ResetEditorSession();
            RefreshEditorFromProject();
            _ = ReleaseProjectCacheSafelyAsync(outgoing);
            RefreshProjectChrome();
            StatusText.Text = $"已导入交换文件：{Path.GetFileName(picker.FileName)}";
        }
        catch (OperationCanceledException) { StatusText.Text = "项目交换导入已取消"; }
        catch (ProjectExchangeException exception) { ShowExchangeError(exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowExchangeError(new ProjectExchangeException(ProjectExchangeStageCode.Read, "导入项目交换文件失败。", exception)); }
        finally { _projectIoInProgress = false; }
    }

    private void ShowExchangeError(ProjectExchangeException exception)
    {
        StatusText.Text = $"交换文件操作失败 · EXCHANGE-{exception.StageCode}";
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：EXCHANGE-{exception.StageCode}",
            "项目交换失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
