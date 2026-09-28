namespace Fabric.Deployment.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error, Success }

/// <summary>Minimal logging abstraction — swap for ILogger/Serilog in your host if you like.</summary>
public interface IDeployLogger
{
    void Log(LogLevel level, string message);
}

public static class DeployLoggerExtensions
{
    public static void Debug(this IDeployLogger l, string m) => l.Log(LogLevel.Debug, m);
    public static void Info(this IDeployLogger l, string m) => l.Log(LogLevel.Info, m);
    public static void Warn(this IDeployLogger l, string m) => l.Log(LogLevel.Warning, m);
    public static void Error(this IDeployLogger l, string m) => l.Log(LogLevel.Error, m);
    public static void Success(this IDeployLogger l, string m) => l.Log(LogLevel.Success, m);
}

public sealed class ConsoleDeployLogger : IDeployLogger
{
    private readonly bool _verbose;
    private readonly bool _azureDevOps;
    private static readonly object Gate = new();

    public ConsoleDeployLogger(bool verbose = false)
    {
        _verbose = verbose;
        _azureDevOps = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));
    }

    public void Log(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !_verbose) return;
        lock (Gate)
        {
            var prefix = level switch
            {
                LogLevel.Warning when _azureDevOps => "##vso[task.logissue type=warning]",
                LogLevel.Error when _azureDevOps => "##vso[task.logissue type=error]",
                LogLevel.Debug => "[debug] ",
                LogLevel.Warning => "[warn]  ",
                LogLevel.Error => "[error] ",
                LogLevel.Success => "[ ok ]  ",
                _ => "[info]  "
            };
            var color = level switch
            {
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Debug => ConsoleColor.DarkGray,
                _ => Console.ForegroundColor
            };
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {prefix}{message}");
            Console.ForegroundColor = old;
        }
    }
}
