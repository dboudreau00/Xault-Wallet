using System.Text;

namespace XaultWallet.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe logger. Writes timestamped lines to a rolling file.
///
/// IMPORTANT: callers must NEVER pass secrets (mnemonics, passwords, private keys,
/// RPC credentials) to the logger. Log high-level events and exception types/messages
/// only. Exception messages from this app are written not to leak secret material.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Point the logger at a directory. Safe to call more than once.</summary>
    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            lock (Gate)
            {
                _file = Path.Combine(directory, "xaultwallet.log");
            }
        }
        catch
        {
            // Logging must never take the app down. If we can't set up a file, we no-op.
            lock (Gate)
            {
                _file = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                if (_file is null)
                {
                    return;
                }

                RollIfNeeded();
                File.AppendAllText(_file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from logging.
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            if (_file is null)
            {
                return;
            }

            var info = new FileInfo(_file);
            if (info.Exists && info.Length > MaxBytes)
            {
                string old = _file + ".1";
                if (File.Exists(old))
                {
                    File.Delete(old);
                }

                File.Move(_file, old);
            }
        }
        catch
        {
            // ignore rotation failures
        }
    }
}
