using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QbtAutoUpdate.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimal logging abstraction. Messages must never contain secrets (passwords, API keys,
/// signed download URLs): callers log hosts and file names, not full URLs.
/// </summary>
public interface ILog
{
    void Write(LogLevel level, string message);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message) => log.Write(LogLevel.Warn, message);
    public static void Error(this ILog log, string message) => log.Write(LogLevel.Error, message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string message)
    {
    }
}

/// <summary>Fans a message out to several logs (file plus console in --verbose mode).</summary>
public sealed class CompositeLog(params ILog[] logs) : ILog
{
    public void Write(LogLevel level, string message)
    {
        foreach (ILog log in logs)
            log.Write(level, message);
    }
}

/// <summary>
/// Appends to "QbtAutoUpdate.log" and rotates by size, keeping at most <see cref="MaxFiles"/> files
/// (QbtAutoUpdate.log, QbtAutoUpdate.1.log … QbtAutoUpdate.9.log). Several processes of the same
/// user can log at once (scheduled check plus a notification click), so each write opens the file
/// briefly under a named mutex.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    public const int MaxFiles = 10;
    public const long MaxBytes = 1024 * 1024;

    private const string BaseName = "QbtAutoUpdate";

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly Mutex _mutex;

    public FileLog(string directory, LogLevel minLevel)
    {
        _directory = directory;
        _minLevel = minLevel;
        _mutex = new Mutex(false, MutexName(directory));
    }

    public string CurrentFile => Path.Combine(_directory, BaseName + ".log");

    public bool Enabled { get; set; } = true;

    public void Write(LogLevel level, string message)
    {
        if (level < _minLevel || !Enabled)
            return;

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{LevelTag(level)}] [{Environment.ProcessId}] {message}{Environment.NewLine}");

        bool owned = false;
        try
        {
            try
            {
                owned = _mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            Directory.CreateDirectory(_directory);
            RotateIfNeeded();
            File.AppendAllText(CurrentFile, line, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break an update. Nothing sensible to do here.
        }
        finally
        {
            if (owned)
                _mutex.ReleaseMutex();
        }
    }

    public void Dispose() => _mutex.Dispose();

    private void RotateIfNeeded()
    {
        var current = new FileInfo(CurrentFile);
        if (!current.Exists || current.Length < MaxBytes)
            return;

        string Numbered(int index) => Path.Combine(_directory, $"{BaseName}.{index}.log");

        File.Delete(Numbered(MaxFiles - 1));
        for (int index = MaxFiles - 2; index >= 1; index--)
        {
            if (File.Exists(Numbered(index)))
                File.Move(Numbered(index), Numbered(index + 1));
        }
        File.Move(CurrentFile, Numbered(1));
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    private static string MutexName(string directory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant()));
        return @"Local\QbtAutoUpdate.Log." + Convert.ToHexString(hash, 0, 8);
    }
}
