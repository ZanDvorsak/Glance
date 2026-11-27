using System.Diagnostics;

namespace Glance.Core.Media;

/// <summary>The decoder child process as <see cref="VideoSource"/> sees it, so tests can feed frames without ffmpeg.</summary>
internal interface IVideoProcess : IDisposable
{
    /// <summary>Raw BGRA frames, back to back.</summary>
    Stream Output { get; }

    /// <summary>Waits up to <paramref name="ms"/> for the process to end; true when it has.</summary>
    bool WaitForExit(int ms);

    /// <summary>The exit code once the process has ended, otherwise null.</summary>
    int? ExitCode { get; }

    void Kill();
}

/// <summary>Starts the real ffmpeg. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string.</summary>
internal sealed class FfmpegProcess : IVideoProcess
{
    private readonly Process _proc;

    private FfmpegProcess(Process proc) => _proc = proc;

    /// <param name="onErrorLine">Called with every non-empty stderr line (on a pool thread).</param>
    public static IVideoProcess Start(string exe, IReadOnlyList<string> args, Action<string> onErrorLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onErrorLine(e.Data); };
        proc.BeginErrorReadLine();
        return new FfmpegProcess(proc);
    }

    public Stream Output => _proc.StandardOutput.BaseStream;

    public bool WaitForExit(int ms)
    {
        if (!_proc.WaitForExit(ms)) return false;
        _proc.WaitForExit();   // returns at once now; also waits for the last stderr lines to be delivered
        return true;
    }

    public int? ExitCode { get { try { return _proc.HasExited ? _proc.ExitCode : null; } catch { return null; } } }

    // Kill() and not Kill(true): ffmpeg starts no child processes, and the tree walk lists every process on the PC (review L2)
    public void Kill() { try { if (!_proc.HasExited) _proc.Kill(); } catch { } }

    public void Dispose() => _proc.Dispose();
}
