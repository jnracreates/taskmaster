using System.IO;
using Jellyfin.Plugin.TaskMaster.ScheduledTasks;
using Jellyfin.Plugin.TaskMaster.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TaskMaster;

/// <summary>
/// Registers TaskMaster's services with Jellyfin's DI container.
/// Jellyfin discovers this class by name during plugin load.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Timing store: records actual runtimes of tasks TaskMaster executes.
        services.AddSingleton<TaskTimingStore>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new TaskTimingStore(Path.Combine(paths.DataPath, "taskmaster"));
        });

        // Reads Jellyfin's built-in task history + our timing store.
        services.AddSingleton<TaskHistoryService>();

        // Backup/restore of native task triggers.
        services.AddSingleton<TaskBackupService>();

        // Detects new and orphaned tasks relative to the config.
        services.AddSingleton<TaskDiscoveryService>();

        // The orchestrator itself, registered as a scheduled task.
        services.AddSingleton<IScheduledTask, SequentialTaskOrchestrator>();
    }
}
