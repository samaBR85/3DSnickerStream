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
/// mpv-hook <c>.glsl</c> as an embedded resource and convert it to our old-dialect SkSL at runtime — the
/// conv weight matrices are kept verbatim as <c>float4x4</c> literals; only the sampling syntax changes.
///
/// <para>Supports the "S" and "M" network sizes, which share the same single-input-per-pass conv chain
/// topology (source → conv0 → conv1 → … → depth-to-space). "M" additionally has one "dense combine" pass
/// before depth-to-space that mixes ALL prior conv layers' outputs at the SAME position (no neighbourhood
/// sampling — a 1×1/fully-connected layer), detected by its distinctive <c>g_0</c>/<c>g_1</c>/… macros.
/// "L"/"VL" use a wider two-texture-per-layer topology and aren't supported by this converter yet.</para>
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

    // Depth-to-space (hand-written; old dialect, no dynamic indexing / mod). Unpacks the last conv layer's
    // 4 channels into the 2×2 output block and adds the bilinearly-upsampled original colour.
    // Resolution-independent: the final pass draws the native rect but is evaluated at display resolution,
    // so we work in normalized uv and derive the 2× grid from CONV_SIZE (rather than assuming coord spans 2×).
    private const string DepthSksl = """
        uniform shader MAIN;
        uniform shader CONV;
        uniform float2 MAIN_SIZE;
        uniform float2 CONV_SIZE;
        uniform float2 OUT_SIZE;
        half4 main(float2 c){
            float2 uv = c / OUT_SIZE;                         // [0..1]
            float2 p2 = uv * (2.0 * CONV_SIZE);              // position in the 2× output grid
            float2 t1 = floor(p2*0.5) + 0.5;                 // 1× conv texel centre
            float4 v = float4(sample(CONV, t1)); v = float4(v.rgb, v.w - 16.0);   // un-bias the +16 alpha
            float2 sub = floor(p2 - 2.0*floor(p2*0.5));       // (0/1, 0/1) sub-pixel within the 2×2 block
            half ch = mix(mix(half(v.x), half(v.y), half(sub.x)), mix(half(v.z), half(v.w), half(sub.x)), half(sub.y));
            float2 mp = uv*MAIN_SIZE - 0.5; float2 mf = fract(mp); float2 mb = floor(mp)+0.5;
            half3 c00=sample(MAIN, mb).rgb, c10=sample(MAIN, mb+float2(1,0)).rgb;
            half3 c01=sample(MAIN, mb+float2(0,1)).rgb, c11=sample(MAIN, mb+float2(1,1)).rgb;
            half3 top=mix(c00,c10,half(mf.x)), bot=mix(c01,c11,half(mf.x));
            return half4(mix(top,bot,half(mf.y)) + half3(ch), 1.0);
        }
        """;

    private static GpuPass[] Build(string resourceName)
    {
        var list = new List<GpuPass>();
        try
        {
            string glsl = ReadResource(resourceName);
            var blocks = Regex.Split(glsl, @"(?=//!DESC)").Where(p => p.Contains("hook()")).ToArray();
            var convFrom = new List<int>();     // pass index of each single-input conv layer's output, in creation order

            foreach (var p in blocks)
            {
                if (p.Contains("Depth-to-Space")) continue;

                // The "M"-style dense-combine pass: distinct g_0/g_1/... macros, reads N prior conv layers
                // at the SAME position (no neighbourhood — a per-pixel channel mix), one pass before depth.
                if (Regex.IsMatch(p, @"#define g_\d+ "))
                {
                    var binds = Regex.Matches(p, @"//!BIND (\S+)").Select(m => m.Groups[1].Value).ToArray();
                    var body = Regex.Match(p, @"vec4 hook\(\) \{(.*?)return result;", RegexOptions.Singleline).Groups[1].Value;
                    body = body.Replace("vec4 result =", "float4 result =").Replace("mat4(", "float4x4(")
                               .Replace("result += vec4(", "result += float4(");
                    for (int k = 0; k < binds.Length; k++)
                    {
                        body = Regex.Replace(body, $@"\bg_{2 * k}\b", $"IN{k}p");
                        body = Regex.Replace(body, $@"\bg_{2 * k + 1}\b", $"IN{k}n");
                    }
                    var sb = new StringBuilder();
                    for (int k = 0; k < binds.Length; k++) sb.AppendLine($"uniform shader IN{k};");
                    sb.AppendLine("half4 main(float2 cc){");
                    for (int k = 0; k < binds.Length; k++)
                    {
                        sb.AppendLine($"  float4 t{k}=float4(sample(IN{k}, cc)); t{k}=float4(t{k}.rgb, t{k}.w-16.0);");
                        sb.AppendLine($"  float4 IN{k}p=max(t{k}, 0.0); float4 IN{k}n=max(-t{k}, 0.0);");
                    }
                    sb.Append(body);
                    sb.AppendLine("  return half4(result.xyz, result.w + 16.0);");
                    sb.AppendLine("}");
                    // The dense pass's !BIND order matches the conv chain's creation order (verified against
                    // the M template), so binds[k] maps positionally to convFrom[k].
                    var inputs = new (string, int)[binds.Length];
                    for (int k = 0; k < binds.Length; k++)
                        inputs[k] = ($"IN{k}", binds[k] == "MAIN" ? -1 : convFrom[k]);
                    list.Add(new GpuPass { Sksl = sb.ToString(), Scale = 1f, F16 = true, Inputs = inputs });
                    continue;
                }

                // Simple single-input conv pass (go_0 = raw/positive, go_1 = negative half via ReLU split).
                bool relu = p.Contains("max((");
                var body2 = Regex.Match(p, @"vec4 hook\(\) \{(.*?)return result;", RegexOptions.Singleline).Groups[1].Value;
                body2 = body2.Replace("vec4 result =", "float4 result =").Replace("mat4(", "float4x4(");
                body2 = Regex.Replace(body2, @"go_0\(", "g0(cc, ");
                body2 = Regex.Replace(body2, @"go_1\(", "g1(cc, ");
                body2 = body2.Replace("result += vec4(", "result += float4(");

                var sb2 = new StringBuilder();
                sb2.AppendLine("uniform shader IN;");
                // Alpha is biased +16 in storage so premultiplied GPU surfaces don't corrupt the RGB feature
                // channels (they only round-trip when alpha>0); un-bias it on read before the ReLU.
                if (relu)
                {
                    sb2.AppendLine("float4 g0(float2 c, float dx, float dy){ float4 s=float4(sample(IN, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(s, 0.0); }");
                    sb2.AppendLine("float4 g1(float2 c, float dx, float dy){ float4 s=float4(sample(IN, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(-s, 0.0); }");
                }
                else
                {
                    sb2.AppendLine("float4 g0(float2 c, float dx, float dy){ return float4(sample(IN, c+float2(dx,dy))); }");
                }
                sb2.AppendLine("half4 main(float2 cc){");
                sb2.Append(body2);
                sb2.AppendLine("  return half4(result.xyz, result.w + 16.0);");
                sb2.AppendLine("}");

                int from = convFrom.Count == 0 ? -1 : convFrom[^1];   // pass 1 ← original, else previous conv
                list.Add(new GpuPass { Sksl = sb2.ToString(), Scale = 1f, F16 = true, Inputs = new[] { ("IN", from) } });
                convFrom.Add(list.Count - 1);
            }
            // Depth-to-space: original + last pre-depth pass → 2×.
            list.Add(new GpuPass { Sksl = DepthSksl, Scale = 2f, F16 = false, Inputs = new[] { ("MAIN", -1), ("CONV", list.Count - 1) } });
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
