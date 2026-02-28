using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SamplePlugin.Windows;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private const string CommandName = "/instanttp";

    private const string CastTimeSig =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 40 4C 8B 3D ?? ?? ?? ?? 49 8B F1 41 0F B6 D8 8B FA";

    private const string SetOpenTransitionSig =
        "E8 ?? ?? ?? ?? F3 0F 10 0D ?? ?? ?? ?? 45 33 C9 F3 0F 59 0D ?? ?? ?? ??";

    private static readonly HashSet<string> TransitionAddonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_LocationTitle",
        "_AreaTitle",
        "_WideText",
        "FadeMiddle",
        "_FadeMiddle",
        "FadeBack",
        "_FadeBack",
        "FadeFront",
        "_FadeFront",
    };

    private delegate int GetAdjustedCastTimeDelegate(uint actionType, uint actionId, byte applyProcs, IntPtr outOptProc);
    private unsafe delegate void SetOpenTransitionDelegate(AtkUnitBase* addon, float duration, short offsetX, short offsetY, float scale);

    private Hook<GetAdjustedCastTimeDelegate>? getAdjustedCastTimeHook;
    private Hook<SetOpenTransitionDelegate>? setOpenTransitionHook;

    private bool isTeleporting;
    private DateTime teleportStartUtc;
    private uint sourceTerritoryTypeId;
    private const float TeleportTimeoutSeconds = 20f;

    public Configuration Configuration { get; init; }
    public WindowSystem WindowSystem { get; } = new("InstantTeleport");
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Instant Teleport settings and status."
        });

        InitializeHooks();

        Framework.Update += OnFrameworkUpdate;
        AddonLifecycle.RegisterListener(AddonEvent.PostOpen, OnAddonTransitionEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostShow, OnAddonTransitionEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, OnAddonTransitionEvent);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        Log.Information("Instant Teleport loaded. Cast={Cast} Transition={Transition}",
            Configuration.EnableInstantCast, Configuration.EnableTransitionSkip);
    }

    private unsafe void InitializeHooks()
    {
        try
        {
            var castAddress = SigScanner.ScanText(CastTimeSig);
            getAdjustedCastTimeHook = GameInteropProvider.HookFromAddress<GetAdjustedCastTimeDelegate>(
                castAddress,
                GetAdjustedCastTimeDetour);
            getAdjustedCastTimeHook.Enable();
            Log.Information("GetAdjustedCastTime hook enabled at 0x{Address:X}", castAddress);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize GetAdjustedCastTime hook.");
        }

        try
        {
            var transitionAddress = (nint)AtkUnitBase.MemberFunctionPointers.SetOpenTransition;
            if (transitionAddress == 0)
            {
                transitionAddress = SigScanner.ScanText(SetOpenTransitionSig);
            }

            setOpenTransitionHook = GameInteropProvider.HookFromAddress<SetOpenTransitionDelegate>(
                transitionAddress,
                SetOpenTransitionDetour);
            setOpenTransitionHook.Enable();
            Log.Information("SetOpenTransition hook enabled at 0x{Address:X}", transitionAddress);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize SetOpenTransition hook. Delay override/addon suppression will still apply.");
        }
    }

    private int GetAdjustedCastTimeDetour(uint actionType, uint actionId, byte applyProcs, IntPtr outOptProc)
    {
        if (Configuration.EnableInstantCast && actionType == 1 && (actionId == 5 || actionId == 6))
        {
            isTeleporting = true;
            teleportStartUtc = DateTime.UtcNow;

            unsafe
            {
                var gameMain = GameMain.Instance();
                sourceTerritoryTypeId = gameMain != null ? gameMain->CurrentTerritoryTypeId : 0;
            }

            if (Configuration.DebugLogging)
            {
                Log.Debug("Instant cast applied to {Action}.", actionId == 5 ? "Teleport" : "Return");
            }

            return 0;
        }

        return getAdjustedCastTimeHook!.Original(actionType, actionId, applyProcs, outOptProc);
    }

    private unsafe void SetOpenTransitionDetour(AtkUnitBase* addon, float duration, short offsetX, short offsetY, float scale)
    {
        if (addon != null &&
            isTeleporting &&
            Configuration.EnableTransitionSkip &&
            Configuration.EnableFastFade &&
            TransitionAddonNames.Contains(addon->NameString))
        {
            duration = Configuration.FadeDurationOverride;
        }

        setOpenTransitionHook!.Original(addon, duration, offsetX, offsetY, scale);
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (!isTeleporting)
        {
            return;
        }

        if ((DateTime.UtcNow - teleportStartUtc).TotalSeconds >= TeleportTimeoutSeconds)
        {
            isTeleporting = false;
            return;
        }

        var gameMain = GameMain.Instance();
        if (gameMain == null)
        {
            return;
        }

        if (Configuration.EnableTransitionSkip &&
            gameMain->TerritoryTransitionState != 0 &&
            gameMain->TerritoryTransitionDelay > Configuration.FadeDurationOverride)
        {
            gameMain->TerritoryTransitionDelay = Configuration.FadeDurationOverride;
        }

        if (gameMain->ConnectedToZone &&
            gameMain->CurrentTerritoryTypeId != 0 &&
            gameMain->CurrentTerritoryTypeId != sourceTerritoryTypeId &&
            gameMain->TerritoryTransitionState == 0)
        {
            isTeleporting = false;
        }
    }

    private unsafe void OnAddonTransitionEvent(AddonEvent type, AddonArgs args)
    {
        if (!isTeleporting || !Configuration.EnableAddonSuppression || !TransitionAddonNames.Contains(args.AddonName))
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null)
        {
            return;
        }

        addon->IsVisible = false;
        addon->Alpha = 0;
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            ToggleConfigUi();
            return;
        }

        switch (parts[0])
        {
            case "status":
                Log.Information("Cast={Cast} Transition={Transition} FastFade={FastFade} AddonSuppression={Addon}",
                    Configuration.EnableInstantCast,
                    Configuration.EnableTransitionSkip,
                    Configuration.EnableFastFade,
                    Configuration.EnableAddonSuppression);
                break;
            case "debug":
                Configuration.DebugLogging = !Configuration.DebugLogging;
                Configuration.Save();
                Log.Information("Debug logging: {Enabled}", Configuration.DebugLogging);
                break;
            default:
                ToggleConfigUi();
                break;
        }
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        Framework.Update -= OnFrameworkUpdate;
        AddonLifecycle.UnregisterListener(AddonEvent.PostOpen, OnAddonTransitionEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostShow, OnAddonTransitionEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, OnAddonTransitionEvent);

        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        getAdjustedCastTimeHook?.Disable();
        getAdjustedCastTimeHook?.Dispose();

        setOpenTransitionHook?.Disable();
        setOpenTransitionHook?.Dispose();
    }
}
