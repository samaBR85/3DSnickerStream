// MetalFX spatial-upscaling helper for 3DSnickerStream (macOS/Apple Silicon exclusive).
//
// Exposes a tiny C ABI the .NET app P/Invokes: hand in a native BGRA8 frame, get back a MetalFX
// spatially-upscaled BGRA8 frame. MetalFX (MTLFXSpatialScaler) is Apple's ML-assisted upscaler, built
// on AMD FSR2 — the highest-quality option on Apple Silicon. The device/queue/scaler/textures are
// cached and only rebuilt when the resolution changes; calls are serialised (the render thread drives it).
//
// Build:
//   clang -x objective-c -fobjc-arc -mmacosx-version-min=13.0 -arch arm64 -dynamiclib \
//     -framework Foundation -framework Metal -framework MetalFX \
//     -o libmetalfx_helper.dylib metalfx_helper.m

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <MetalFX/MetalFX.h>

typedef struct {
    id<MTLDevice> device;
    id<MTLCommandQueue> queue;
    id<MTLFXSpatialScaler> scaler;
    id<MTLTexture> inTex;
    id<MTLTexture> outTex;
    int inW, inH, outW, outH;
} MFXCtx;

static MFXCtx g;
static NSLock *gLock;

__attribute__((constructor))
static void mfx_ctor(void) { gLock = [NSLock new]; }

// Returns 1 if this machine can run MetalFX spatial scaling, else 0.
int mfx_available(void) {
    @autoreleasepool {
        id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
        if (!dev) return 0;
        return [MTLFXSpatialScalerDescriptor supportsDevice:dev] ? 1 : 0;
    }
}

// Builds (or rebuilds on a size change) the device, scaler and I/O textures. Returns 0 on success.
static int mfx_ensure(int inW, int inH, int outW, int outH) {
    if (!g.device) {
        g.device = MTLCreateSystemDefaultDevice();
        if (!g.device) return -1;
        g.queue = [g.device newCommandQueue];
        if (!g.queue) return -1;
    }
    if (g.scaler && g.inW == inW && g.inH == inH && g.outW == outW && g.outH == outH) return 0;

    MTLFXSpatialScalerDescriptor *d = [[MTLFXSpatialScalerDescriptor alloc] init];
    d.inputWidth = (NSUInteger)inW;
    d.inputHeight = (NSUInteger)inH;
    d.outputWidth = (NSUInteger)outW;
    d.outputHeight = (NSUInteger)outH;
    d.colorTextureFormat = MTLPixelFormatBGRA8Unorm;
    d.outputTextureFormat = MTLPixelFormatBGRA8Unorm;
    d.colorProcessingMode = MTLFXSpatialScalerColorProcessingModePerceptual;

    id<MTLFXSpatialScaler> scaler = [d newSpatialScalerWithDevice:g.device];
    if (!scaler) return -2;

    MTLTextureDescriptor *itd =
        [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                                           width:(NSUInteger)inW
                                                          height:(NSUInteger)inH
                                                       mipmapped:NO];
    itd.usage = MTLTextureUsageShaderRead;
    itd.storageMode = MTLStorageModeShared;
    id<MTLTexture> inTex = [g.device newTextureWithDescriptor:itd];

    MTLTextureDescriptor *otd =
        [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                                           width:(NSUInteger)outW
                                                          height:(NSUInteger)outH
                                                       mipmapped:NO];
    otd.usage = MTLTextureUsageShaderWrite | MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
    otd.storageMode = MTLStorageModeShared;
    id<MTLTexture> outTex = [g.device newTextureWithDescriptor:otd];

    if (!inTex || !outTex) return -1;

    scaler.colorTexture = inTex;
    scaler.outputTexture = outTex;

    g.scaler = scaler;
    g.inTex = inTex;
    g.outTex = outTex;
    g.inW = inW; g.inH = inH; g.outW = outW; g.outH = outH;
    return 0;
}

// Upscales a BGRA8 frame (src, inW×inH, tightly packed) to dst (outW×outH). Returns 0 on success,
// negative on failure (caller falls back to a shader/CPU path).
int mfx_upscale(const uint8_t *src, int inW, int inH, int outW, int outH, uint8_t *dst) {
    if (!src || !dst || inW <= 0 || inH <= 0 || outW <= 0 || outH <= 0) return -10;
    [gLock lock];
    @try {
        int rc = mfx_ensure(inW, inH, outW, outH);
        if (rc != 0) return rc;
        @autoreleasepool {
            [g.inTex replaceRegion:MTLRegionMake2D(0, 0, inW, inH)
                       mipmapLevel:0
                         withBytes:src
                       bytesPerRow:(NSUInteger)(inW * 4)];

            id<MTLCommandBuffer> cb = [g.queue commandBuffer];
            [g.scaler encodeToCommandBuffer:cb];
            [cb commit];
            [cb waitUntilCompleted];
            if (cb.status != MTLCommandBufferStatusCompleted) return -3;

            [g.outTex getBytes:dst
                   bytesPerRow:(NSUInteger)(outW * 4)
                    fromRegion:MTLRegionMake2D(0, 0, outW, outH)
                   mipmapLevel:0];
        }
        return 0;
    }
    @catch (NSException *e) { return -4; }
    @finally { [gLock unlock]; }
}
