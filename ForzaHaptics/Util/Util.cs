using System.Diagnostics;

namespace ForzaHaptics.Util;

/// <summary>Monotonic application clock in seconds since startup.</summary>
public static class Clock
{
    private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();
    public static double Now => Stopwatch.Elapsed.TotalSeconds;
}

public static class MathX
{
    public const float TwoPi = MathF.PI * 2f;

    public static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

    public static float Clamp(float x, float lo, float hi) => x < lo ? lo : (x > hi ? hi : x);

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Smooth 0→1 transition between edge0 and edge1.</summary>
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0) return x >= edge0 ? 1f : 0f;
        float t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    /// <summary>Single-pole smoothing coefficient for the time constant tau in seconds.</summary>
    public static float SmoothingCoef(float tauSeconds, float sampleRate)
    {
        if (tauSeconds <= 0f) return 1f;
        return 1f - MathF.Exp(-1f / (tauSeconds * sampleRate));
    }
}

/// <summary>
/// Console output with regular messages and an overwritable status line.
/// Thread-safe.
/// </summary>
public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error,
}

public static class Log
{
    private static readonly object Sync = new();
    private static int _statusLength;

    /// <summary>Every message, exposed for the window log. May be raised from any thread.</summary>
    public static event Action<LogLevel, string>? Message;

    public static void Info(string message) => Write(LogLevel.Info, message, ConsoleColor.Gray);
    public static void Ok(string message) => Write(LogLevel.Ok, message, ConsoleColor.Green);
    public static void Warn(string message) => Write(LogLevel.Warn, message, ConsoleColor.Yellow);
    public static void Error(string message) => Write(LogLevel.Error, message, ConsoleColor.Red);

    private static void Write(LogLevel level, string message, ConsoleColor color)
    {
        try { Message?.Invoke(level, message); } catch { /* Logging must not interrupt processing. */ }
        lock (Sync)
        {
            ClearStatus();
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = old;
        }
    }

    public static void Status(string line)
    {
        lock (Sync)
        {
            if (Console.IsOutputRedirected) return;
            int width = ConsoleWidth();
            if (line.Length > width - 1) line = line.Substring(0, width - 1);
            Console.Write("\r" + line.PadRight(Math.Max(_statusLength, line.Length)));
            _statusLength = line.Length;
        }
    }

    public static void EndStatus()
    {
        lock (Sync)
        {
            if (_statusLength > 0 && !Console.IsOutputRedirected) Console.WriteLine();
            _statusLength = 0;
        }
    }

    private static void ClearStatus()
    {
        if (_statusLength > 0 && !Console.IsOutputRedirected)
        {
            Console.Write("\r" + new string(' ', _statusLength) + "\r");
        }
        _statusLength = 0;
    }

    private static int ConsoleWidth()
    {
        try
        {
            int w = Console.WindowWidth;
            return w > 20 ? w : 120;
        }
        catch
        {
            return 120;
        }
    }
}
