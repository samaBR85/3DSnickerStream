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
/// <para>Structure: 4 conv layers at source resolution (signed feature maps in RgbaF16) then a
/// depth-to-space pass that unpacks the 4 output channels into a 2×2 block and adds the bilinear original.</para>
/// </summary>
internal static class Anime4KCnn
{
    private static GpuPass[]? _cached;
    private static readonly object _lock = new();

    public static GpuPass[] Passes { get { lock (_lock) { return _cached ??= Build(); } } }

    // Depth-to-space (hand-written; old dialect, no dynamic indexing / mod). Unpacks conv2d_last's 4 channels
    // into the 2×2 output block and adds the bilinearly-upsampled original colour.
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

    private static GpuPass[] Build()
    {
        var list = new List<GpuPass>();
        try
        {
            string glsl = ReadResource("Anime4K_Upscale_CNN_x2_S.glsl");
            var blocks = Regex.Split(glsl, @"(?=//!DESC)").Where(p => p.Contains("hook()")).ToArray();
            int convIdx = 0;
            foreach (var p in blocks)
            {
                if (p.Contains("Depth-to-Space")) continue;              // appended at the end
                bool relu = p.Contains("max((");
                var body = Regex.Match(p, @"vec4 hook\(\) \{(.*?)return result;", RegexOptions.Singleline).Groups[1].Value;
                body = body.Replace("vec4 result =", "float4 result =").Replace("mat4(", "float4x4(");
                body = Regex.Replace(body, @"go_0\(", "g0(cc, ");
                body = Regex.Replace(body, @"go_1\(", "g1(cc, ");
                body = body.Replace("result += vec4(", "result += float4(");

                var sb = new StringBuilder();
                sb.AppendLine("uniform shader IN;");
                // Alpha is biased +16 in storage so premultiplied GPU surfaces don't corrupt the RGB feature
                // channels (they only round-trip when alpha>0); un-bias it on read before the ReLU.
                if (relu)
                {
                    sb.AppendLine("float4 g0(float2 c, float dx, float dy){ float4 s=float4(sample(IN, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(s, 0.0); }");
                    sb.AppendLine("float4 g1(float2 c, float dx, float dy){ float4 s=float4(sample(IN, c+float2(dx,dy))); s=float4(s.rgb, s.w-16.0); return max(-s, 0.0); }");
                }
                else
                {
                    sb.AppendLine("float4 g0(float2 c, float dx, float dy){ return float4(sample(IN, c+float2(dx,dy))); }");
                }
                sb.AppendLine("half4 main(float2 cc){");
                sb.Append(body);
                sb.AppendLine("  return half4(result.xyz, result.w + 16.0);");
                sb.AppendLine("}");

                int from = convIdx == 0 ? -1 : convIdx - 1;             // pass 1 ← original, else previous conv
                list.Add(new GpuPass { Sksl = sb.ToString(), Scale = 1f, F16 = true, Inputs = new[] { ("IN", from) } });
                convIdx++;
            }
            // Depth-to-space: original + last conv → 2×.
            list.Add(new GpuPass { Sksl = DepthSksl, Scale = 2f, F16 = false, Inputs = new[] { ("MAIN", -1), ("CONV", convIdx - 1) } });
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
