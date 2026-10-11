using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// usage: HookCheck <path to Assembly-CSharp.dll>
var asm = AssemblyDefinition.ReadAssembly(args[0]);
var types = asm.MainModule.GetTypes().ToDictionary(t => t.FullName.Replace('/', '+'));
int bad = 0;
void Need(bool ok, string what) { Console.WriteLine((ok ? "ok    " : "MISSING ") + what); if (!ok) bad++; }

void Method(string type, string name, params string[] paramNames)
{
    var t = types[type];
    var ms = t.Methods.Where(m => m.Name == name).ToList();
    Need(ms.Count == 1, $"{type}.{name} exists once (found {ms.Count})");
    if (ms.Count == 1)
        foreach (var p in paramNames) Need(ms[0].Parameters.Any(x => x.Name == p), $"  parameter '{p}'");
}
Method("Controller", "GetAxis", "nameWithControl");
Method("Controller", "GetButton", "nameWithControl", "isGetButtonDown", "isGetButtonUp");
Method("InputController", "GetAxis", "nameWithControl");
Method("InputController", "GetButton", "nameWithControl", "isGetButtonDown", "isGetButtonUp");
Method("InputController", "GetIsControlTypeAvailable", "controlType");
Method("StageLoader", "LoadingStage", "stageNum");
Method("Savegame", "SaveToFile");
Method("Menu_Main", "Start");
Method("Menu_Main", "Navigation");
Method("Menu_Main", "MoveSelector", "option");
void Field(string type, string name, string fieldType) => Need(types[type].Fields.Any(f => f.Name == name && f.FieldType.FullName == fieldType), $"{type}.{name} : {fieldType}");
Field("Menu_Main", "RemarkedOption", "System.Int32");
Field("Menu_Main", "SelectorPosition", "System.Single[][]");
Field("Menu_Main", "Buttons", "UnityEngine.RectTransform[]");
Field("Menu_Main", "ButtonsFinalPosY", "System.Single[]");
Field("Menu_Main", "ButtonsInitialPosY", "System.Single[]");
Field("Menu_Main", "TimeBetweenButtonsCount", "System.Single[]");
Field("Menu_Main", "NavigationControlV", "System.String");
Field("Menu_Main", "isVerticalMovementDone", "System.Boolean");
Field("MenuBase", "AudioSource_Effects", "UnityEngine.AudioSource");
Field("MenuBase", "Audio_Button1", "UnityEngine.AudioClip");
Field("MenuBase", "Audio_Button2", "UnityEngine.AudioClip");
Need(types["StageController"].Fields.Any(f => f.Name == "PlayerHealth" && f.FieldType.FullName == "UnityEngine.UI.Image[]"), "StageController.PlayerHealth : Image[]");
Need(types["StageController"].Fields.Any(f => f.Name == "PlayerEnergy" && f.FieldType.FullName == "UnityEngine.UI.Image[]"), "StageController.PlayerEnergy : Image[]");
var data = types["GameData+Data"];
foreach (var f in new[] { "BattleRounds", "ItemsEnabled", "HazardsEnabled", "TimingIsEnabled", "DamageHandicap", "AllowStageUltraModes", "Difficulty" })
    Need(data.Fields.Any(x => x.Name == f), "GameData.Data." + f);
Console.WriteLine("form toggles synced: " + string.Join(", ", data.Fields.Where(f => f.FieldType.FullName == "System.Boolean" && f.Name.StartsWith("IsForm") && !f.Name.EndsWith("_Obtained") && !f.Name.EndsWith("_AlreadyUsed")).Select(f => f.Name)));

var timers = new[] { "get_time", "get_realtimeSinceStartup", "get_unscaledTime", "get_unscaledDeltaTime" };
bool Excluded(string n) => n.StartsWith("Menu_") || n == "Savegame" || n.StartsWith("SavingSpinner") || n == "StageLoader" || n == "SoundManager" || n.Contains("UVAnimatedTexture") || n == "UnscaledTimeParticleSystem";
var clock = asm.MainModule.GetTypes().Where(t => { var o = t; while (o.DeclaringType != null) o = o.DeclaringType; return !Excluded(o.Name); })
    .SelectMany(t => t.Methods.Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "UnityEngine.Time" && timers.Contains(r.Name))))
    .ToList();
Console.WriteLine($"clock-patched methods: {clock.Count}");
foreach (var m in clock.Where(m => m.DeclaringType.Name.Contains("<") || m.DeclaringType.Name == "Controller")) Console.WriteLine("   e.g. " + m.DeclaringType.FullName + "." + m.Name);
var fixedTypes = asm.MainModule.GetTypes().Where(t => t.Methods.Any(m => m.Name == "FixedUpdate" && m.HasBody && !m.HasParameters)).ToList();
Console.WriteLine($"FixedUpdate scripts driven per frame: {fixedTypes.Count}");
Console.WriteLine(bad == 0 ? "ALL HOOKS FOUND" : bad + " MISSING");
return bad == 0 ? 0 : 1;
