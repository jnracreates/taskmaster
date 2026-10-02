using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TaskMaster.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TaskMaster.Services;

/// <summary>
/// Snapshots the native triggers of every Jellyfin scheduled task so
/// "Take over" and "Release" can be a safe round-trip.
///
/// Backing store: {DataPath}/taskmaster/backups/backup-yyyyMMdd-HHmmss.json
/// </summary>
public class TaskBackupService
{
    private readonly string _backupDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TaskBackupService(IApplicationPaths paths)
    {
        _backupDir = Path.Combine(paths.DataPath, "taskmaster", "backups");
        Directory.CreateDirectory(_backupDir);
    }

    /// <summary>
    /// Writes a snapshot of every task's triggers to disk.
    /// The orchestrator itself is always excluded.
    /// Returns the full path of the file that was created.
    /// </summary>
    public string CreateBackup(IEnumerable<IScheduledTaskWorker> workers)
    {
        var snapshot = new TaskBackup
        {
            CreatedUtc = DateTime.UtcNow,
            JellyfinVersion = typeof(IScheduledTask).Assembly.GetName().Version?.ToString() ?? "unknown",
            Tasks = workers
            .Where(w => w.ScheduledTask.Key != SequentialTaskOrchestrator.OrchestratorKey)
            .Select(w => new TaskBackupEntry
            {
                Key = w.ScheduledTask.Key,
                Name = w.ScheduledTask.Name,
                Triggers = w.Triggers?.ToList() ?? new List<TaskTriggerInfo>()
            })
            .ToList()
        };

        var file = Path.Combine(
            _backupDir,
            $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOpts));
        return file;
    }

    /// <summary>
    /// Reads the most recent backup file, or null if there aren't any.
    /// </summary>
    public TaskBackup? GetLatest()
    {
        var latest = Directory
        .EnumerateFiles(_backupDir, "backup-*.json")
        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
        .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TaskBackup>(
                File.ReadAllText(latest),
                                                          JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lightweight listing of every backup file on disk, newest first.
    /// </summary>
    public IReadOnlyList<BackupSummary> ListBackups()
    {
        return Directory
        .EnumerateFiles(_backupDir, "backup-*.json")
        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
        .Select(f =>
        {
            var info = new FileInfo(f);
            return new BackupSummary
            {
                FileName = info.Name,
                CreatedUtc = info.CreationTimeUtc,
                SizeBytes = info.Length
            };
        })
        .ToList();
    }

    /// <summary>
    /// Deletes every backup file older than <paramref name="keep"/> count.
    /// Called opportunistically to avoid unbounded growth.
    /// </summary>
    public void PruneOldBackups(int keep = 20)
    {
        try
        {
            var files = Directory
            .EnumerateFiles(_backupDir, "backup-*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────
//  Data transfer objects
// ─────────────────────────────────────────────────────────────────────────

public class TaskBackup
{
    public DateTime CreatedUtc { get; set; }

    public string JellyfinVersion { get; set; } = string.Empty;

    public List<TaskBackupEntry> Tasks { get; set; } = new();
}

public class TaskBackupEntry
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<TaskTriggerInfo> Triggers { get; set; } = new();
}

public class BackupSummary
{
    public string FileName { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public long SizeBytes { get; set; }
}
