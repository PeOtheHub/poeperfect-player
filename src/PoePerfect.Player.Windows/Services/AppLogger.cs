using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace APTV.Services;

public sealed class AppLogger
{
    private const int RetainedLogFileCount = 20;
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s'""<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly object _syncRoot = new();
    private bool _sessionHeaderWritten;

    private AppLogger()
    {
        LogDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APTV",
            "logs");

        Directory.CreateDirectory(LogDirectoryPath);
        CleanupOldLogs();

        var sessionStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        CurrentLogFilePath = Path.Combine(LogDirectoryPath, $"PoePerfectPlayer-{sessionStamp}.log");
    }

    public static AppLogger Instance { get; } = new();

    public string LogDirectoryPath { get; }

    public string CurrentLogFilePath { get; }

    public void Info(string message, [CallerMemberName] string caller = "")
    {
        Write("INFO", message, null, caller);
    }

    public void Warning(string message, [CallerMemberName] string caller = "")
    {
        Write("WARN", message, null, caller);
    }

    public void Error(string message, Exception? exception = null, [CallerMemberName] string caller = "")
    {
        Write("ERROR", message, exception, caller);
    }

    public string DescribeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "<empty>";
        }

        var trimmed = source.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var authority = string.IsNullOrWhiteSpace(uri.Host)
                ? uri.Scheme
                : uri.IsDefaultPort
                    ? uri.Host
                    : $"{uri.Host}:{uri.Port}";
            var absolutePath = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            var queryInfo = string.IsNullOrWhiteSpace(uri.Query)
                ? "query=no"
                : $"query=yes/{CountQueryParameters(uri.Query)}";
            return $"{uri.Scheme}://{authority}{absolutePath} ({queryInfo})";
        }

        if (Path.IsPathRooted(trimmed)
            || trimmed.Contains(Path.DirectorySeparatorChar)
            || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return $"file:{Path.GetFileName(trimmed)}";
        }

        return trimmed;
    }

    public string SanitizeSensitiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return UrlRegex.Replace(value, match => DescribeSource(match.Value));
    }

    private void Write(string level, string message, Exception? exception, string caller)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            builder.Append(" [").Append(level).Append(']');

            if (!string.IsNullOrWhiteSpace(caller))
            {
                builder.Append(" [").Append(caller).Append(']');
            }

            builder.Append(' ').AppendLine(SanitizeSensitiveText(message));

            if (exception is not null)
            {
                builder.AppendLine(SanitizeSensitiveText(exception.ToString()));
            }

            lock (_syncRoot)
            {
                EnsureSessionHeader();
                File.AppendAllText(CurrentLogFilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private void EnsureSessionHeader()
    {
        if (_sessionHeaderWritten)
        {
            return;
        }

        _sessionHeaderWritten = true;

        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var builder = new StringBuilder();
        builder.AppendLine("=== PoePerfect Player Session Log ===");
        builder.AppendLine($"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Version: {version}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"Runtime: {Environment.Version}");
        builder.AppendLine($"Process: {Environment.ProcessId}");
        builder.AppendLine();
        File.AppendAllText(CurrentLogFilePath, builder.ToString(), Encoding.UTF8);
    }

    private void CleanupOldLogs()
    {
        try
        {
            var directory = new DirectoryInfo(LogDirectoryPath);
            var oldLogs = directory
                .EnumerateFiles("*.log")
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(RetainedLogFileCount)
                .ToList();

            foreach (var logFile in oldLogs)
            {
                logFile.Delete();
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static int CountQueryParameters(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }
}
