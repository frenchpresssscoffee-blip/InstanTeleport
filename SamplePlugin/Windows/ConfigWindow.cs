using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SamplePlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Instant Teleport")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;

        Size = new Vector2(460, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.Text("FFXIV Instant Teleport");
        ImGui.Separator();

        var instantCast = configuration.EnableInstantCast;
        if (ImGui.Checkbox("Instant Cast (Teleport/Return)", ref instantCast))
        {
            configuration.EnableInstantCast = instantCast;
            configuration.Save();
        }

        var transitionSkip = configuration.EnableTransitionSkip;
        if (ImGui.Checkbox("Skip Transition Screen", ref transitionSkip))
        {
            configuration.EnableTransitionSkip = transitionSkip;
            configuration.Save();
        }

        var fastFade = configuration.EnableFastFade;
        if (ImGui.Checkbox("Force Fast Fade", ref fastFade))
        {
            configuration.EnableFastFade = fastFade;
            configuration.Save();
        }

        var suppress = configuration.EnableAddonSuppression;
        if (ImGui.Checkbox("Suppress Transition Addons", ref suppress))
        {
            configuration.EnableAddonSuppression = suppress;
            configuration.Save();
        }

        var fadeOverride = configuration.FadeDurationOverride;
        if (ImGui.SliderFloat("Fade Override (seconds)", ref fadeOverride, 0.00f, 0.25f, "%.3f"))
        {
            configuration.FadeDurationOverride = fadeOverride;
            configuration.Save();
        }

        var debug = configuration.DebugLogging;
        if (ImGui.Checkbox("Debug Logging", ref debug))
        {
            configuration.DebugLogging = debug;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Command: /instanttp");
        if (ImGui.Button("Close"))
        {
            plugin.ToggleConfigUi();
        }
    }
}
