using Mono.Cecil;
using Mono.Cecil.Cil;

namespace RimForge.Core.Diagnostics;

public enum PatchKind { Prefix, Postfix, Transpiler, Finalizer, Unknown }

/// <summary>One Harmony patch a mod applies to a game (or other mod) method.</summary>
public sealed record HarmonyPatchInfo(string ModId, string TargetType, string TargetMethod, PatchKind Kind, bool CanSkipOriginal, string PatchMethod)
{
    public string Target => $"{TargetType}.{TargetMethod}";
}

/// <summary>What one mod DLL contains: its top-level types and the Harmony patches it declares.</summary>
public sealed record AssemblyScan(string Path, string AssemblyName, IReadOnlyList<string> Types, IReadOnlyList<HarmonyPatchInfo> Patches);

/// <summary>Reads mod DLLs with Mono.Cecil without loading or running them.</summary>
public static class AssemblyScanner
{
    // Shared libraries many mods ship; they never contain a mod's own patches.
    private static readonly string[] SkippedPrefixes =
        ["0Harmony", "Harmony", "0MultiplayerAPI", "0PrepatcherAPI", "Mono.", "System.", "Microsoft.", "Unity", "Newtonsoft", "netstandard"];

    public static bool IsSharedLibrary(string assemblyName) =>
        SkippedPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static AssemblyScan? Scan(string path, string modId)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (IsSharedLibrary(name)) return null;
        try
        {
            using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadSymbols = false, InMemory = true });
            var types = module.Types
                .Where(t => t.Name != "<Module>" && !t.Name.StartsWith('<'))
                .Select(t => t.FullName)
                .ToList();
            var patches = new List<HarmonyPatchInfo>();
            foreach (var type in module.GetTypes())
            {
                ReadAttributePatches(type, modId, patches);
                ReadManualPatches(type, modId, patches);
            }
            return new AssemblyScan(path, module.Assembly?.Name.Name ?? name, types, patches);
        }
        catch (Exception)
        {
            // Not a .NET assembly or a damaged one: nothing to learn from it.
            return null;
        }
    }

    // ── [HarmonyPatch] attributes ──

    private sealed record Target(string? Type, string? Method);

    private static void ReadAttributePatches(TypeDefinition type, string modId, List<HarmonyPatchInfo> patches)
    {
        var classTarget = Merge(type.CustomAttributes.Where(IsHarmonyPatch).Select(ReadTarget));
        var classHasPatch = type.CustomAttributes.Any(IsHarmonyPatch);

        foreach (var method in type.Methods)
        {
            var kind = KindOf(method);
            if (kind is null) continue;
            var methodAttrs = method.CustomAttributes.Where(IsHarmonyPatch).ToList();
            if (!classHasPatch && methodAttrs.Count == 0) continue;

            var target = Merge([classTarget, .. methodAttrs.Select(ReadTarget)]);
            if (target.Type is null || target.Method is null) continue; // resolved at run time (TargetMethod) — unknown here

            var canSkip = kind == PatchKind.Prefix && method.ReturnType.FullName == "System.Boolean";
            patches.Add(new HarmonyPatchInfo(modId, Clean(target.Type), target.Method, kind.Value, canSkip, $"{type.FullName}.{method.Name}"));
        }
    }

    private static bool IsHarmonyPatch(CustomAttribute a) =>
        a.AttributeType.Name is "HarmonyPatch" or "HarmonyPatchAttribute";

    private static PatchKind? KindOf(MethodDefinition method)
    {
        foreach (var a in method.CustomAttributes)
        {
            switch (a.AttributeType.Name)
            {
                case "HarmonyPrefix": return PatchKind.Prefix;
                case "HarmonyPostfix": return PatchKind.Postfix;
                case "HarmonyTranspiler": return PatchKind.Transpiler;
                case "HarmonyFinalizer": return PatchKind.Finalizer;
            }
        }
        return method.Name switch
        {
            "Prefix" => PatchKind.Prefix,
            "Postfix" => PatchKind.Postfix,
            "Transpiler" => PatchKind.Transpiler,
            "Finalizer" => PatchKind.Finalizer,
            _ => null,
        };
    }

    /// <summary>
    /// Reads HarmonyPatch(typeof(T)), ("Method"), (typeof(T), "Method"), ("Type", "Method"),
    /// (typeof(T), MethodType.Getter) and similar forms.
    /// </summary>
    private static Target ReadTarget(CustomAttribute attribute)
    {
        string? type = null, method = null;
        var strings = new List<string>();
        int? methodType = null;
        foreach (var arg in attribute.ConstructorArguments)
        {
            switch (arg.Value)
            {
                case TypeReference t: type ??= t.FullName; break;
                case string s: strings.Add(s); break;
                case int i when arg.Type.Name == "MethodType": methodType = i; break;
            }
        }

        if (type is null && strings.Count >= 2)
        {
            // ("Namespace.Type", "Method") or ("Namespace.Type, Assembly", "Method")
            type = strings[0].Split(',')[0].Trim();
            method = strings[1];
        }
        else if (strings.Count > 0)
        {
            method = strings[0];
        }

        method = methodType switch
        {
            1 when method is not null => "get_" + method,
            2 when method is not null => "set_" + method,
            3 => ".ctor",
            4 => ".cctor",
            _ => method,
        };
        return new Target(type, method);
    }

    private static Target Merge(IEnumerable<Target> parts)
    {
        string? type = null, method = null;
        foreach (var p in parts)
        {
            type = p.Type ?? type;
            method = p.Method ?? method;
        }
        return new Target(type, method);
    }

    private static string Clean(string typeName) => typeName.Replace('/', '+');

    // ── harmony.Patch(AccessTools.Method(typeof(T), "Name"), ...) ──

    private static void ReadManualPatches(TypeDefinition type, string modId, List<HarmonyPatchInfo> patches)
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            var body = method.Body.Instructions;
            // Only methods that really call Harmony.Patch count; AccessTools is also used for plain reflection.
            if (!body.Any(i => i.Operand is MethodReference m && m.DeclaringType.Name == "Harmony" && m.Name == "Patch"))
                continue;

            for (var i = 0; i < body.Count; i++)
            {
                if (body[i].Operand is not MethodReference call || call.DeclaringType.Name != "AccessTools") continue;
                var targetMethod = call.Name switch
                {
                    "Method" or "DeclaredMethod" => FindString(body, i),
                    "PropertyGetter" or "DeclaredPropertyGetter" => FindString(body, i) is { } p ? "get_" + p : null,
                    "PropertySetter" or "DeclaredPropertySetter" => FindString(body, i) is { } p ? "set_" + p : null,
                    _ => null,
                };
                if (targetMethod is null) continue;

                var targetType = FindType(body, i);
                if (targetType is null && targetMethod.Contains(':'))
                {
                    // AccessTools.Method("Namespace.Type:Method")
                    var parts = targetMethod.Split(':', 2);
                    (targetType, targetMethod) = (parts[0], parts[1]);
                }
                if (targetType is null) continue;
                patches.Add(new HarmonyPatchInfo(modId, Clean(targetType), targetMethod, PatchKind.Unknown, false, $"{type.FullName}.{method.Name}"));
            }
        }
    }

    private static string? FindString(IList<Instruction> body, int callIndex)
    {
        for (var j = callIndex - 1; j >= Math.Max(0, callIndex - 12); j--)
            if (body[j].OpCode == OpCodes.Ldstr) return (string)body[j].Operand;
        return null;
    }

    private static string? FindType(IList<Instruction> body, int callIndex)
    {
        for (var j = callIndex - 1; j >= Math.Max(0, callIndex - 12); j--)
            if (body[j].OpCode == OpCodes.Ldtoken && body[j].Operand is TypeReference t) return t.FullName;
        return null;
    }
}
