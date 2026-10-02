using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TaskMaster;

/// <summary>
/// Persisted settings for the TaskMaster plugin.
/// Serialized to XML by Jellyfin as {DataPath}/config/TaskMaster.xml.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// How the orchestrator orders the sequence.
    /// Valid values: "FastestFirst", "SlowestFirst", "Manual".
    /// </summary>
    public string OrderingMode { get; set; } = "FastestFirst";

    /// <summary>
    /// Earliest time of day the orchestrator may start (HH:mm).
    /// Null or empty means no lower bound.
    /// </summary>
    public string? StartTime { get; set; } = "02:00";

    /// <summary>
    /// Latest time of day the orchestrator may still be running (HH:mm).
    /// Null or empty means no upper bound. If EndTime is earlier than
    /// StartTime the window is treated as overnight.
    /// </summary>
    public string? EndTime { get; set; } = "06:00";

    /// <summary>
    /// When true, any Jellyfin task not already in <see cref="Tasks"/> is
    /// automatically added (enabled, no day restrictions) at the start of
    /// every orchestrator run. When false, a warning is logged instead.
    /// </summary>
    public bool AutoImportNewTasks { get; set; } = false;

    /// <summary>
    /// The ordered list of tasks TaskMaster knows about.
    /// </summary>
    public List<TaskEntry> Tasks { get; set; } = new();
}

/// <summary>
/// A single task managed by TaskMaster.
/// </summary>
public class TaskEntry
{
    /// <summary>
    /// Jellyfin's internal task key (e.g. "RefreshLibrary").
    /// Must match a real scheduled task at run time or the entry is skipped.
    /// </summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable task name, cached for display purposes only.
    /// Refreshed whenever the config page loads.
    /// </summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the orchestrator should run this task at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Used only when OrderingMode == "Manual". Lower numbers run first.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Days of the week this task should run.
    /// Empty list means every day.
    /// Values are English day names: "Monday", "Tuesday", etc.
    /// </summary>
    public List<string> RunOnDays { get; set; } = new();

    /// <summary>
    /// True when this task had zero native Jellyfin triggers at the time it
    /// was imported. Used only as a UI hint to flag manual-only tasks.
    /// </summary>
    public bool WasManualOnly { get; set; }
}
