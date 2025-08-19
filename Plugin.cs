using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AudioPruner;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths appPaths) : base(appPaths) { }

    public override string Name => "AudioPruner";
    public override Guid Id => new("c7f2b3f7-8f8a-47a0-9c6d-1a1111111111");
    public override string Description => "Safer AudioPruner: creates non-destructive remuxes and offers restore.";
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "audiopruner",
            EmbeddedResourcePath = $"{GetType().Namespace}.web.audiopruner.html"
        }
    };
}
