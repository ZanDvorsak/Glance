namespace Glance.Core.Storage;

/// <summary>Seam for saving and reading the files the runner watches. Same behaviour as before for now.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text, bool keepBackup = false) => File.WriteAllText(path, text);

    public static void Write(string path, Action<Stream> write, bool keepBackup = false)
    {
        if (File.Exists(path)) File.Delete(path);
        using var fs = File.Create(path);
        write(fs);
    }

    public static T ReadWithRetry<T>(Func<T> read) => read();
}
