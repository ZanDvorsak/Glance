using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Glance.Core.Storage;

/// <summary>
/// Saves files so that the name always holds either the whole old content or the whole new content, never a
/// half-written file: the new content goes to a temporary file in the same folder, is flushed to the disk, and is then
/// renamed over the old one (a rename within one NTFS volume is all or nothing). A crash, a power cut or an error while
/// writing leaves the old file as it was.
/// <para>The rename uses POSIX semantics where Windows supports it (Windows 10 1709 and later, on NTFS), so a program
/// that holds the old file open while allowing it to be deleted (many indexers, sync and backup tools) no longer blocks
/// the save; elsewhere it falls back to <c>MoveFileEx</c> with write-through. A handle that does not allow deleting
/// still blocks the rename; that is retried for <see cref="RetryBudget"/> and then reported.</para>
/// <para>Temporary files are named <c>&lt;name&gt;.&lt;32 hex digits&gt;.tmp</c> (and <c>&lt;name&gt;.bak.&lt;32 hex&gt;.tmp</c>
/// for the backup copy), so a watcher for <c>*.json</c> only sees the final rename (as a <c>Renamed</c> event; with the
/// <c>MoveFileEx</c> fallback a <c>Deleted</c> event for the old file comes first). Ones older than an hour are left over
/// from a crash and are deleted by the next save of the same file.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>How long a save or a read keeps trying while another program has the file open. Tests make it shorter.</summary>
    internal static TimeSpan RetryBudget { get; set; } = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    /// <summary>A temporary file this old cannot belong to a save that is still running.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>Where <see cref="Write"/> keeps the previous version when asked to.</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte order mark (what <see cref="File.WriteAllText(string, string?)"/> writes).</summary>
    public static void WriteAllText(string path, string text, bool keepBackup = false) =>
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text);
        }, keepBackup);

    /// <summary>
    /// Replaces <paramref name="path"/> with what <paramref name="write"/> writes, all or nothing. If anything fails,
    /// the old file is untouched, no temporary file is left behind, and the error is thrown.
    /// <para>With <paramref name="keepBackup"/>, the file being replaced is also kept as <see cref="BackupPath"/> (one copy,
    /// overwritten by the next save). That is best effort: if the copy cannot be made (the old file cannot be read, the
    /// disk is full), the save goes ahead without it.</para>
    /// </summary>
    public static void Write(string path, Action<Stream> write, bool keepBackup = false)
    {
        var target = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        CleanUpStale(target);

        var temp = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}.tmp");
        var backupTemp = keepBackup ? TryCopyForBackup(target, dir, name) : null;
        var renamed = false;
        try
        {
            using (var fs = CreateTemp(temp))
            {
                write(fs);
                fs.Flush(flushToDisk: true);   // the content is on the disk before the name points at it
                renamed = Retry(() => TryPosixRename(fs.SafeFileHandle, target), ex => IsBusyForReplace(ex, target));
                if (renamed)
                {
                    try { fs.Flush(flushToDisk: true); } catch { /* the rename happened; this only hurries it to the disk */ }
                }
            }
            if (!renamed)   // POSIX rename not supported here (older Windows, FAT, some network shares)
                Retry(() => MoveReplace(temp, target), ex => IsBusyForReplace(ex, target));
        }
        catch
        {
            if (!renamed) TryDelete(temp);
            if (backupTemp is not null) TryDelete(backupTemp);
            throw;
        }

        if (backupTemp is not null)
        {
            // the save has happened; failing to keep the backup is not a reason to report it as failed
            try { MoveReplace(backupTemp, BackupPath(target)); }
            catch { TryDelete(backupTemp); }
        }
    }

    /// <summary>A copy of the file about to be replaced, or null when there is none or it cannot be made. Never waits.</summary>
    private static string? TryCopyForBackup(string target, string dir, string name)
    {
        if (!File.Exists(target)) return null;
        var copy = Path.Combine(dir, $"{name}.bak.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(target, copy);
            File.SetAttributes(copy, FileAttributes.Normal);   // a read-only original must not make the copy undeletable
            return copy;
        }
        catch
        {
            TryDelete(copy);
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> and tries again for <see cref="RetryBudget"/> while the file is busy (a sharing or
    /// lock violation: another program has it open) or does not parse yet (another program is halfway through writing
    /// it in place). Anything else (missing file, access denied, disk errors) fails at once.
    /// </summary>
    public static T ReadWithRetry<T>(Func<T> read) => Retry(read, ex => IsSharingOrLock(ex) || ex is JsonException);

    /// <summary>
    /// Deletes temporary files that a crash left next to <paramref name="path"/>: only names this class makes
    /// (<c>&lt;name&gt;.&lt;32 hex&gt;.tmp</c>, <c>&lt;name&gt;.bak.&lt;32 hex&gt;.tmp</c>) and only ones older than
    /// <see cref="StaleAfter"/>, so a save that is running in another process keeps its file. Never throws.
    /// </summary>
    public static void CleanUpStale(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var name = Path.GetFileName(path);
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var file in Directory.EnumerateFiles(dir, name + ".*.tmp"))
            {
                var middle = Path.GetFileName(file)[(name.Length + 1)..^4];   // between "<name>." and ".tmp"
                if (middle.StartsWith("bak.", StringComparison.Ordinal)) middle = middle[4..];
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigitLower)) continue;
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
            }
        }
        catch { /* tidying up must never break a save or a load */ }
    }

    // ---- retries ------------------------------------------------------------------------------------------------

    private static void Retry(Action action, Func<Exception, bool> worthRetrying) => Retry<object?>(() => { action(); return null; }, worthRetrying);

    private static T Retry<T>(Func<T> operation, Func<Exception, bool> worthRetrying)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return operation(); }
            catch (Exception ex) when (worthRetrying(ex) && watch.Elapsed < RetryBudget) { Thread.Sleep(RetryDelay); }
        }
    }

    private const int ErrorAccessDenied = 5, ErrorSharingViolation = 32, ErrorLockViolation = 33;
    private const int ErrorInvalidFunction = 1, ErrorNotSupported = 50, ErrorInvalidParameter = 87;

    private static bool IsSharingOrLock(Exception ex) =>
        ex is IOException && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation && (ex.HResult >> 16 & 0x1FFF) == 7;

    /// <summary>
    /// The rename onto an existing file reports a file that another program holds open as a sharing violation or as
    /// "access denied". A read-only target also says "access denied", and that does not clear by waiting.
    /// </summary>
    private static bool IsBusyForReplace(Exception ex, string target)
    {
        if (IsSharingOrLock(ex)) return true;
        if (ex is not UnauthorizedAccessException) return false;
        try { return File.Exists(target) && !File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly); }
        catch { return false; }
    }

    // ---- Win32 --------------------------------------------------------------------------------------------------

    private const uint GenericWrite = 0x40000000, Delete = 0x00010000;
    private const uint CreateNew = 1, FileAttributeNormal = 0x80;
    private const int FileRenameInfoEx = 22;
    private const uint FileRenameFlagReplaceIfExists = 0x1, FileRenameFlagPosixSemantics = 0x2;
    private const uint MoveFileReplaceExisting = 0x1, MoveFileWriteThrough = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    /// <summary>The temporary file, opened with the DELETE right so it can later be renamed through its own handle.</summary>
    private static FileStream CreateTemp(string temp)
    {
        var handle = CreateFileW(temp, GenericWrite | Delete, FileShare.None, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid) throw Error(Marshal.GetLastWin32Error(), temp, "create");
        return new FileStream(handle, FileAccess.Write);
    }

    /// <summary>
    /// Renames the open temporary file over <paramref name="target"/> with POSIX semantics. Returns false when this
    /// Windows version or file system does not support it; throws on any other error.
    /// </summary>
    private static bool TryPosixRename(SafeFileHandle file, string target)
    {
        // FILE_RENAME_INFO: DWORD Flags; HANDLE RootDirectory; DWORD FileNameLength; WCHAR FileName[]
        var rootOffset = IntPtr.Size;                     // Flags is padded to pointer alignment
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var nameBytes = target.Length * sizeof(char);
        var size = nameOffset + nameBytes + sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics)));
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
            Marshal.Copy(target.ToCharArray(), 0, buffer + nameOffset, target.Length);
            if (SetFileInformationByHandle(file, FileRenameInfoEx, buffer, (uint)size)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction) return false;
            throw Error(error, target, "replace");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Renames <paramref name="from"/> over <paramref name="to"/>; returns only once the rename is on the disk.</summary>
    private static void MoveReplace(string from, string to)
    {
        if (!MoveFileExW(from, to, MoveFileReplaceExisting | MoveFileWriteThrough)) throw Error(Marshal.GetLastWin32Error(), to, "replace");
    }

    private static Exception Error(int error, string path, string what)
    {
        var reason = new Win32Exception(error).Message;
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException($"Could not {what} '{path}': {reason} (it may be read-only, or open in another program).")
            : new IOException($"Could not {what} '{path}': {reason}", unchecked((int)0x80070000) | error);
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* best effort */ } }
}
