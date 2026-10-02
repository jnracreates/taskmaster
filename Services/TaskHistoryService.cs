using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TaskMaster.Services;

/// <summary>
/// Resolves the average completion time of a task by consulting, in order:
///   1. Jellyfin's own per-task history files (if present on disk)
///   2. TaskMaster's local timing store (samples from our own runs)
///   3. Zero (unknown)
///
/// The orchestrator uses the result to sort tasks "fastest first" or
/// "slowest first".
/// </summary>
public class TaskHistoryService
{
    private const int MaxHistoryFilesToScan = 20;

    private readonly string _dataPath;
    private readonly TaskTimingStore _store;

    public TaskHistoryService(IApplicationPaths paths, TaskTimingStore store)
    {
        _dataPath = paths.DataPath;
        _store = store;
    }

    /// <summary>
    /// Best-known average runtime for a task. Never throws; returns
    /// <see cref="TimeSpan.Zero"/> when nothing is known yet.
    /// </summary>
    /// <summary>
    /// Sort key for FastestFirst / SlowestFirst ordering. Known durations
    /// sort by their actual length. Unknown durations sort as if they were
    /// the longest possible task, so they never crowd out tasks whose
    /// runtime we actually know.
    /// </summary>
    public long GetSortKey(string taskKey)
    {
        var avg = GetAverage(taskKey);
        return avg > TimeSpan.Zero ? avg.Ticks : long.MaxValue;
    }

    public TimeSpan GetAverage(string taskKey)
    {
        if (string.IsNullOrEmpty(taskKey))
        {
            return TimeSpan.Zero;
        }

        var fromJellyfin = TryReadJellyfinHistory(taskKey);
        if (fromJellyfin.HasValue && fromJellyfin.Value > TimeSpan.Zero)
        {
            return fromJellyfin.Value;
        }

        var fromLocal = _store.GetAverage(taskKey);
        if (fromLocal.HasValue && fromLocal.Value > TimeSpan.Zero)
        {
            return fromLocal.Value;
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Human-readable source of the average, for diagnostics in the UI
    /// or the log. Values: "jellyfin", "taskmaster", "unknown".
    /// </summary>
    public string GetAverageSource(string taskKey)
    {
        if (string.IsNullOrEmpty(taskKey))
        {
            return "unknown";
        }

        var fromJellyfin = TryReadJellyfinHistory(taskKey);
        if (fromJellyfin.HasValue && fromJellyfin.Value > TimeSpan.Zero)
        {
            return "jellyfin";
        }

        if (_store.GetAverage(taskKey).HasValue)
        {
            return "taskmaster";
        }

        return "unknown";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Jellyfin history reader
    // ─────────────────────────────────────────────────────────────────────

    private TimeSpan? TryReadJellyfinHistory(string taskKey)
    {
        // Jellyfin writes task results as JSON files under:
        //   {DataPath}/ScheduledTasks/{TaskKey}/{timestamp}.json
        // Each file has StartTimeUtc and EndTimeUtc. Some builds have
        // migrated this to the database and no longer write files; in
        // that case the directory is missing and we return null.
        var dir = Path.Combine(_dataPath, "ScheduledTasks", taskKey);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var durations = new List<long>();

        // Newest files first; cap the scan so a task with years of history
        // doesn't cost us a directory walk on every sort comparison.
        var files = Directory
        .EnumerateFiles(dir, "*.json")
        .OrderByDescending(f => f)
        .Take(MaxHistoryFilesToScan);

        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                if (!TryReadDate(root, "StartTimeUtc", out var start) ||
                    !TryReadDate(root, "EndTimeUtc", out var end) ||
                    end <= start)
                {
                    continue;
                }

                durations.Add((end - start).Ticks);
            }
            catch
            {
                // Malformed or partially-written file — skip it.
            }
        }

        if (durations.Count == 0)
        {
            return null;
        }

        return TimeSpan.FromTicks((long)durations.Average());
    }

    private static bool TryReadDate(JsonElement root, string propertyName, out DateTime value)
    {
        value = default;

        if (!root.TryGetProperty(propertyName, out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            return DateTime.TryParse(el.GetString(), out value);
        }

        // Some Jellyfin versions serialize as ISO strings, others as
        // ticks. Fall back to ticks if the string parse above didn't
        // apply.
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ticks))
        {
            try
            {
                value = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
