using System;

namespace SnickerstreamV2.Views;

/// <summary>
/// ScaleFX (Sp00kyFox, MIT) — the reference pixel-art edge-interpolation upscaler, ported from the
/// libretro slang passes to our old-dialect SkSL (own <c>mod</c>; <c>greaterThan</c> for bool-vectors;
/// step reimplemented). 5 passes: metrics → corner strength → junction consolidation → bit-packed edge
/// tags → 3× subpixel output that only ever picks colours present in the original. Parameters at the
/// ScaleFX defaults (CLR 0.5, SAA 1, SCN 1). All passes validated to compile against SkiaSharp 2.88.9.
/// </summary>
internal static class ScaleFx
{
    private static GpuPass[]? _cached;
    private static readonly object _lock = new();
    public static GpuPass[] Passes { get { lock (_lock) { return _cached ??= Build(); } } }

    private const string P0 = """
        uniform shader src;
        float dist3(float3 A, float3 B){ float r=0.5*(A.x+B.x); float3 d=A-B; float3 k=float3(2.0+r,4.0,3.0-r); return sqrt(dot(k*d,d))/3.0; }
        float3 T(float2 c,float dx,float dy){ return float3(sample(src, c+float2(dx,dy)).rgb); }
        half4 main(float2 c){
            float3 A=T(c,-1,-1),B=T(c,0,-1),Cc=T(c,1,-1),E=T(c,0,0),F=T(c,1,0);
            return half4(half(dist3(E,A)),half(dist3(E,B)),half(dist3(E,Cc)),half(dist3(E,F)+4.0));
        }
        """;

    private const string P1 = """
        uniform shader src;
        float str(float d, float2 a, float2 b){
            float CLR=0.5;
            float diff=a.x-a.y;
            float w1=max(CLR-d,0.0)/CLR;
            float cnd=(min(a.x,b.x)+a.x > min(a.y,b.y)+a.y) ? diff : -diff;
            float w2=clamp((1.0-d)+cnd,0.0,1.0);
            return (w1*w2)*(a.x*a.y);
        }
        float4 T(float2 c,float dx,float dy){ float4 s=float4(sample(src, c+float2(dx,dy))); return float4(s.rgb, s.w-4.0); }
        half4 main(float2 c){
            float4 A=T(c,-1,-1),B=T(c,0,-1);
            float4 D=T(c,-1,0),E=T(c,0,0),F=T(c,1,0);
            float4 G=T(c,-1,1),H=T(c,0,1),I=T(c,1,1);
            float rx=str(D.z, float2(D.w,E.y), float2(A.w,D.y));
            float ry=str(F.x, float2(E.w,E.y), float2(B.w,F.y));
            float rz=str(H.z, float2(E.w,H.y), float2(H.w,I.y));
            float rw=str(H.x, float2(D.w,H.y), float2(G.w,G.y));
            return half4(half(rx),half(ry),half(rz),half(rw+4.0));
        }
        """;

    private const string P2 = """
        uniform shader src;
        uniform shader MET;
        float4 stpv(float4 e,float4 x){ return clamp(sign(x-e)+float4(1.0),0.0,1.0); }
        float stps(float e,float x){ return clamp(sign(x-e)+1.0,0.0,1.0); }
        float4 LE(float4 x,float4 y){ return float4(1.0)-stpv(y,x); }
        float4 GE(float4 x,float4 y){ return float4(1.0)-stpv(x,y); }
        float4 LEQ(float4 x,float4 y){ return stpv(x,y); }
        float GEs(float x,float y){ return 1.0-stps(x,y); }
        float4 dom(float3 x,float3 y,float3 z,float3 w){ return 2.0*float4(x.y,y.y,z.y,w.y)-(float4(x.x,y.x,z.x,w.x)+float4(x.z,y.z,z.z,w.z)); }
        float clr1(float2 crn,float2 a,float2 b){ return ((crn.x>=max(min(a.x,a.y),min(b.x,b.y))) && (crn.y>=max(min(a.x,b.y),min(b.x,a.y)))) ? 1.0 : 0.0; }
        float4 Tm(float2 c,float dx,float dy){ float4 s=float4(sample(MET, c+float2(dx,dy))); return float4(s.rgb, s.w-4.0); }
        float4 Ts(float2 c,float dx,float dy){ float4 s=float4(sample(src, c+float2(dx,dy))); return float4(s.rgb, s.w-4.0); }
        half4 main(float2 c){
            float4 A=Tm(c,-1,-1),B=Tm(c,0,-1); float4 D=Tm(c,-1,0),E=Tm(c,0,0),F=Tm(c,1,0); float4 G=Tm(c,-1,1),H=Tm(c,0,1),I=Tm(c,1,1);
            float4 As=Ts(c,-1,-1),Bs=Ts(c,0,-1),Cs=Ts(c,1,-1); float4 Ds=Ts(c,-1,0),Es=Ts(c,0,0),Fs=Ts(c,1,0); float4 Gs=Ts(c,-1,1),Hs=Ts(c,0,1),Is=Ts(c,1,1);
            float4 jSx=float4(As.z,Bs.w,Es.x,Ds.y), jDx=dom(As.yzw,Bs.zwx,Es.wxy,Ds.xyz);
            float4 jSy=float4(Bs.z,Cs.w,Fs.x,Es.y), jDy=dom(Bs.yzw,Cs.zwx,Fs.wxy,Es.xyz);
            float4 jSz=float4(Es.z,Fs.w,Is.x,Hs.y), jDz=dom(Es.yzw,Fs.zwx,Is.wxy,Hs.xyz);
            float4 jSw=float4(Ds.z,Es.w,Hs.x,Gs.y), jDw=dom(Ds.yzw,Es.zwx,Hs.wxy,Gs.xyz);
            float4 z4=float4(0.0), one4=float4(1.0);
            float4 jx=min(GE(jDx,z4)*(LEQ(jDx.yzwx,z4)*LEQ(jDx.wxyz,z4)+GE(jDx+jDx.zwxy, jDx.yzwx+jDx.wxyz)), one4);
            float4 jy=min(GE(jDy,z4)*(LEQ(jDy.yzwx,z4)*LEQ(jDy.wxyz,z4)+GE(jDy+jDy.zwxy, jDy.yzwx+jDy.wxyz)), one4);
            float4 jz=min(GE(jDz,z4)*(LEQ(jDz.yzwx,z4)*LEQ(jDz.wxyz,z4)+GE(jDz+jDz.zwxy, jDz.yzwx+jDz.wxyz)), one4);
            float4 jw=min(GE(jDw,z4)*(LEQ(jDw.yzwx,z4)*LEQ(jDw.wxyz,z4)+GE(jDw+jDw.zwxy, jDw.yzwx+jDw.wxyz)), one4);
            float4 res;
            res.x=min(jx.z + (1.0-jx.y)*(1.0-jx.w)*GEs(jSx.z,0.0)*(jx.x + GEs(jSx.x+jSx.z, jSx.y+jSx.w)), 1.0);
            res.y=min(jy.w + (1.0-jy.z)*(1.0-jy.x)*GEs(jSy.w,0.0)*(jy.y + GEs(jSy.y+jSy.w, jSy.x+jSy.z)), 1.0);
            res.z=min(jz.x + (1.0-jz.w)*(1.0-jz.y)*GEs(jSz.x,0.0)*(jz.z + GEs(jSz.x+jSz.z, jSz.y+jSz.w)), 1.0);
            res.w=min(jw.y + (1.0-jw.x)*(1.0-jw.z)*GEs(jSw.y,0.0)*(jw.w + GEs(jSw.y+jSw.w, jSw.x+jSw.z)), 1.0);
            res=min(res*(float4(jx.z,jy.w,jz.x,jw.y)+(one4-res.wxyz*res.yzwx)), one4);
            float4 clr;
            clr.x=clr1(float2(D.z,E.x), float2(D.w,E.y), float2(A.w,D.y));
            clr.y=clr1(float2(F.x,E.z), float2(E.w,E.y), float2(B.w,F.y));
            clr.z=clr1(float2(H.z,I.x), float2(E.w,H.y), float2(H.w,I.y));
            clr.w=clr1(float2(H.x,G.z), float2(D.w,H.y), float2(G.w,G.y));
            float4 hh=float4(min(D.w,A.w),min(E.w,B.w),min(E.w,H.w),min(D.w,G.w));
            float4 vv=float4(min(E.y,D.y),min(E.y,F.y),min(H.y,I.y),min(H.y,G.y));
            float4 orien=GE(hh+float4(D.w,E.w,E.w,D.w), vv+float4(E.y,E.y,H.y,H.y));
            float4 hori=LE(hh,vv)*clr;
            float4 vert=GE(hh,vv)*clr;
            float4 o=(res + 2.0*hori + 4.0*vert + 8.0*orien)/15.0;
            return half4(o.x,o.y,o.z,o.w+4.0);
        }
        """;

    private const string P3 = """
        uniform shader src;
        float4 mdv(float4 x,float y){ return x - y*floor(x/y); }
        float4 lc(float4 x){ return floor(mdv(x*15.0+0.5,2.0)); }
        float4 lh(float4 x){ return floor(mdv(x*7.5+0.25,2.0)); }
        float4 lv(float4 x){ return floor(mdv(x*3.75+0.125,2.0)); }
        float4 lo(float4 x){ return floor(mdv(x*1.875+0.0625,2.0)); }
        float4 T(float2 c,float dx,float dy){ float4 s=float4(sample(src, c+float2(dx,dy))); return float4(s.rgb, s.w-4.0); }
        half4 main(float2 c){
            float4 H5=float4(0.5);
            float4 E=T(c,0,0);
            float4 D=T(c,-1,0),D0=T(c,-2,0),D1=T(c,-3,0);
            float4 Fv=T(c,1,0),F0=T(c,2,0),F1=T(c,3,0);
            float4 Bv=T(c,0,-1),B0=T(c,0,-2),B1=T(c,0,-3);
            float4 Hv=T(c,0,1),H0=T(c,0,2),H1=T(c,0,3);
            bool4 Ec=greaterThan(lc(E),H5),Eh=greaterThan(lh(E),H5),Ev=greaterThan(lv(E),H5),Eo=greaterThan(lo(E),H5);
            bool4 Dc=greaterThan(lc(D),H5),Dh=greaterThan(lh(D),H5),Do=greaterThan(lo(D),H5),D0c=greaterThan(lc(D0),H5),D0h=greaterThan(lh(D0),H5),D1h=greaterThan(lh(D1),H5);
            bool4 Fc=greaterThan(lc(Fv),H5),Fh=greaterThan(lh(Fv),H5),Fo=greaterThan(lo(Fv),H5),F0c=greaterThan(lc(F0),H5),F0h=greaterThan(lh(F0),H5),F1h=greaterThan(lh(F1),H5);
            bool4 Bc=greaterThan(lc(Bv),H5),Bvt=greaterThan(lv(Bv),H5),Bo=greaterThan(lo(Bv),H5),B0c=greaterThan(lc(B0),H5),B0v=greaterThan(lv(B0),H5),B1v=greaterThan(lv(B1),H5);
            bool4 Hc=greaterThan(lc(Hv),H5),Hvt=greaterThan(lv(Hv),H5),Ho=greaterThan(lo(Hv),H5),H0c=greaterThan(lc(H0),H5),H0v=greaterThan(lv(H0),H5),H1v=greaterThan(lv(H1),H5);
            bool lvl1x=Ec.x && (Dc.z||Bc.z||true);
            bool lvl1y=Ec.y && (Fc.w||Bc.w||true);
            bool lvl1z=Ec.z && (Fc.x||Hc.x||true);
            bool lvl1w=Ec.w && (Dc.y||Hc.y||true);
            bool2 lvl2x=bool2((Ec.x&&Eh.y)&&Dc.z, (Ec.y&&Eh.x)&&Fc.w);
            bool2 lvl2y=bool2((Ec.y&&Ev.z)&&Bc.w, (Ec.z&&Ev.y)&&Hc.x);
            bool2 lvl2z=bool2((Ec.w&&Eh.z)&&Dc.y, (Ec.z&&Eh.w)&&Fc.x);
            bool2 lvl2w=bool2((Ec.x&&Ev.w)&&Bc.z, (Ec.w&&Ev.x)&&Hc.y);
            bool2 lvl3x=bool2(lvl2x.y&&(Dh.y&&Dh.x)&&Fh.z, lvl2w.y&&(Bvt.w&&Bvt.x)&&Hvt.z);
            bool2 lvl3y=bool2(lvl2x.x&&(Fh.x&&Fh.y)&&Dh.w, lvl2y.y&&(Bvt.z&&Bvt.y)&&Hvt.w);
            bool2 lvl3z=bool2(lvl2z.x&&(Fh.w&&Fh.z)&&Dh.x, lvl2y.x&&(Hvt.y&&Hvt.z)&&Bvt.x);
            bool2 lvl3w=bool2(lvl2z.y&&(Dh.z&&Dh.w)&&Fh.y, lvl2w.x&&(Hvt.x&&Hvt.w)&&Bvt.y);
            bool2 lvl4x=bool2((Dc.x&&Dh.y&&Eh.x&&Eh.y&&Fh.x&&Fh.y)&&(D0c.z&&D0h.w), (Bc.x&&Bvt.w&&Ev.x&&Ev.w&&Hvt.x&&Hvt.w)&&(B0c.z&&B0v.y));
            bool2 lvl4y=bool2((Fc.y&&Fh.x&&Eh.y&&Eh.x&&Dh.y&&Dh.x)&&(F0c.w&&F0h.z), (Bc.y&&Bvt.z&&Ev.y&&Ev.z&&Hvt.y&&Hvt.z)&&(B0c.w&&B0v.x));
            bool2 lvl4z=bool2((Fc.z&&Fh.w&&Eh.z&&Eh.w&&Dh.z&&Dh.w)&&(F0c.x&&F0h.y), (Hc.z&&Hvt.y&&Ev.z&&Ev.y&&Bvt.z&&Bvt.y)&&(H0c.x&&H0v.w));
            bool2 lvl4w=bool2((Dc.w&&Dh.z&&Eh.w&&Eh.z&&Fh.w&&Fh.z)&&(D0c.y&&D0h.x), (Hc.w&&Hvt.x&&Ev.w&&Ev.x&&Bvt.w&&Bvt.x)&&(H0c.y&&H0v.z));
            bool2 lvl5x=bool2(lvl4x.x&&(F0h.x&&F0h.y)&&(D1h.z&&D1h.w), lvl4y.x&&(D0h.y&&D0h.x)&&(F1h.w&&F1h.z));
            bool2 lvl5y=bool2(lvl4y.y&&(H0v.y&&H0v.z)&&(B1v.w&&B1v.x), lvl4z.y&&(B0v.z&&B0v.y)&&(H1v.x&&H1v.w));
            bool2 lvl5z=bool2(lvl4w.x&&(F0h.w&&F0h.z)&&(D1h.y&&D1h.x), lvl4z.x&&(D0h.z&&D0h.w)&&(F1h.x&&F1h.y));
            bool2 lvl5w=bool2(lvl4x.y&&(H0v.x&&H0v.w)&&(B1v.z&&B1v.y), lvl4w.y&&(B0v.w&&B0v.x)&&(H1v.y&&H1v.z));
            bool2 lvl6x=bool2(lvl5x.y&&(D1h.y&&D1h.x), lvl5w.y&&(B1v.w&&B1v.x));
            bool2 lvl6y=bool2(lvl5x.x&&(F1h.x&&F1h.y), lvl5y.y&&(B1v.z&&B1v.y));
            bool2 lvl6z=bool2(lvl5z.x&&(F1h.w&&F1h.z), lvl5y.x&&(H1v.y&&H1v.z));
            bool2 lvl6w=bool2(lvl5z.y&&(D1h.z&&D1h.w), lvl5w.x&&(H1v.x&&H1v.w));
            float4 crn;
            crn.x=(lvl1x&&Eo.x||lvl3x.x&&Eo.y||lvl4x.x&&Do.x||lvl6x.x&&Fo.y)?5.0:(lvl1x||lvl3x.y&&!Eo.w||lvl4x.y&&!Bo.x||lvl6x.y&&!Ho.w)?1.0:lvl3x.x?3.0:lvl3x.y?7.0:lvl4x.x?2.0:lvl4x.y?6.0:lvl6x.x?4.0:lvl6x.y?8.0:0.0;
            crn.y=(lvl1y&&Eo.y||lvl3y.x&&Eo.x||lvl4y.x&&Fo.y||lvl6y.x&&Do.x)?5.0:(lvl1y||lvl3y.y&&!Eo.z||lvl4y.y&&!Bo.y||lvl6y.y&&!Ho.z)?3.0:lvl3y.x?1.0:lvl3y.y?7.0:lvl4y.x?4.0:lvl4y.y?6.0:lvl6y.x?2.0:lvl6y.y?8.0:0.0;
            crn.z=(lvl1z&&Eo.z||lvl3z.x&&Eo.w||lvl4z.x&&Fo.z||lvl6z.x&&Do.w)?7.0:(lvl1z||lvl3z.y&&!Eo.y||lvl4z.y&&!Ho.z||lvl6z.y&&!Bo.y)?3.0:lvl3z.x?1.0:lvl3z.y?5.0:lvl4z.x?4.0:lvl4z.y?8.0:lvl6z.x?2.0:lvl6z.y?6.0:0.0;
            crn.w=(lvl1w&&Eo.w||lvl3w.x&&Eo.z||lvl4w.x&&Do.w||lvl6w.x&&Fo.z)?7.0:(lvl1w||lvl3w.y&&!Eo.x||lvl4w.y&&!Ho.w||lvl6w.y&&!Bo.x)?1.0:lvl3w.x?3.0:lvl3w.y?5.0:lvl4w.x?2.0:lvl4w.y?8.0:lvl6w.x?4.0:lvl6w.y?6.0:0.0;
            float4 mid;
            mid.x=(lvl2x.x&&Eo.x||lvl2x.y&&Eo.y||lvl5x.x&&Do.x||lvl5x.y&&Fo.y)?5.0:lvl2x.x?1.0:lvl2x.y?3.0:lvl5x.x?2.0:lvl5x.y?4.0:(Ec.x&&Dc.z&&Ec.y&&Fc.w)?(Eo.x?(Eo.y?5.0:3.0):1.0):0.0;
            mid.y=(lvl2y.x&&!Eo.y||lvl2y.y&&!Eo.z||lvl5y.x&&!Bo.y||lvl5y.y&&!Ho.z)?3.0:lvl2y.x?5.0:lvl2y.y?7.0:lvl5y.x?6.0:lvl5y.y?8.0:(Ec.y&&Bc.w&&Ec.z&&Hc.x)?(!Eo.y?(!Eo.z?3.0:7.0):5.0):0.0;
            mid.z=(lvl2z.x&&Eo.w||lvl2z.y&&Eo.z||lvl5z.x&&Do.w||lvl5z.y&&Fo.z)?7.0:lvl2z.x?1.0:lvl2z.y?3.0:lvl5z.x?2.0:lvl5z.y?4.0:(Ec.z&&Fc.x&&Ec.w&&Dc.y)?(Eo.z?(Eo.w?7.0:1.0):3.0):0.0;
            mid.w=(lvl2w.x&&!Eo.x||lvl2w.y&&!Eo.w||lvl5w.x&&!Bo.x||lvl5w.y&&!Ho.w)?1.0:lvl2w.x?5.0:lvl2w.y?7.0:lvl5w.x?6.0:lvl5w.y?8.0:(Ec.w&&Hc.y&&Ec.x&&Bc.z)?(!Eo.w?(!Eo.x?1.0:5.0):7.0):0.0;
            float4 o=(crn + 9.0*mid)/80.0;
            return half4(o.x,o.y,o.z,o.w+4.0);
        }
        """;

    private const string P4 = """
        uniform shader src;
        uniform shader REF;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        float4 mdv(float4 x,float y){ return x - y*floor(x/y); }
        half4 main(float2 c){
            float2 uv=c/OUT_SIZE;
            float2 sp_px=uv*src_SIZE;
            float2 tc=floor(sp_px)+0.5;
            float4 E=float4(sample(src, tc)); E=float4(E.rgb, E.w-4.0);
            float4 crn=floor(mdv(E*80.0+0.5,9.0));
            float4 mid=floor(mdv(E*8.888888+0.055555,9.0));
            float2 fp=floor(3.0*fract(sp_px));
            float sp=(fp.y==0.0)?((fp.x==0.0)?crn.x:(fp.x==1.0)?mid.x:crn.y):((fp.y==1.0)?((fp.x==0.0)?mid.w:(fp.x==1.0)?0.0:mid.y):((fp.x==0.0)?crn.w:(fp.x==1.0)?mid.z:crn.z));
            float2 res=(sp==0.0)?float2(0,0):(sp==1.0)?float2(-1,0):(sp==2.0)?float2(-2,0):(sp==3.0)?float2(1,0):(sp==4.0)?float2(2,0):(sp==5.0)?float2(0,-1):(sp==6.0)?float2(0,-2):(sp==7.0)?float2(0,1):float2(0,2);
            float2 refc=floor(sp_px)+0.5+res;
            return half4(sample(REF, refc).rgb, 1.0);
        }
        """;

    private static GpuPass[] Build()
    {
        var passes = new[]
        {
            new GpuPass { Sksl = P0, Scale = 1f, F16 = true,  Inputs = new[] { ("src", -1) } },                 // metrics ← original
            new GpuPass { Sksl = P1, Scale = 1f, F16 = true,  Inputs = new[] { ("src", 0) } },                  // strength ← pass0
            new GpuPass { Sksl = P2, Scale = 1f, F16 = true,  Inputs = new[] { ("src", 1), ("MET", 0) } },      // consolidate ← pass1 + pass0 (F16 + alpha bias to survive premul)
            new GpuPass { Sksl = P3, Scale = 1f, F16 = true,  Inputs = new[] { ("src", 2) } },                  // tags ← pass2
            new GpuPass { Sksl = P4, Scale = 3f, F16 = false, Inputs = new[] { ("src", 3), ("REF", -1) } },     // 3× output ← pass3 + original
        };
        foreach (var gp in passes) gp.Effect = SkiaSharp.SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return passes;
    }
}
