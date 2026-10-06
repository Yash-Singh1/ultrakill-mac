// Offscreen Metal check. No AppKit, NSApplication, windows, or event loop.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <vector>

int main(int argc, const char **argv) {
    @autoreleasepool {
        if (argc != 4) return 2;
        id<MTLDevice> device = MTLCreateSystemDefaultDevice();
        NSError *error = nil;
        NSString *source = [NSString stringWithContentsOfFile:@(argv[1]) encoding:NSUTF8StringEncoding error:&error];
        source = [source stringByAppendingString:@R"(
struct FixtureOut {
    float4 position [[position]];
    float4 COLOR0 [[user(COLOR0)]];
    float4 COLOR2 [[user(COLOR2)]];
    float4 TEXCOORD0 [[user(TEXCOORD0)]];
    float4 TEXCOORD1 [[user(TEXCOORD1)]];
};
vertex FixtureOut fixtureVertex(uint id [[vertex_id]], constant float4& params [[buffer(0)]]) {
    const float2 positions[] = {float2(-1,-1),float2(3,-1),float2(-1,3)};
    FixtureOut out;
    float2 position = positions[id];
    out.position = float4(position,0,1);
    out.COLOR0 = float4(1);
    out.COLOR2 = float4(.45,.2,.05,0);
    out.TEXCOORD0 = float4((position+1)*.5*params.zw+params.xy,0,0);
    out.TEXCOORD1 = float4(0,0,0,1);
    return out;
}
)"];
        MTLCompileOptions *options = [MTLCompileOptions new];
        id<MTLLibrary> library = [device newLibraryWithSource:source options:options error:&error];
        if (!library) { fprintf(stderr,"%s\n",error.description.UTF8String); return 3; }
        uint32_t remap = 0x10;
        MTLFunctionConstantValues *constants = [MTLFunctionConstantValues new];
        [constants setConstantValue:&remap type:MTLDataTypeUInt atIndex:1];
        id<MTLFunction> fragment = [library newFunctionWithName:@"xlatMtlMain" constantValues:constants error:&error];
        MTLRenderPipelineDescriptor *pipelineDesc = [MTLRenderPipelineDescriptor new];
        pipelineDesc.vertexFunction = [library newFunctionWithName:@"fixtureVertex"];
        pipelineDesc.fragmentFunction = fragment;
        for (int i=0;i<2;i++) pipelineDesc.colorAttachments[i].pixelFormat=MTLPixelFormatRGBA8Unorm;
        id<MTLRenderPipelineState> pipeline = [device newRenderPipelineStateWithDescriptor:pipelineDesc error:&error];
        if (!pipeline) { fprintf(stderr,"%s\n",error.description.UTF8String); return 4; }
        NSData *rgba = [NSData dataWithContentsOfFile:@(argv[2])];
        if (rgba.length != 32*32*4) return 5;
        MTLTextureDescriptor *texDesc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:32 height:32 mipmapped:NO];
        texDesc.usage=MTLTextureUsageShaderRead;
        id<MTLTexture> texture = [device newTextureWithDescriptor:texDesc];
        [texture replaceRegion:MTLRegionMake2D(0,0,32,32) mipmapLevel:0 withBytes:rgba.bytes bytesPerRow:128];
        texDesc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:256 height:256 mipmapped:NO];
        texDesc.usage=MTLTextureUsageRenderTarget;
        id<MTLTexture> targets[2] = {[device newTextureWithDescriptor:texDesc],[device newTextureWithDescriptor:texDesc]};
        float globals[360] = {}; globals[144/4]=1;
        id<MTLCommandQueue> queue = [device newCommandQueue];
        NSMutableArray *results = [NSMutableArray array];
        for (int repeat=0;repeat<2;repeat++) {
            MTLSamplerDescriptor *samplerDesc = [MTLSamplerDescriptor new];
            samplerDesc.minFilter=MTLSamplerMinMagFilterNearest;
            samplerDesc.magFilter=MTLSamplerMinMagFilterNearest;
            samplerDesc.sAddressMode=samplerDesc.tAddressMode=repeat?MTLSamplerAddressModeRepeat:MTLSamplerAddressModeClampToEdge;
            id<MTLSamplerState> sampler = [device newSamplerStateWithDescriptor:samplerDesc];
            for (int phase=0;phase<3;phase++) {
                std::array<float,4> params = {phase==0?0.f:phase==1?2.f:-4.f,phase==0?0.f:phase==1?2.f:-4.f,4,4};
                MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
                for (int i=0;i<2;i++) {
                    pass.colorAttachments[i].texture=targets[i];
                    pass.colorAttachments[i].loadAction=MTLLoadActionClear;
                    pass.colorAttachments[i].storeAction=MTLStoreActionStore;
                    pass.colorAttachments[i].clearColor=MTLClearColorMake(0,0,0,0);
                }
                id<MTLCommandBuffer> command = [queue commandBuffer];
                id<MTLRenderCommandEncoder> encoder = [command renderCommandEncoderWithDescriptor:pass];
                [encoder setRenderPipelineState:pipeline];
                [encoder setVertexBytes:params.data() length:16 atIndex:0];
                [encoder setFragmentBytes:globals length:sizeof(globals) atIndex:0];
                [encoder setFragmentTexture:texture atIndex:0];
                [encoder setFragmentSamplerState:sampler atIndex:0];
                [encoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
                [encoder endEncoding]; [command commit]; [command waitUntilCompleted];
                if (command.error) { fprintf(stderr,"%s\n",command.error.description.UTF8String); return 6; }
                std::vector<unsigned char> pixels(256*256*4);
                [targets[0] getBytes:pixels.data() bytesPerRow:1024 fromRegion:MTLRegionMake2D(0,0,256,256) mipmapLevel:0];
                int holes=0;
                for (int i=3;i<pixels.size();i+=4) if(pixels[i]==0) holes++;
                [results addObject:@{@"bound_sampler":repeat?@"repeat":@"clamp",@"phase":@(phase),@"holes":@(holes),@"pixels":@(256*256)}];
            }
        }
        NSData *json = [NSJSONSerialization dataWithJSONObject:results options:NSJSONWritingPrettyPrinted error:&error];
        if (![json writeToFile:@(argv[3]) atomically:YES]) return 7;
        puts([[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding].UTF8String);
    }
    return 0;
}
