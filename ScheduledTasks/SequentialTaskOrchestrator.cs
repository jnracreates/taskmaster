using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TaskMaster.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TaskMaster.ScheduledTasks;

/// <summary>
/// The TaskMaster runner. Registered with Jellyfin as a normal scheduled
/// task; when its own trigger fires, it walks the configured sequence and
/// executes each child task to completion before starting the next.
/// </summary>
public class SequentialTaskOrchestrator : IScheduledTask
{
    /// <summary>
    /// Stable key that identifies the orchestrator to Jellyfin and
    /// everywhere else in this plugin. Used to keep ourselves out of
    /// backups, config tables, and the sequence itself.
    /// </summary>
    public const string OrchestratorKey = "TaskMasterOrchestrator";

    private readonly ITaskManager _taskManager;
    private readonly TaskHistoryService _history;
    private readonly TaskTimingStore _timings;
    private readonly TaskDiscoveryService _discovery;
    private readonly ILogger<SequentialTaskOrchestrator> _logger;

    private readonly object _overrideLock = new();
    private bool _ignoreWindowOnce;

    public SequentialTaskOrchestrator(
        ITaskManager taskManager,
        TaskHistoryService history,
        TaskTimingStore timings,
        TaskDiscoveryService discovery,
        ILogger<SequentialTaskOrchestrator> logger)
    {
        _taskManager = taskManager;
        _history = history;
        _timings = timings;
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>
    /// Requests a one-shot override of the time window for the next execution.
    /// Called by the RunNow endpoint after the user confirms a manual run.
    /// The next call to <see cref="ExecuteAsync"/> consumes the flag and skips
    /// both the opening and the between-task window checks.
    /// </summary>
    public void RequestWindowOverride()
    {
        lock (_overrideLock)
        {
            _ignoreWindowOnce = true;
        }
    }

    private bool ConsumeWindowOverride()
    {
        lock (_overrideLock)
        {
            var value = _ignoreWindowOnce;
            _ignoreWindowOnce = false;
            return value;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  IScheduledTask metadata
    // ─────────────────────────────────────────────────────────────────────

    public string Name => "TaskMaster: Run ordered task sequence";

    public string Key => OrchestratorKey;

    public string Description =>
    "Runs the configured scheduled tasks sequentially inside the configured " +
    "time window, ordered by average completion time or manual order.";

    public string Category => "TaskMaster";

    /// <summary>
    /// Default trigger: daily at 02:00. The user can change this from
    /// Dashboard → Scheduled Tasks just like any other task.
    /// </summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Execution
    // ─────────────────────────────────────────────────────────────────────

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;

        if (plugin is null || config is null)
        {
            _logger.LogWarning("TaskMaster: plugin configuration unavailable, aborting run.");
            return;
        }

        // ── Discovery step ──────────────────────────────────────────────
        HandleDiscovery(plugin, config);

        // ── Time window check ───────────────────────────────────────────
        var ignoreWindow = ConsumeWindowOverride();

        if (ignoreWindow)
        {
            _logger.LogInformation(
                "TaskMaster: manual run requested - time window override active ({Start} to {End}).",
                config.StartTime, config.EndTime);
        }

        if (!ignoreWindow && !IsWithinTimeWindow(config, DateTime.Now))
        {
            _logger.LogInformation(
                "TaskMaster: outside configured time window ({Start}–{End}), skipping run.",
                config.StartTime, config.EndTime);
            return;
        }

        // ── Build the ordered sequence ──────────────────────────────────
        var sequence = BuildSequence(config);

        if (sequence.Count == 0)
        {
            _logger.LogInformation("TaskMaster: no tasks selected for today.");
            progress.Report(100);
            return;
        }

        _logger.LogInformation(
            "TaskMaster: starting sequence of {Count} task(s) (mode: {Mode}).",
                               sequence.Count, config.OrderingMode);

        var total = sequence.Count;
        var index = 0;

        foreach (var entry in sequence)
        {
            // Deliberately NOT checking cancellationToken here: Jellyfin links
            // the orchestrator's token to the child's, so stopping a single
            // child would falsely halt the whole sequence. Halt signals are
            // IsStopRequested() (our Stop button), the time window, and the
            // server shutting down (process exit).

            if (!ignoreWindow && !IsWithinTimeWindow(config, DateTime.Now))
            {
                _logger.LogInformation(
                    "TaskMaster: past end of time window at {Index}/{Total}, stopping early.",
                    index, total);
                break;
            }

            var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(w => string.Equals(
                w.ScheduledTask.Key, entry.TaskKey, StringComparison.Ordinal));

            if (worker is null)
            {
                _logger.LogWarning(
                    "TaskMaster: task '{Key}' no longer exists, skipping.",
                    entry.TaskKey);
                index++;
                continue;
            }

            var taskLabel = string.IsNullOrEmpty(entry.TaskName)
            ? entry.TaskKey
            : entry.TaskName;

            _logger.LogInformation(
                "TaskMaster: [{Index}/{Total}] starting '{Name}' ({Key}).",
                                   index + 1, total, taskLabel, entry.TaskKey);

            var sw = Stopwatch.StartNew();

            try
            {
                await _taskManager
                .Execute(worker, new TaskOptions())
                .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "TaskMaster: task '{Key}' was cancelled; skipping to next.",
                    entry.TaskKey);
            }
            catch (Exception ex)
            {
                // One bad task shouldn't kill the rest of the sequence.
                _logger.LogError(
                    ex,
                    "TaskMaster: task '{Key}' threw an exception.",
                    entry.TaskKey);
            }
            finally
            {
                sw.Stop();
                _timings.Record(entry.TaskKey, sw.Elapsed);

                _logger.LogInformation(
                    "TaskMaster: [{Index}/{Total}] finished '{Key}' in {Elapsed}.",
                    index + 1, total, entry.TaskKey, sw.Elapsed);
            }

            index++;
            try
            {
                progress.Report((double)index / total * 100.0);
            }
            catch
            {
                // Progress reporting may fail if the orchestrator's token was
                // cancelled by a child stop - ignore and keep going.
            }
        }

        _logger.LogInformation("TaskMaster: sequence complete ({Ran}/{Total}).",
                               index, total);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Discovery
    // ─────────────────────────────────────────────────────────────────────

    private void HandleDiscovery(Plugin plugin, PluginConfiguration config)
    {
        var unmanaged = _discovery.GetUnmanaged(config);

        if (unmanaged.Count > 0)
        {
            if (config.AutoImportNewTasks)
            {
                var added = _discovery.Import(
                    unmanaged.Select(t => t.Key), config);

                if (added > 0)
                {
                    plugin.SaveConfiguration();
                    _logger.LogInformation(
                        "TaskMaster: auto-imported {Count} new task(s): {Keys}",
                                           added,
                                           string.Join(", ", unmanaged.Select(t => t.Key)));
                }
            }
            else
            {
                _logger.LogWarning(
                    "TaskMaster: {Count} task(s) exist in Jellyfin but are not " +
                    "in TaskMaster's list: {Keys}. Open the TaskMaster config " +
                    "page to import them, or enable AutoImportNewTasks.",
                    unmanaged.Count,
                    string.Join(", ", unmanaged.Select(t => t.Key)));
            }
        }

        var orphans = _discovery.GetOrphans(config);
        if (orphans.Count > 0)
        {
            _logger.LogInformation(
                "TaskMaster: {Count} config entry(ies) point at tasks that no " +
                "longer exist: {Keys}. These will be skipped and can be pruned " +
                "from the config page.",
                orphans.Count,
                string.Join(", ", orphans.Select(t => t.TaskKey)));
        }

        // Opportunistically refresh cached display names.
        _discovery.RefreshNames(config);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Sequence building
    // ─────────────────────────────────────────────────────────────────────

    private List<TaskEntry> BuildSequence(PluginConfiguration config)
    {
        var today = DateTime.Now.DayOfWeek.ToString();

        var candidates = config.Tasks
        .Where(t => t.Enabled)
        .Where(t => !string.Equals(
            t.TaskKey, OrchestratorKey, StringComparison.Ordinal))
        .Where(t => t.RunOnDays.Count == 0 ||
        t.RunOnDays.Any(d => string.Equals(
            d, today, StringComparison.OrdinalIgnoreCase)))
        .ToList();

        return config.OrderingMode switch
        {
            "SlowestFirst" => candidates
            .OrderByDescending(t => _history.GetSortKey(t.TaskKey))
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList(),

            "Manual" => candidates
            .OrderBy(t => t.Order)
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList(),

            // "FastestFirst" and anything unrecognised
            _ => candidates
            .OrderBy(t => _history.GetSortKey(t.TaskKey))
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList()
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Time window
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True if the current time is inside the configured window.
    /// An unset or unparsable boundary means "no limit" on that side.
    /// A window where EndTime &lt; StartTime is treated as overnight.
    /// </summary>
    private static bool IsWithinTimeWindow(PluginConfiguration config, DateTime now)
    {
        var start = ParseTime(config.StartTime);
        var end = ParseTime(config.EndTime);

        if (start is null || end is null)
        {
            return true;
        }

        var t = now.TimeOfDay;

        return start <= end
        ? t >= start && t <= end      // Same-day window, e.g. 02:00–06:00
        : t >= start || t <= end;     // Overnight window, e.g. 22:00–06:00
    }

    private static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var formats = new[] { @"hh\:mm", @"h\:mm" };

        return TimeSpan.TryParseExact(
            value.Trim(),
                                      formats,
                                          CultureInfo.InvariantCulture,
                                      out var ts)
        ? ts
        : null;
    }
}
