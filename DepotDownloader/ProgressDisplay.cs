// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Threading;
using Spectre.Console;

namespace DepotDownloader;

static class ProgressDisplay
{
    private enum ProgressPhase { Validation, Download }

    private static readonly object sync = new();

    private static Func<(ulong downloaded, ulong total)> getProgress;
    private static Func<bool> getIsScanning;
    private static Func<bool> getHasDownloads;
    private static Func<ulong> getUpdateSize;
    private static Timer timer;

    private static bool enabled;
    private static bool active;
    private static ProgressPhase currentPhase = ProgressPhase.Validation;

    private static long lastSampleTimestamp;
    private static ulong lastSampleBytes;
    private static double smoothedBytesPerSecond;

    private static string lastBarText = "";

    private const int RedrawIntervalMs = 150;
    private const double SmoothingFactor = 0.25;
    private const int BarWidth = 28;

    public static void Start(Func<(ulong downloaded, ulong total)> progressSnapshot, Func<bool> isScanning, Func<bool> hasDownloads = null, Func<ulong> updateSize = null)
    {
        getProgress = progressSnapshot;
        getIsScanning = isScanning;
        getHasDownloads = hasDownloads;
        getUpdateSize = updateSize;

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            enabled = false;
            return;
        }

        var (supportsAnsi, legacyConsole) = AnsiDetector.Detect(stdError: false, upgrade: true);
        enabled = supportsAnsi && !legacyConsole;

        if (!enabled)
        {
            return;
        }

        lock (sync)
        {
            active = true;
            currentPhase = ProgressPhase.Validation;
            lastSampleTimestamp = Environment.TickCount64;
            lastSampleBytes = 0;
            smoothedBytesPerSecond = 0;
            lastBarText = "";

            Console.Write("\x1B[?25l");
            Redraw();
        }

        timer = new Timer(_ => Tick(), null, RedrawIntervalMs, RedrawIntervalMs);
    }

    public static void Stop()
    {
        if (!enabled)
        {
            return;
        }

        if (timer != null)
        {
            using var stopped = new ManualResetEvent(false);
            timer.Dispose(stopped);
            stopped.WaitOne();
            timer = null;
        }

        lock (sync)
        {
            if (active)
            {
                Console.Write("\r\x1B[2K");
                Console.Write("\x1B[?25h");
                active = false;
            }
        }

        enabled = false;
    }

    public static void WriteLine()
    {
        WriteLine(string.Empty);
    }

    public static void WriteLine(string message)
    {
        if (!enabled || !active)
        {
            Console.WriteLine(message);
            return;
        }

        lock (sync)
        {
            Console.Write("\r\x1B[2K");
            Console.WriteLine(message);
            Console.Write(lastBarText);
        }
    }

    public static void WriteLine(string format, params object[] args)
    {
        WriteLine(string.Format(format, args));
    }

    private static void Tick()
    {
        lock (sync)
        {
            if (!active)
            {
                return;
            }

            Redraw();
        }
    }

    private static void Redraw()
    {
        var (downloaded, total) = getProgress();
        var isScanning = getIsScanning != null && getIsScanning();
        var hasDownloads = getHasDownloads != null && getHasDownloads();

        // Auto-detect transition from validation to download phase
        if (currentPhase == ProgressPhase.Validation && !isScanning && hasDownloads)
        {
            currentPhase = ProgressPhase.Download;
            lastSampleTimestamp = Environment.TickCount64;
            lastSampleBytes = 0;
            smoothedBytesPerSecond = 0;
            lastBarText = "";
        }

        var now = Environment.TickCount64;
        var elapsedMs = now - lastSampleTimestamp;

        if (elapsedMs > 0)
        {
            var deltaBytes = downloaded > lastSampleBytes ? downloaded - lastSampleBytes : 0UL;
            var instantBytesPerSecond = deltaBytes / (elapsedMs / 1000.0);

            smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                ? instantBytesPerSecond
                : (SmoothingFactor * instantBytesPerSecond) + ((1 - SmoothingFactor) * smoothedBytesPerSecond);

            lastSampleBytes = downloaded;
            lastSampleTimestamp = now;
        }

        var percent = total > 0 ? Math.Clamp(downloaded / (float)total * 100f, 0f, 100f) : 0f;
        var filled = (int)(percent / 100f * BarWidth);

        var bar = "[" + new string('#', filled) + new string('-', BarWidth - filled) + "]";

        string stateTag;
        if (isScanning)
        {
            stateTag = "\x1B[33mValidating\x1B[0m";
        }
        else if (!hasDownloads)
        {
            stateTag = "\x1B[32mUp to date\x1B[0m";
        }
        else
        {
            stateTag = "\x1B[36mDownloading\x1B[0m";
        }

        // Only show update size when we're past validation and actually downloading
        var updateTag = "";
        if (!isScanning && hasDownloads && getUpdateSize != null)
        {
            var updateSize = getUpdateSize();
            if (updateSize > 0)
            {
                updateTag = $"  (Update: {FormatBytes(updateSize)})";
            }
        }

        var text = $"{stateTag}{updateTag}  {bar} {percent,6:0.00}%  {FormatBytes(downloaded)}/{FormatBytes(total)}  {FormatBytes((ulong)smoothedBytesPerSecond)}/s";

        lastBarText = text;
        Console.Write("\r\x1B[2K" + text);
    }

    /// <summary>
    /// Formats bytes into a human-readable string using binary units (KB, MB, GB, TiB).
    /// Steps at 1024 boundaries: 1024 B = 1 KB, 1024 KB = 1 MB, etc.
    /// </summary>
    public static string FormatBytes(ulong bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.00} {units[unit]}";
    }
}
