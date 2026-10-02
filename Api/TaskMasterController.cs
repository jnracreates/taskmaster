using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TaskMaster.ScheduledTasks;
using Jellyfin.Plugin.TaskMaster.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TaskMaster.Api;

/// <summary>
/// REST endpoints backing the TaskMaster config page.
/// All routes require an admin (RequiresElevation policy).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TaskMaster")]
public class TaskMasterController : ControllerBase
{
    private readonly ITaskManager _taskManager;
    private readonly TaskBackupService _backups;
    private readonly TaskDiscoveryService _discovery;
    private readonly TaskHistoryService _history;
    private readonly string _backupDir;
    private readonly ILogger<TaskMasterController> _logger;

    public TaskMasterController(
        ITaskManager taskManager,
        TaskBackupService backups,
        TaskDiscoveryService discovery,
        TaskHistoryService history,
        IApplicationPaths paths,
        ILogger<TaskMasterController> logger)
    {
        _taskManager = taskManager;
        _backups = backups;
        _discovery = discovery;
        _history = history;
        _backupDir = Path.Combine(paths.DataPath, "taskmaster", "backups");
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Status
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("Status")]
    public ActionResult<StatusDto> GetStatus()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        var allTasks = _taskManager.ScheduledTasks
        .Where(w => w.ScheduledTask.Key != SequentialTaskOrchestrator.OrchestratorKey)
        .ToList();

        var takenOver = allTasks.Count(w => (w.Triggers?.Count ?? 0) == 0);
        var backups = _backups.ListBackups();
        var latestBackup = backups.FirstOrDefault();

        return new StatusDto
        {
            TotalTasks = allTasks.Count,
            TasksWithNoTriggers = takenOver,
            TakenOver = takenOver > 0,
            LatestBackupUtc = latestBackup?.CreatedUtc,
            LatestBackupFile = latestBackup?.FileName,
            BackupCount = backups.Count,
            UnmanagedTaskCount = _discovery.GetUnmanaged(config).Count,
            OrphanedTaskCount = _discovery.GetOrphans(config).Count,
            AutoImportNewTasks = config.AutoImportNewTasks
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Backup / Restore / Download
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost("Backup/Create")]
    public ActionResult<object> CreateBackup()
    {
        var path = _backups.CreateBackup(_taskManager.ScheduledTasks);
        _backups.PruneOldBackups(20);
        return Ok(new { file = Path.GetFileName(path) });
    }

    [HttpGet("Backup/List")]
    public ActionResult<IReadOnlyList<BackupSummary>> ListBackups()
    => Ok(_backups.ListBackups());

    [HttpGet("Backup/Download")]
    public ActionResult DownloadBackup([FromQuery] string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return BadRequest(new { error = "Missing file parameter." });
        }

        // Whitelist against the known backup list so we never build a path
        // from user input. This also rejects path-traversal attempts.
        var summary = _backups.ListBackups()
        .FirstOrDefault(b => string.Equals(b.FileName, file, StringComparison.Ordinal));

        if (summary is null)
        {
            return NotFound(new { error = "Backup not found." });
        }

        var fullPath = Path.Combine(_backupDir, summary.FileName);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { error = "Backup file missing on disk." });
        }

        return PhysicalFile(fullPath, "application/json", summary.FileName);
    }

    [HttpPost("Backup/Restore")]
    public ActionResult Restore([FromQuery] string? file = null)
    {
        TaskBackup? backup;

        if (string.IsNullOrWhiteSpace(file))
        {
            backup = _backups.GetLatest();
        }
        else
        {
            // Same whitelist idea as Download.
            var summary = _backups.ListBackups()
            .FirstOrDefault(b => string.Equals(b.FileName, file, StringComparison.Ordinal));

            if (summary is null)
            {
                return NotFound(new { error = "Backup not found." });
            }

            try
            {
                var json = System.IO.File.ReadAllText(Path.Combine(_backupDir, summary.FileName));
                backup = System.Text.Json.JsonSerializer.Deserialize<TaskBackup>(json);
            }
            catch
            {
                backup = null;
            }
        }

        if (backup is null)
        {
            return NotFound(new { error = "No backup found." });
        }

        ApplyBackup(backup);
        return Ok(new { restoredFrom = backup.CreatedUtc, taskCount = backup.Tasks.Count });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Take over / Release
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost("TakeOver")]
    public ActionResult TakeOver()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin not initialised." });
        }

        // Safety: always ensure at least one backup exists before we touch
        // anyone's triggers.
        if (_backups.ListBackups().Count == 0)
        {
            _backups.CreateBackup(_taskManager.ScheduledTasks);
        }

        // Import every task that currently has a real (non-startup) trigger
        // so TaskMaster actually owns them after we blank their schedules.
        // Tasks whose only trigger is Startup are left alone (they're
        // initialization hooks, not scheduled work).
        var toImport = _taskManager.ScheduledTasks
            .Where(w => w.ScheduledTask.Key != SequentialTaskOrchestrator.OrchestratorKey)
            .Where(w => (w.Triggers ?? new List<TaskTriggerInfo>())
                .Any(t => t.Type != TaskTriggerInfoType.StartupTrigger))
            .Select(w => w.ScheduledTask.Key)
            .ToList();

        var imported = _discovery.Import(toImport, plugin.Configuration);
        if (imported > 0)
        {
            plugin.SaveConfiguration();
        }

        var disabled = 0;

        foreach (var worker in _taskManager.ScheduledTasks)
        {
            if (worker.ScheduledTask.Key == SequentialTaskOrchestrator.OrchestratorKey)
            {
                continue;
            }

            if ((worker.Triggers?.Count ?? 0) == 0)
            {
                continue; // Already taken over; leave it alone.
            }

            try
            {
                // Preserve StartupTrigger and ImmediateTrigger - those are
                // initialization hooks, not scheduling. Only strip the
                // timer-based triggers that TaskMaster is taking over.
                var kept = (worker.Triggers ?? new List<TaskTriggerInfo>())
                    .Where(t => t.Type == TaskTriggerInfoType.StartupTrigger)
                    .ToList();

                worker.Triggers = kept;
                disabled++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                                 "TaskMaster: failed to clear triggers for '{Key}'.",
                                 worker.ScheduledTask.Key);
            }
        }

        return Ok(new { disabled, imported });
    }

    [HttpPost("Release")]
    public ActionResult Release()
    {
        var backup = _backups.GetLatest();
        if (backup is null)
        {
            return NotFound(new { error = "No backup to release from." });
        }

        ApplyBackup(backup);
        return Ok(new { restoredFrom = backup.CreatedUtc, taskCount = backup.Tasks.Count });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Discovery / orphans / import
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("Discovered")]
    public ActionResult<IReadOnlyList<DiscoveredTask>> GetDiscovered()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(Array.Empty<DiscoveredTask>());
        }

        return Ok(_discovery.GetUnmanaged(config));
    }

    [HttpGet("Orphans")]
    public ActionResult<IReadOnlyList<TaskEntry>> GetOrphans()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(Array.Empty<TaskEntry>());
        }

        return Ok(_discovery.GetOrphans(config));
    }

    [HttpPost("Import")]
    public ActionResult<object> Import([FromBody] ImportRequest req)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin not initialised." });
        }

        var added = _discovery.Import(
            req?.Keys ?? Array.Empty<string>(),
                                      plugin.Configuration);

        if (added > 0)
        {
            plugin.SaveConfiguration();
        }

        return Ok(new { added });
    }

    [HttpPost("PruneOrphans")]
    public ActionResult<object> PruneOrphans()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin not initialised." });
        }

        var removed = _discovery.PruneOrphans(plugin.Configuration);

        if (removed > 0)
        {
            plugin.SaveConfiguration();
        }

        return Ok(new { removed });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Preview
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("Preview")]
    public ActionResult<IEnumerable<PreviewItem>> Preview()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(Array.Empty<PreviewItem>());
        }

        var today = DateTime.Now.DayOfWeek.ToString();

        var candidates = config.Tasks
        .Where(t => t.Enabled)
        .Where(t => t.TaskKey != SequentialTaskOrchestrator.OrchestratorKey)
        .Where(t => t.RunOnDays.Count == 0 ||
        t.RunOnDays.Any(d => string.Equals(
            d, today, StringComparison.OrdinalIgnoreCase)))
        .ToList();

        var ordered = config.OrderingMode switch
        {
            "SlowestFirst" => candidates
            .OrderByDescending(t => _history.GetSortKey(t.TaskKey))
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase),

            "Manual" => candidates
            .OrderBy(t => t.Order)
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase),

            _ => candidates
            .OrderBy(t => _history.GetSortKey(t.TaskKey))
            .ThenBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)
        };

        var list = ordered
        .Select((t, i) => new PreviewItem
        {
            Index = i + 1,
            TaskKey = t.TaskKey,
            TaskName = t.TaskName,
            AverageTicks = _history.GetAverage(t.TaskKey).Ticks,
                AverageSource = _history.GetAverageSource(t.TaskKey)
        })
        .ToList();

        return Ok(list);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Run now (with time-window override)
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost("RunNow")]
    public ActionResult RunNow()
    {
        var worker = _taskManager.ScheduledTasks
        .FirstOrDefault(w => w.ScheduledTask.Key == SequentialTaskOrchestrator.OrchestratorKey);

        if (worker is null)
        {
            return NotFound(new { error = "Orchestrator task not registered." });
        }

        if (worker.ScheduledTask is not SequentialTaskOrchestrator orchestrator)
        {
            return StatusCode(500, new { error = "Orchestrator task has unexpected type." });
        }

        // Arm the one-shot override, then kick off the run in the
        // background. The HTTP request returns immediately; the task
        // manager tracks the run and the config page can poll /Status
        // to see progress if needed.
        orchestrator.RequestWindowOverride();

        _ = Task.Run(async () =>
        {
            try
            {
                await _taskManager
                .Execute(worker, new TaskOptions())
                .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TaskMaster: RunNow execution failed.");
            }
        });

        return Ok(new { started = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Sync trigger
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the orchestrator's daily trigger to fire at the configured
    /// StartTime. Called from the config page after saving so the Scheduled
    /// Tasks page and the TaskMaster window agree on when the sequence starts.
    /// </summary>
    [HttpPost("SyncTrigger")]
    public ActionResult SyncTrigger()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin not initialised." });
        }

        // Read StartTime from the in-memory config, but fall back to reading
        // the XML file directly if the in-memory value is empty. Some Jellyfin
        // 12.x builds don't deserialize plugin configs properly when the
        // plugin is installed at runtime.
        var startStr = plugin.Configuration?.StartTime;

        if (string.IsNullOrWhiteSpace(startStr))
        {
            startStr = ReadStartTimeFromXml(plugin);
            _logger.LogInformation(
                "TaskMaster: in-memory StartTime was empty; fell back to XML value '{Value}'.",
                startStr ?? "(null)");
        }

        var start = ParseTimeString(startStr);
        if (start is null)
        {
            return Ok(new { synced = false, reason = "no start time configured" });
        }

        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(w => w.ScheduledTask.Key == SequentialTaskOrchestrator.OrchestratorKey);

        if (worker is null)
        {
            return NotFound(new { error = "Orchestrator task not registered." });
        }

        var triggers = worker.Triggers?.ToList() ?? new List<TaskTriggerInfo>();
        var daily = triggers.FirstOrDefault(t => t.Type == TaskTriggerInfoType.DailyTrigger);

        if (daily is null)
        {
            daily = new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = start.Value.Ticks
            };
            triggers.Add(daily);
        }
        else
        {
            daily.TimeOfDayTicks = start.Value.Ticks;
        }

        worker.Triggers = triggers;

        var formatted = $"{start.Value.Hours:D2}:{start.Value.Minutes:D2}";

        _logger.LogInformation(
            "TaskMaster: orchestrator trigger synced to {Time}.",
            formatted);

        return Ok(new { synced = true, time = formatted });
    }

    /// <summary>
    /// Reads StartTime directly from the plugin's config XML on disk.
    /// Used as a fallback when the in-memory configuration is empty
    /// (a Jellyfin 12.x issue with runtime-installed plugins).
    /// </summary>
    private static string? ReadStartTimeFromXml(Plugin plugin)
    {
        try
        {
            var path = plugin.ConfigurationFilePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return null;
            }

            var doc = System.Xml.Linq.XDocument.Load(path);
            var el = doc.Root?.Element("StartTime");
            var value = el?.Value?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? ParseTimeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Parse "HH:mm" manually. TimeSpan format strings require escaped
        // colons (@"hh:mm"), which has been a recurring copy/paste hazard.
        // Simple split is bulletproof.
        var parts = value.Trim().Split(':');
        if (parts.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes))
        {
            return null;
        }

        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
        {
            return null;
        }

        return new TimeSpan(hours, minutes, 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private void ApplyBackup(TaskBackup backup)
    {
        foreach (var entry in backup.Tasks)
        {
            var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(w => string.Equals(
                w.ScheduledTask.Key, entry.Key, StringComparison.Ordinal));

            if (worker is null)
            {
                continue;
            }

            try
            {
                worker.Triggers = entry.Triggers.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                                 "TaskMaster: failed to restore triggers for '{Key}'.",
                                 entry.Key);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
//  Request / response DTOs
// ─────────────────────────────────────────────────────────────────────────

public class StatusDto
{
    public int TotalTasks { get; set; }

    public int TasksWithNoTriggers { get; set; }

    public bool TakenOver { get; set; }

    public DateTime? LatestBackupUtc { get; set; }

    public string? LatestBackupFile { get; set; }

    public int BackupCount { get; set; }

    public int UnmanagedTaskCount { get; set; }

    public int OrphanedTaskCount { get; set; }

    public bool AutoImportNewTasks { get; set; }
}

public class PreviewItem
{
    public int Index { get; set; }

    public string TaskKey { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public long AverageTicks { get; set; }

    public string AverageSource { get; set; } = "unknown";
}

public class ImportRequest
{
    public string[]? Keys { get; set; }
}
