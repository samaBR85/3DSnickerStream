using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace SnickerstreamV2.Views;

/// <summary>One pass of a GPU multi-pass filter: a SkSL effect rendered into an intermediate GPU surface
/// (or the screen, for the last pass), sampling earlier passes / the original frame.</summary>
public sealed class GpuPass
{
    public string Sksl = "";
    public float Scale = 1f;                                  // output size = source × Scale
    public bool F16;                                          // intermediate holds signed feature maps
    public (string child, int from)[] Inputs = Array.Empty<(string, int)>();   // from: -1 = original frame, else pass index
    public SKRuntimeEffect? Effect;                          // compiled lazily
    public string? Error;
}

/// <summary>
/// Anime4K "AI" upscaler (bloc97, MIT): a small CNN expressed as GPU shader passes. We ship the original
/// mpv-hook <c>.glsl</c> files as embedded resources and convert them to our old-dialect SkSL at runtime —
/// the conv weight matrices are kept verbatim as <c>float4x4</c> literals; only the sampling syntax changes.
///
/// <para>Handles every network size (S/M/L/VL) with one generic parser, since they're all generated from
/// the same mpv-shader template with 3 pass shapes:</para>
/// <list type="bullet">
/// <item><b>Offset conv</b> (<c>go_N</c> macros, <c>_texOff</c> reads): 1 input texture (the very first
/// layer, S/M/L/VL alike) or 2 "wide" parallel input textures (L/VL's deeper layers); each becomes a
/// per-offset SkSL function so the 3×3 neighbourhood sampling in the ported body still works.</item>
/// <item><b>Dense combine</b> (<c>g_N</c> macros, <c>_tex</c> reads, no offset): mixes ALL prior conv
/// layers at the SAME position — a per-pixel/fully-connected layer (M has 1 of these with 7 inputs; VL has
/// 3 with 14 inputs each). Each input is sampled once per pixel instead of per weight term.</item>
/// <item><b>Depth-to-space</b>: 1 conv texture (S/M) or 3 parallel ones (L/VL), each contributing one
/// channel-select into the 2×2 output block, added to the bilinear original.</item>
/// </list>
/// <para>Which texture feeds which pass is resolved by name (the <c>!SAVE</c>/<c>!BIND</c> conv names),
/// not by position, so the converter doesn't depend on the textual macro ordering matching creation order.</para>
/// </summary>
internal static class Anime4KCnn
{
    private static readonly Dictionary<string, GpuPass[]> _cache = new();
    private static readonly object _lock = new();

    public static GpuPass[] PassesFor(string resourceName)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(resourceName, out var cached)) return cached;
            var built = Build(resourceName);
            _cache[resourceName] = built;
            return built;
        }
    }

    private static readonly Regex ReOffsetPlain = new(@"^#define (go_\d+)\(x_off, y_off\) \((\w+)_texOff\(vec2\(x_off, y_off\)\)\)\s*$", RegexOptions.Multiline);
    private static readonly Regex ReOffsetRelu = new(@"^#define (go_\d+)\(x_off, y_off\) \(max\((-)?\((\w+)_texOff\(vec2\(x_off, y_off\)\)\), 0\.0\)\)\s*$", RegexOptions.Multiline);
    private static readonly Regex ReDenseRelu = new(@"^#define (g_\d+) \(max\((-)?\((\w+)_tex\(\3_pos\)\), 0\.0\)\)\s*$", RegexOptions.Multiline);

    private static GpuPass[] Build(string resourceName)
    {
        var list = new List<GpuPass>();
        try
        {
            string glsl = ReadResource(resourceName);
            var blocks = Regex.Split(glsl, @"(?=//!DESC)").Where(b => b.Contains("hook()")).ToArray();
            var savedAt = new Dictionary<string, int>();   // conv texture name -> pass index that produced it
            string? depthBlock = null;

            foreach (var p in blocks)
            {
                if (p.Contains("Depth-to-Space")) { depthBlock = p; continue; }

                var saveName = Regex.Match(p, @"//!SAVE (\S+)").Groups[1].Value;
                var body = Regex.Match(p, @"vec4 hook\(\) \{(.*?)return result;", RegexOptions.Singleline).Groups[1].Value;
                body = body.Replace("vec4 result =", "float4 result =").Replace("mat4(", "float4x4(")
                           .Replace("result += vec4(", "result += float4(");

                bool dense = ReDenseRelu.IsMatch(p);
                if (dense)
                {
                    var defs = ReDenseRelu.Matches(p);
                    var texIndex = new Dictionary<string, int>();               // texture name -> IN slot
                    foreach (Match m in defs)
                    {
                        string token = m.Groups[1].Value, sign = m.Groups[2].Value, texname = m.Groups[3].Value;
                        if (!texIndex.TryGetValue(texname, out int idx)) { idx = texIndex.Count; texIndex[texname] = idx; }
                        body = Regex.Replace(body, $@"\b{Regex.Escape(token)}\b", sign == "-" ? $"NEG{idx}" : $"POS{idx}");
                    }
                    var sb = new StringBuilder();
                    foreach (var idx in texIndex.Values) sb.AppendLine($"uniform shader IN{idx};");
                    sb.AppendLine("half4 main(float2 cc){");
                    foreach (var idx in texIndex.Values)
                    {
                        // Alpha is biased +16 in storage so premultiplied GPU surfaces don't corrupt the RGB
                        // feature channels; un-bias it on read before the ReLU split.
                        sb.AppendLine($"  float4 T{idx}=float4(sample(IN{idx}, cc)); T{idx}=float4(T{idx}.rgb, T{idx}.w-16.0);");
                        sb.AppendLine($"  float4 POS{idx}=max(T{idx}, 0.0); float4 NEG{idx}=max(-T{idx}, 0.0);");
                    }
                    sb.Append(body);
                    sb.AppendLine("  return half4(result.xyz, result.w + 16.0);");
                    sb.AppendLine("}");

                    var inputs = new (string, int)[texIndex.Count];
                    foreach (var (texname, idx) in texIndex)
                        inputs[idx] = ($"IN{idx}", texname == "MAIN" ? -1 : savedAt[texname]);
                    list.Add(new GpuPass { Sksl = sb.ToString(), Scale = 1f, F16 = true, Inputs = inputs });
                }
                else
                {
                    var defsPlain = ReOffsetPlain.Matches(p);
                    var defsRelu = ReOffsetRelu.Matches(p);
                    bool relu = defsRelu.Count > 0;
                    var texIndex = new Dictionary<string, int>();
                    var tokenToFn = new Dictionary<string, string>();

                    foreach (Match m in (relu ? defsRelu : defsPlain))
                    {
                        string token = m.Groups[1].Value;
                        string texname = relu ? m.Groups[3].Value : m.Groups[2].Value;
                        string sign = relu ? m.Groups[2].Value : "";
                        if (!texIndex.TryGetValue(texname, out int idx)) { idx = texIndex.Count; texIndex[texname] = idx; }
                        tokenToFn[token] = relu ? (sign == "-" ? $"g{idx}neg" : $"g{idx}pos") : $"g{idx}raw";
                    }
                    foreach (var (token, fn) in tokenToFn)
                        body = Regex.Replace(body, $@"\b{Regex.Escape(token)}\(", fn + "(cc, ");

                    var sb = new StringBuilder();
                    foreach (var idx in texIndex.Values) sb.AppendLine($"uniform shader IN{idx};");
                    foreach (var idx in texIndex.Values)
                    {
                        if (!relu)
                            sb.AppendLine($"float4 g{idx}raw(float2 c, float dx, float dy){{ return float4(sample(IN{idx}, c+float2(dx,dy))); }}");
                        else
                        {
                            sb.AppendLine($"float4 g{idx}pos(float2 c, float dx, float dy){{ float4 s=float4(sample(IN{idx}, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(s, 0.0); }}");
                            sb.AppendLine($"float4 g{idx}neg(float2 c, float dx, float dy){{ float4 s=float4(sample(IN{idx}, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(-s, 0.0); }}");
                        }
                    }
                    sb.AppendLine("half4 main(float2 cc){");
                    sb.Append(body);
                    sb.AppendLine("  return half4(result.xyz, result.w + 16.0);");
                    sb.AppendLine("}");

                    var inputs = new (string, int)[texIndex.Count];
                    foreach (var (texname, idx) in texIndex)
                        inputs[idx] = ($"IN{idx}", texname == "MAIN" ? -1 : savedAt[texname]);
                    list.Add(new GpuPass { Sksl = sb.ToString(), Scale = 1f, F16 = true, Inputs = inputs });
                }

                savedAt[saveName] = list.Count - 1;
            }

            if (depthBlock == null) return Array.Empty<GpuPass>();

            // Depth-to-space: N conv textures (1 for S/M, 3 for L/VL), each contributing one channel-select
            // (via the same hard mix-select trick as before) into the 2×2 output block.
            var binds = Regex.Matches(depthBlock, @"//!BIND (\S+)").Select(m => m.Groups[1].Value).Where(b => b != "MAIN").ToArray();
            var dsb = new StringBuilder();
            dsb.AppendLine("uniform shader MAIN;");
            for (int i = 0; i < binds.Length; i++) dsb.AppendLine($"uniform shader CONV{i};");
            dsb.AppendLine("uniform float2 MAIN_SIZE;");
            dsb.AppendLine("uniform float2 CONV0_SIZE;");   // all CONV textures share the same size — CONV0's suffices
            dsb.AppendLine("uniform float2 OUT_SIZE;");
            dsb.AppendLine("half4 main(float2 c){");
            dsb.AppendLine("  float2 uv = c / OUT_SIZE;");
            dsb.AppendLine("  float2 p2 = uv * (2.0 * CONV0_SIZE);");
            dsb.AppendLine("  float2 t1 = floor(p2*0.5) + 0.5;");
            dsb.AppendLine("  float2 sub = floor(p2 - 2.0*floor(p2*0.5));");
            for (int i = 0; i < binds.Length; i++)
            {
                dsb.AppendLine($"  float4 v{i}=float4(sample(CONV{i}, t1)); v{i}=float4(v{i}.rgb, v{i}.w-16.0);");
                dsb.AppendLine($"  half ch{i}=half(mix(mix(v{i}.x,v{i}.y,sub.x), mix(v{i}.z,v{i}.w,sub.x), sub.y));");
            }
            dsb.AppendLine("  float2 mp = uv*MAIN_SIZE - 0.5; float2 mf = fract(mp); float2 mb = floor(mp)+0.5;");
            dsb.AppendLine("  half3 c00=sample(MAIN, mb).rgb, c10=sample(MAIN, mb+float2(1,0)).rgb;");
            dsb.AppendLine("  half3 c01=sample(MAIN, mb+float2(0,1)).rgb, c11=sample(MAIN, mb+float2(1,1)).rgb;");
            dsb.AppendLine("  half3 top=mix(c00,c10,half(mf.x)), bot=mix(c01,c11,half(mf.x));");
            // 1 conv texture (S/M): its single channel drives R=G=B. 3 (L/VL): one channel each into R/G/B.
            string chExpr = binds.Length == 1 ? "half3(ch0,ch0,ch0)" : "half3(ch0,ch1,ch2)";
            dsb.AppendLine($"  return half4(mix(top,bot,half(mf.y)) + {chExpr}, 1.0);");
            dsb.AppendLine("}");

            var depthInputs = new (string, int)[1 + binds.Length];
            depthInputs[0] = ("MAIN", -1);
            for (int i = 0; i < binds.Length; i++) depthInputs[i + 1] = ($"CONV{i}", savedAt[binds[i]]);
            list.Add(new GpuPass { Sksl = dsb.ToString(), Scale = 2f, F16 = false, Inputs = depthInputs });
        }
        catch { return Array.Empty<GpuPass>(); }

        foreach (var gp in list) gp.Effect = SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return list.ToArray();
    }

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames().First(n => n.EndsWith(name, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(full)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
