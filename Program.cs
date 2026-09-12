// ============================================================
// HUD Extras: Fuel Burn Endurance Timer
// Made by Hellcat92
// Version: 3.1.2
// Date: 06 August 2026
// ============================================================

using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace FuelBurnHUD;

public enum RangeUnit
{
    Km,
    Nm,
    Mile
}

[BepInPlugin("com.hellcat92.fuelburnhud", "Fuel Burn Endurance Timer", "3.1.2")]
public class Plugin : BaseUnityPlugin
{
    public const int LockedHorizontalOffset = -310;
    public const int LockedVerticalOffset = -120;
    public static ConfigEntry<bool> ModEnabled;

    public static ConfigEntry<bool> ShowFUEL;
    public static ConfigEntry<bool> ShowTIMEREM;
    public static ConfigEntry<bool> ShowFLOW;
    public static ConfigEntry<bool> ShowRNG;

    public static ConfigEntry<int> FontSize;

    internal static ManualLogSource Log { get; private set; }

    // Recolour slider — default HUD green (#00FF00)
    public static ConfigEntry<Color> HudColor;

    public static ConfigEntry<bool> UseMetricUnits;

    public static ConfigEntry<RangeUnit> RangeUnits;

    private void Awake()
    {
        Log = Logger;

        Log.LogInfo("Fuel Burn Endurance Timer 3.1.2 Loaded");

        ModEnabled = Config.Bind("General", "Enable Mod", true);

        ShowFUEL = Config.Bind("HUD", "Show FUEL", true);
        ShowTIMEREM = Config.Bind("HUD", "Show TIME REM", true);
        ShowFLOW = Config.Bind("HUD", "Show FLOW", true);
        ShowRNG = Config.Bind("HUD", "Show RNG", true);

        FontSize = Config.Bind("HUD", "Font Size", 16);

        // Default HUD green (#00FF00)
        HudColor = Config.Bind("HUD", "HUD Text Color", new Color(0f, 1f, 0f, 1f));

        UseMetricUnits = Config.Bind("HUD", "Use Metric Units", false);

        RangeUnits = Config.Bind("HUD", "Range Units", RangeUnit.Km);

        gameObject.AddComponent<FuelBurnWatcher>();
    }
}

public class FuelBurnWatcher : MonoBehaviour
{
    private Aircraft aircraft;

    private FuelBurnController controller;
    private CombatHUD hud;
    private Transform hudCenter;

    private float nextUpdateTime;

    private void Update()
    {
        float now = Time.timeSinceLevelLoad;
        if (now < nextUpdateTime)
        {
            return;
        }

        Plugin.Log.LogDebug("FuelBurnWatcher Update()");
        nextUpdateTime = now + 1.0f;

        if (!hud)
        {
            Plugin.Log.LogDebug("FuelBurnWatcher Called FindObjectOfType<CombatHUD>");
            CombatHUD newHud = FindObjectOfType<CombatHUD>();
            if (newHud != hud)
            {
                hud = newHud;
                ResetInjection();
            }
        }

        if (!hud)
            return;

        if (aircraft != hud.aircraft)
        {
            aircraft = hud.aircraft;
            ResetInjection();
        }

        if (!aircraft)
            return;

        FlightHud fh = SceneSingleton<FlightHud>.i;
        if (!fh)
            return;

        Transform newCenter = fh.GetHUDCenter();
        if (newCenter != hudCenter)
        {
            hudCenter = newCenter;
            ResetInjection();
        }

        if (!hudCenter)
            return;

        if (!controller)
        {
            Plugin.Log.LogDebug("FuelBurnWatcher Calling InjectHUD()");
            InjectHUD();
        }
    }

    private void ResetInjection()
    {
        if (controller)
        {
            Destroy(controller.gameObject);
            controller = null;
        }
    }

    private void InjectHUD()
    {
        GameObject go = new("FuelBurnHUD");
        go.transform.SetParent(hudCenter, false);

        Plugin.Log.LogDebug("FuelBurnWatcher Called AddComponent<FuelBurnController>");
        controller = go.AddComponent<FuelBurnController>();
        controller.aircraft = aircraft;
        controller.hud = hud;
    }
}

public class FuelBurnController : MonoBehaviour
{
    private const float KgToLb = 2.20462262f;
    public Aircraft aircraft;

    private float flashTimer;
    private float flowKgPerSec;
    private bool hasSample;

    private float lastFuelKg;
    private float lastTime;

    private TextMeshProUGUI rangeText;
    private TextMeshProUGUI timeRemText;
    private TextMeshProUGUI flowText;
    private TextMeshProUGUI fuelText;

    public CombatHUD hud;

    private float nextLateUpdateTime;

    private bool established;

    private void Start()
    {
        Plugin.Log.LogDebug($"FuelBurnController Start");
        fuelText = CreateTMP("FUEL");
        timeRemText = CreateTMP("TIME REM");
        flowText = CreateTMP("FLOW");
        rangeText = CreateTMP("RNG");

        TextMeshProUGUI vanillaHUD = GetVanillaHUDText();
        if (!vanillaHUD) return;

        TMP_FontAsset vanillaFont = vanillaHUD.font;

        // ⭐ One shared material for FUEL, FLOW, RNG
        Material matSolid = new(vanillaHUD.fontSharedMaterial);

        // ⭐ One isolated material for TIME REM
        Material matTime = new(vanillaHUD.fontSharedMaterial);

        fuelText.font = vanillaFont;
        timeRemText.font = vanillaFont;
        flowText.font = vanillaFont;
        rangeText.font = vanillaFont;

        fuelText.fontSharedMaterial = matSolid;
        flowText.fontSharedMaterial = matSolid;
        rangeText.fontSharedMaterial = matSolid;

        timeRemText.fontSharedMaterial = matTime;

        ApplyUserColor();
    }

    private void LateUpdate()
    {
        float now = Time.timeSinceLevelLoad;
        if (now < nextLateUpdateTime)
        {
            return;
        }
        Plugin.Log.LogDebug("FuelBurnController LateUpdate()");
        nextLateUpdateTime = now + 1.0f;

        if (!Plugin.ModEnabled.Value)
        {
            fuelText.enabled = false;
            timeRemText.enabled = false;
            flowText.enabled = false;
            rangeText.enabled = false;
            return;
        }

        if (!aircraft)
            return;

        // Reapply user colour for non-critical lines
        ApplyUserColor();

        List<FuelTank> tanks = aircraft.GetFuelTanks();
        if (tanks == null || tanks.Count == 0)
            return;

        float totalKg = 0f;
        foreach (FuelTank t in tanks)
            if (t)
                totalKg += t.fuelMass;

        if (lastTime == 0f)
        {
            lastTime = now;
            lastFuelKg = totalKg;
            flowKgPerSec = 0f;
            hasSample = false;
        }

        if (now - lastTime >= 1f)
        {
            float delta = lastFuelKg - totalKg;
            float dt = now - lastTime;

            lastTime = now;
            lastFuelKg = totalKg;

            if (delta > 0.01f && dt > 0f)
            {
                flowKgPerSec = delta / dt;
                hasSample = true;
            }
            else
            {
                flowKgPerSec = 0f;
                hasSample = false;
            }
        }

        float enduranceSec =
            hasSample && flowKgPerSec > 0f && totalKg > 0f
                ? totalKg / flowKgPerSec
                : 0f;

        int baseX = Plugin.LockedHorizontalOffset;
        int baseY = Plugin.LockedVerticalOffset;

        fuelText.fontSize = Plugin.FontSize.Value;
        timeRemText.fontSize = Plugin.FontSize.Value;
        flowText.fontSize = Plugin.FontSize.Value;
        rangeText.fontSize = Plugin.FontSize.Value;

        // ⭐ Collapse‑upwards layout
        int y = baseY;

        if (Plugin.ShowFUEL.Value)
        {
            fuelText.enabled = true;
            fuelText.rectTransform.anchoredPosition = new Vector2(baseX, y);
            y -= 20;
        }
        else
        {
            fuelText.enabled = false;
        }

        if (Plugin.ShowTIMEREM.Value)
        {
            timeRemText.enabled = true;
            timeRemText.rectTransform.anchoredPosition = new Vector2(baseX, y);
            y -= 20;
        }
        else
        {
            timeRemText.enabled = false;
        }

        if (Plugin.ShowFLOW.Value)
        {
            flowText.enabled = true;
            flowText.rectTransform.anchoredPosition = new Vector2(baseX, y);
            y -= 20;
        }
        else
        {
            flowText.enabled = false;
        }

        if (Plugin.ShowRNG.Value)
        {
            rangeText.enabled = true;
            rangeText.rectTransform.anchoredPosition = new Vector2(baseX, y);
        }
        else
        {
            rangeText.enabled = false;
        }

        bool metric = Plugin.UseMetricUnits.Value;

        // FUEL
        if (Plugin.ShowFUEL.Value)
        {
            if (metric)
                fuelText.text = $"FUEL [{totalKg:0}] kg";
            else
                fuelText.text = $"FUEL [{totalKg * KgToLb:0}] lb";
        }

        // ⭐ TIME REM — isolated critical colour logic
        if (Plugin.ShowTIMEREM.Value)
        {
            if (!hasSample || enduranceSec <= 0f)
            {
                timeRemText.text = "TIME REM (--:--:--)";
                timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, Plugin.HudColor.Value);
            }
            else
            {
                int h = Mathf.FloorToInt(enduranceSec / 3600);
                int m = Mathf.FloorToInt(enduranceSec % 3600 / 60);
                int s = Mathf.FloorToInt(enduranceSec % 60);
                timeRemText.text = $"TIME REM ({h}:{m:D2}:{s:D2})";

                float minutes = enduranceSec / 60f;

                if (minutes <= 1f)
                {
                    flashTimer += Time.deltaTime * 4f;
                    bool flash = Mathf.FloorToInt(flashTimer) % 2 == 0;
                    Color c = flash ? Color.red : Color.yellow;
                    timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, c);
                }
                else if (minutes <= 5f)
                {
                    timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, Color.red);
                }
                else if (minutes <= 15f)
                {
                    timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, Color.yellow);
                }
                else
                {
                    timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, Plugin.HudColor.Value);
                }
            }
        }

        // FLOW
        if (Plugin.ShowFLOW.Value)
        {
            if (!hasSample || flowKgPerSec <= 0f)
            {
                flowText.text = metric ? "FLOW [--] kg/s" : "FLOW [--] lb/s";
            }
            else
            {
                if (metric)
                    flowText.text = $"FLOW [{flowKgPerSec:0.0}] kg/s";
                else
                    flowText.text = $"FLOW [{flowKgPerSec * KgToLb:0.0}] lb/s";
            }
        }

        // RANGE
        if (Plugin.ShowRNG.Value)
        {
            if (!hasSample || enduranceSec <= 0f || aircraft.rb == null)
            {
                rangeText.text = "RNG ----";
            }
            else
            {
                float gs = aircraft.rb.velocity.magnitude;
                float meters = gs * enduranceSec;

                switch (Plugin.RangeUnits.Value)
                {
                    case RangeUnit.Km:
                        float km = meters / 1000f;
                        rangeText.text = $"RNG {km:0}km";
                        break;

                    case RangeUnit.Mile:
                        float mi = meters / 1609.34f;
                        rangeText.text = $"RNG {mi:0}mi";
                        break;

                    default: // Nm
                        float nm = meters / 1852f;
                        rangeText.text = $"RNG {nm:0}nm";
                        break;
                }
            }
        }
    }

    private TextMeshProUGUI GetVanillaHUDText()
    {
        Plugin.Log.LogDebug($"FuelBurnController GetVanillaHUDText");
        CombatHUD hud = FindObjectOfType<CombatHUD>();
        if (!hud)
            return null;

        FieldInfo field = typeof(CombatHUD).GetField("targetInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);

        return field?.GetValue(hud) as TextMeshProUGUI;
    }

    private void ApplyUserColor()
    {
        Plugin.Log.LogDebug($"FuelBurnController User Color");
        Color userColor = Plugin.HudColor.Value;

        fuelText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, userColor);
        flowText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, userColor);
        rangeText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, userColor);

        // TIME REM starts in user colour too
        timeRemText.fontSharedMaterial.SetColor(ShaderUtilities.ID_FaceColor, userColor);
    }

    private TextMeshProUGUI CreateTMP(string name)
    {
        Plugin.Log.LogDebug($"FuelBurnController Creating TMP {name}");
        GameObject go = new(name);
        go.transform.SetParent(transform, false);

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;

        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(800f, 40f);

        return tmp;
    }
}