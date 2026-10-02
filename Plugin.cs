using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TaskMaster;

/// <summary>
/// TaskMaster plugin entry point.
/// Loads the persisted configuration and exposes the dashboard config page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin GUID. Must match the <c>guid</c> field in build.yaml.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("3388f1aa-2b91-4166-9d3b-727ad0755057");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
    : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Singleton reference, set once during construction.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "TaskMaster";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
    "Runs Jellyfin scheduled tasks sequentially with ordering by average " +
    "completion time, a configurable time window, per-day exclusions, " +
    "backup/restore of native triggers, and detection of newly added tasks.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }

}
