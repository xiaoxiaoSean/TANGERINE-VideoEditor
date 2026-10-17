using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Batch export queue for rendering multiple jobs with priority management.
/// Allows queuing multiple exports and processing them sequentially or in parallel.
/// </summary>
public class ExportQueueService : INotifyPropertyChanged
{
    public enum JobStatus
    {
        Queued,
        Encoding,
        Completed,
        Failed,
        Cancelled
    }

    public sealed class ExportJob : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "导出任务";
        public ProjectDocument Project { get; set; } = null!;
        public VideoExportSettings Settings { get; set; } = null!;
        public string OutputPath { get; set; } = string.Empty;
        public int Priority { get; set; }

        private JobStatus _status = JobStatus.Queued;
        public JobStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(nameof(Progress)); }
        }

        private string _statusText = "等待中";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public DateTime QueuedTime { get; set; } = DateTime.Now;
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? ErrorMessage { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public ObservableCollection<ExportJob> Jobs { get; } = new();

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set { _isProcessing = value; OnPropertyChanged(nameof(IsProcessing)); }
    }

    private CancellationTokenSource? _cancellationTokenSource;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Adds a new export job to the queue.
    /// </summary>
    public ExportJob AddJob(ProjectDocument project, VideoExportSettings settings, string outputPath, int priority = 0)
    {
        var job = new ExportJob
        {
            Name = Path.GetFileName(outputPath),
            Project = project,
            Settings = settings,
            OutputPath = outputPath,
            Priority = priority,
            QueuedTime = DateTime.Now
        };

        // Insert by priority (higher priority first)
        int insertIndex = 0;
        for (int i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].Status == JobStatus.Queued && Jobs[i].Priority < priority)
            {
                insertIndex = i;
                break;
            }
            insertIndex = i + 1;
        }

        Jobs.Insert(insertIndex, job);
        return job;
    }

    /// <summary>
    /// Removes a job from the queue (only if queued or failed).
    /// </summary>
    public bool RemoveJob(ExportJob job)
    {
        if (job.Status is JobStatus.Encoding)
            return false;

        return Jobs.Remove(job);
    }

    /// <summary>
    /// Cancels an encoding job.
    /// </summary>
    public void CancelJob(ExportJob job)
    {
        if (job.Status == JobStatus.Encoding)
        {
            _cancellationTokenSource?.Cancel();
            job.Status = JobStatus.Cancelled;
            job.StatusText = "已取消";
        }
        else if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.StatusText = "已取消";
        }
    }

    /// <summary>
    /// Starts processing the export queue.
    /// </summary>
    public async Task StartProcessingAsync()
    {
        if (IsProcessing) return;

        IsProcessing = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            while (true)
            {
                // Get next queued job
                var nextJob = Jobs
                    .Where(j => j.Status == JobStatus.Queued)
                    .OrderByDescending(j => j.Priority)
                    .ThenBy(j => j.QueuedTime)
                    .FirstOrDefault();

                if (nextJob == null)
                    break; // No more jobs

                await ProcessJobAsync(nextJob, _cancellationTokenSource.Token);

                // Small delay between jobs
                await Task.Delay(500);
            }
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    /// <summary>
    /// Stops processing the queue after current job completes.
    /// </summary>
    public void StopProcessing()
    {
        IsProcessing = false;
    }

    private async Task ProcessJobAsync(ExportJob job, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Encoding;
        job.StatusText = "编码中...";
        job.StartTime = DateTime.Now;
        job.Progress = 0;

        try
        {
            var progress = new Progress<double>(p =>
            {
                job.Progress = p * 100;
                job.StatusText = $"编码中... {p:P0}";
            });

            await TimelineExporter.ExportMp4Async(
                job.Project,
                job.OutputPath,
                new Dictionary<string, string>(), // Empty title images
                progress,
                cancellationToken,
                job.Settings);

            job.Status = JobStatus.Completed;
            job.StatusText = "已完成";
            job.Progress = 100;
            job.EndTime = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.StatusText = "已取消";
            job.EndTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.StatusText = "失败";
            job.ErrorMessage = ex.Message;
            job.EndTime = DateTime.Now;
        }
    }

    /// <summary>
    /// Gets summary statistics for the queue.
    /// </summary>
    public (int Queued, int Encoding, int Completed, int Failed) GetStatistics()
    {
        return (
            Jobs.Count(j => j.Status == JobStatus.Queued),
            Jobs.Count(j => j.Status == JobStatus.Encoding),
            Jobs.Count(j => j.Status == JobStatus.Completed),
            Jobs.Count(j => j.Status == JobStatus.Failed)
        );
    }

    /// <summary>
    /// Clears all completed and failed jobs from the queue.
    /// </summary>
    public void ClearFinished()
    {
        var toRemove = Jobs.Where(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled).ToList();
        foreach (var job in toRemove)
            Jobs.Remove(job);
    }

    /// <summary>
    /// Retries all failed jobs.
    /// </summary>
    public void RetryFailed()
    {
        foreach (var job in Jobs.Where(j => j.Status == JobStatus.Failed))
        {
            job.Status = JobStatus.Queued;
            job.StatusText = "等待中";
            job.Progress = 0;
            job.ErrorMessage = null;
            job.StartTime = null;
            job.EndTime = null;
        }
    }
}
