using System.Diagnostics;
using System.IO;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>The outcome of a finished external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The end of stderr, where encoders put the actual reason for a failure.
    /// The start of the stream is only a version banner.
    /// </summary>
    public string ErrorTail(int maximumLength = 300)
    {
        string text = StandardError.Trim();
        return text.Length > maximumLength ? text[^maximumLength..] : text;
    }
}

/// <summary>
/// Runs a command-line tool without the failure modes that repeat across the
/// editor's FFmpeg and Whisper calls. A redirected stream that is never read
/// fills its pipe, the tool blocks on its next write, and the wait never ends;
/// disposing a Process does not stop it, so a cancelled run keeps going in the
/// background. This type drains both streams and kills the tree on cancel.
/// </summary>
public static class ExternalProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动外部程序：{Path.GetFileName(fileName)}");

        // Start reading before waiting: waiting first is exactly the deadlock.
        // Cancellation is handled below, so the reads do not take the token;
        // a cancelled read would throw before the process could be killed.
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* it exited on its own */ }
            throw;
        }
        return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }
}
