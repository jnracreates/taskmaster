using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.TaskMaster.Services;

/// <summary>
/// Persists the last N measured runtimes of each task TaskMaster executes.
/// Feeds the "fastest first" / "slowest first" ordering when Jellyfin's own
/// task history isn't available (or hasn't accumulated enough samples yet).
///
/// Backing store: {DataPath}/taskmaster/timings.json
/// Format: { "TaskKey": [ ticks, ticks, ticks, ... ], ... }
/// </summary>
public class TaskTimingStore
{
    private const int MaxSamplesPerTask = 20;

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, List<long>> _samples = new();

    public TaskTimingStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "timings.json");
        Load();
    }

    /// <summary>
    /// Records one measured runtime for the given task.
    /// Keeps only the most recent <see cref="MaxSamplesPerTask"/> samples.
    /// </summary>
    public void Record(string taskKey, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(taskKey) || duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            if (!_samples.TryGetValue(taskKey, out var list))
            {
                list = new List<long>();
                _samples[taskKey] = list;
            }

            list.Add(duration.Ticks);

            while (list.Count > MaxSamplesPerTask)
            {
                list.RemoveAt(0);
            }

            Save();
        }
    }

    /// <summary>
    /// Returns the average of the recorded runtimes for a task, or null
    /// if no samples exist yet.
    /// </summary>
    public TimeSpan? GetAverage(string taskKey)
    {
        if (string.IsNullOrEmpty(taskKey))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_samples.TryGetValue(taskKey, out var list) || list.Count == 0)
            {
                return null;
            }

            return TimeSpan.FromTicks((long)list.Average());
        }
    }

    /// <summary>
    /// Number of recorded samples for a task (0 if none).
    /// </summary>
    public int GetSampleCount(string taskKey)
    {
        if (string.IsNullOrEmpty(taskKey))
        {
            return 0;
        }

        lock (_lock)
        {
            return _samples.TryGetValue(taskKey, out var list) ? list.Count : 0;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            _samples = JsonSerializer.Deserialize<Dictionary<string, List<long>>>(json)
            ?? new Dictionary<string, List<long>>();
        }
        catch
        {
            // Corrupt or partially-written file — start fresh, but don't
            // overwrite the bad file until the next Record() call.
            _samples = new Dictionary<string, List<long>>();
        }
    }

    private void Save()
    {
        try
        {
            // Write-then-rename so a crash mid-write can't corrupt the file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_samples));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Timings are best-effort — never let a disk hiccup crash a run.
        }
    }
}
