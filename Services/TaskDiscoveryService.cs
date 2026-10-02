using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TaskMaster.ScheduledTasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TaskMaster.Services;

/// <summary>
/// Compares the set of tasks Jellyfin knows about against the set of tasks
/// TaskMaster's config knows about, and reports the difference in both
/// directions:
///   • Unmanaged — Jellyfin has the task, TaskMaster doesn't. Usually a new
///     plugin that added a scheduled task after our last config save.
///   • Orphaned  — TaskMaster has the entry, Jellyfin doesn't. Usually the
///     plugin that owned the task was uninstalled.
/// </summary>
public class TaskDiscoveryService
{
    private readonly ITaskManager _taskManager;

    public TaskDiscoveryService(ITaskManager taskManager)
    {
        _taskManager = taskManager;
    }

    /// <summary>
    /// Jellyfin tasks that aren't yet in the TaskMaster config.
    /// Never includes the orchestrator itself.
    /// </summary>
    public IReadOnlyList<DiscoveredTask> GetUnmanaged(PluginConfiguration config)
    {
        var known = new HashSet<string>(
            config.Tasks.Select(t => t.TaskKey),
                                        StringComparer.Ordinal);

        return _taskManager.ScheduledTasks
        .Where(w => w.ScheduledTask.Key != SequentialTaskOrchestrator.OrchestratorKey)
        .Where(w => !known.Contains(w.ScheduledTask.Key))
        .Select(w => new DiscoveredTask
        {
            Key = w.ScheduledTask.Key,
            Name = w.ScheduledTask.Name,
            Category = w.ScheduledTask.Category,
            Description = w.ScheduledTask.Description,
            NativeTriggerCount = w.Triggers?.Count ?? 0,
            LastRunUtc = w.LastExecutionResult != null
                ? w.LastExecutionResult.StartTimeUtc
                : (DateTime?)null,
            LastDurationTicks = (w.LastExecutionResult != null &&
                                 w.LastExecutionResult.EndTimeUtc > w.LastExecutionResult.StartTimeUtc)
                ? (w.LastExecutionResult.EndTimeUtc - w.LastExecutionResult.StartTimeUtc).Ticks
                : 0L,
            LastStatus = w.LastExecutionResult != null
                ? w.LastExecutionResult.Status.ToString()
                : null
        })
        .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    /// <summary>
    /// Config entries whose keys no longer correspond to a real Jellyfin
    /// task. Never includes the orchestrator itself.
    /// </summary>
    public IReadOnlyList<TaskEntry> GetOrphans(PluginConfiguration config)
    {
        var live = new HashSet<string>(
            _taskManager.ScheduledTasks.Select(w => w.ScheduledTask.Key),
                                       StringComparer.Ordinal);

        return config.Tasks
        .Where(t => t.TaskKey != SequentialTaskOrchestrator.OrchestratorKey)
        .Where(t => !live.Contains(t.TaskKey))
        .ToList();
    }

    /// <summary>
    /// Adds the given task keys to the config as enabled entries with no
    /// day restrictions. Keys that don't correspond to a live task, or
    /// that are already in the config, are silently skipped.
    /// Returns the number of newly added entries.
    /// </summary>
    public int Import(IEnumerable<string> keys, PluginConfiguration config)
    {
        var existing = new HashSet<string>(
            config.Tasks.Select(t => t.TaskKey),
                                           StringComparer.Ordinal);

        var added = 0;

        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (key == SequentialTaskOrchestrator.OrchestratorKey)
            {
                continue;
            }

            if (existing.Contains(key))
            {
                continue;
            }

            var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(w => w.ScheduledTask.Key == key);

            if (worker is null)
            {
                continue;
            }

            // Tasks Jellyfin never scheduled (0 native triggers) are usually
            // one-shot migrations or manual-only utilities. Import them
            // disabled so they appear in the list without running nightly.
            var hadNativeSchedule = (worker.Triggers?.Count ?? 0) > 0;

            config.Tasks.Add(new TaskEntry
            {
                TaskKey = key,
                TaskName = worker.ScheduledTask.Name,
                Enabled = hadNativeSchedule,
                Order = 0,
                RunOnDays = new List<string>(),
                WasManualOnly = !hadNativeSchedule
            });

            existing.Add(key);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Removes config entries whose underlying Jellyfin task no longer
    /// exists. Returns the number of entries removed.
    /// </summary>
    public int PruneOrphans(PluginConfiguration config)
    {
        var live = new HashSet<string>(
            _taskManager.ScheduledTasks.Select(w => w.ScheduledTask.Key),
                                       StringComparer.Ordinal);

        return config.Tasks.RemoveAll(t =>
        t.TaskKey != SequentialTaskOrchestrator.OrchestratorKey &&
        !live.Contains(t.TaskKey));
    }

    /// <summary>
    /// Refreshes the cached display name of every config entry from the
    /// live Jellyfin task, in case a plugin renamed a task between runs.
    /// Returns the number of names that actually changed.
    /// </summary>
    public int RefreshNames(PluginConfiguration config)
    {
        var byKey = _taskManager.ScheduledTasks
        .ToDictionary(w => w.ScheduledTask.Key, w => w.ScheduledTask.Name, StringComparer.Ordinal);

        var changed = 0;

        foreach (var entry in config.Tasks)
        {
            if (byKey.TryGetValue(entry.TaskKey, out var name) &&
                !string.Equals(entry.TaskName, name, StringComparison.Ordinal))
            {
                entry.TaskName = name;
                changed++;
            }
        }

        return changed;
    }
}

/// <summary>
/// A Jellyfin task that isn't yet in TaskMaster's config.
/// </summary>
public class DiscoveredTask
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// How many native triggers the task currently has. Useful context —
    /// a task with zero triggers won't run on its own anyway.
    /// </summary>
    public int NativeTriggerCount { get; set; }

    /// <summary>UTC time the task last ran, if any.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Duration of the last run, in ticks. 0 if unknown.</summary>
    public long LastDurationTicks { get; set; }

    /// <summary>Status of the last run (Completed, Failed, Cancelled, etc.).</summary>
    public string? LastStatus { get; set; }

}
