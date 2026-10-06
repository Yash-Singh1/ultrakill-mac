// Executes Metal kernels only. No windows, application, audio, or user input.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <cmath>
#include <cstdio>
#include <random>
#include <vector>

int main(int argc, const char** argv) {
    @autoreleasepool {
        if(argc!=3) return 2;
        NSError* error=nil;
        id<MTLDevice> device=MTLCreateSystemDefaultDevice();
        MTLCompileOptions* options=[MTLCompileOptions new]; options.mathMode=MTLMathModeSafe;
        NSString* source=[NSString stringWithContentsOfFile:@(argv[1]) encoding:NSUTF8StringEncoding error:&error];
        id<MTLLibrary> library=[device newLibraryWithSource:source options:options error:&error];
        if(!library) {fprintf(stderr,"%s\n",error.description.UTF8String);return 3;}
        id<MTLCommandQueue> queue=[device newCommandQueue];
        const int count=4096, stride=256;
        std::mt19937 random(883041);
        auto uniform=[&](float a,float b){return std::uniform_real_distribution<float>(a,b)(random);};
        std::vector<float> fixtures(count*stride,0), expected(count*5);
        for(int i=0;i<count;i++) {
            float* row=fixtures.data()+i*stride;
            row[19]=i%5; // cb0[4].w is _ClipPlaneCount in the original DXBC.
            for(int j=20;j<40;j++) row[j]=i%7==0 && j>=36?0:uniform(-2,2);
            float* draw=row+48;
            for(int j=0;j<16;j++) draw[j]=uniform(-3,3);
            draw[16]=draw[21]=draw[26]=draw[31]=1;
            float* input=row+160;
            for(int j=0;j<3;j++) input[j]=uniform(-20,20); input[3]=1;
            row[96+68]=row[96+73]=row[96+78]=row[96+83]=1;
            row[192]=row[193]=row[196]=row[197]=row[198]=row[199]=1;
            float world[3]={};
            for(int axis=0;axis<3;axis++)for(int j=0;j<4;j++)world[axis]+=draw[j*4+axis]*input[j];
            for(int clip=0;clip<5;clip++) {
                float* plane=row+(clip==0?36:20+(clip-1)*4);
                bool disabled=clip==0 ? plane[0]==0&&plane[1]==0&&plane[2]==0&&plane[3]==0 : clip>i%5;
                expected[i*5+clip]=disabled?1:-(world[0]*plane[0]+world[1]*plane[1]+world[2]*plane[2]+plane[3]);
            }
        }
        auto input=[device newBufferWithBytes:fixtures.data() length:fixtures.size()*4 options:MTLResourceStorageModeShared];
        NSMutableDictionary* report=[NSMutableDictionary dictionary]; bool passed=true;
        for(NSString* name in @[@"before_clip",@"after_clip"]) {
            auto pipeline=[device newComputePipelineStateWithFunction:[library newFunctionWithName:name] error:&error];
            if(!pipeline){fprintf(stderr,"%s\n",error.description.UTF8String);return 4;}
            auto output=[device newBufferWithLength:count*5*4 options:MTLResourceStorageModeShared];
            auto command=[queue commandBuffer];auto encoder=[command computeCommandEncoder];
            [encoder setComputePipelineState:pipeline];[encoder setBuffer:input offset:0 atIndex:0];[encoder setBuffer:output offset:0 atIndex:1];
            [encoder dispatchThreads:MTLSizeMake(count,1,1) threadsPerThreadgroup:MTLSizeMake(64,1,1)];[encoder endEncoding];[command commit];[command waitUntilCompleted];
            if(command.error){fprintf(stderr,"%s\n",command.error.description.UTF8String);return 5;}
            int failed=0;double maximum=0;float* results=(float*)output.contents;
            for(int j=0;j<count*5;j++) {
                double difference=fabs((double)results[j]-expected[j]);maximum=fmax(maximum,difference);
                if(!std::isfinite(results[j])||difference>0.0001+0.00001*fabs(expected[j]))failed++;
            }
            bool after=[name isEqual:@"after_clip"]; passed=passed&&(after?failed==0:failed==count*3);
            report[name]=@{@"values":@(count*5),@"outside_tolerance":@(failed),@"max_absolute_difference":@(maximum)};
        }
        const int width=32;
        std::vector<unsigned char> texels(width*width*4);
        for(int y=0;y<width;y++)for(int x=0;x<width;x++) {
            int p=(y*width+x)*4;texels[p]=x*7;texels[p+1]=y*7;texels[p+2]=(x^y)*7;texels[p+3]=255;
        }
        auto descriptor=[MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:width height:width mipmapped:NO];descriptor.usage=MTLTextureUsageShaderRead;
        auto texture=[device newTextureWithDescriptor:descriptor];
        [texture replaceRegion:MTLRegionMake2D(0,0,width,width) mipmapLevel:0 withBytes:texels.data() bytesPerRow:width*4];
        std::vector<float> uv(count*4);
        for(int i=0;i<count;i++) {
            // Texel centers avoid ambiguous exact-boundary rounding. Positive and
            // negative repeated tiles, and wide warp-depth changes, cover angles.
            uv[i*4]=(i%32+.5f)/32+(i%9-4);uv[i*4+1]=((i/32)%32+.5f)/32+(i%11-5);
            uv[i*4+2]=i%3==0?.03125f:i%3==1?1.f:32.f;
        }
        auto uvInput=[device newBufferWithBytes:uv.data() length:uv.size()*4 options:MTLResourceStorageModeShared];
        for(NSString* name in @[@"before_sampler",@"after_sampler"])for(int repeat=0;repeat<2;repeat++) {
            MTLSamplerDescriptor* sd=[MTLSamplerDescriptor new];sd.minFilter=sd.magFilter=MTLSamplerMinMagFilterNearest;
            sd.sAddressMode=sd.tAddressMode=repeat?MTLSamplerAddressModeRepeat:MTLSamplerAddressModeClampToEdge;
            auto sampler=[device newSamplerStateWithDescriptor:sd];
            auto pipeline=[device newComputePipelineStateWithFunction:[library newFunctionWithName:name] error:&error];
            if(!pipeline){fprintf(stderr,"%s\n",error.description.UTF8String);return 6;}
            auto output=[device newBufferWithLength:count*16 options:MTLResourceStorageModeShared];
            auto command=[queue commandBuffer];auto encoder=[command computeCommandEncoder];
            [encoder setComputePipelineState:pipeline];[encoder setBuffer:uvInput offset:0 atIndex:0];[encoder setBuffer:output offset:0 atIndex:1];
            [encoder setTexture:texture atIndex:0];[encoder setSamplerState:sampler atIndex:0];
            [encoder dispatchThreads:MTLSizeMake(count,1,1) threadsPerThreadgroup:MTLSizeMake(64,1,1)];[encoder endEncoding];[command commit];[command waitUntilCompleted];
            if(command.error){fprintf(stderr,"%s\n",command.error.description.UTF8String);return 7;}
            int failed=0;float* results=(float*)output.contents;
            for(int i=0;i<count;i++)for(int c=0;c<4;c++) {
                int x=i%32,y=(i/32)%32;float reference=texels[(y*32+x)*4+c]/255.f;
                if(fabs(results[i*4+c]-reference)>0.00001)failed++;
            }
            bool after=[name isEqual:@"after_sampler"];passed=passed&&(after||repeat?failed==0:failed>0);
            report[[NSString stringWithFormat:@"%@_%@",name,repeat?@"repeat":@"clamp"]]=@{@"values":@(count*4),@"outside_tolerance":@(failed)};
        }
        report[@"passed"]=@(passed);report[@"fixtures"]=@(count);report[@"windows_opened"]=@0;
        report[@"scope"]=@"Metal clip outputs versus the original DXBC plane equations and output signature. Metal texture samples versus CPU point/repeat reference. No Windows GPU execution.";
        NSData* json=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:&error];
        [json writeToFile:@(argv[2]) atomically:YES];puts([[NSString alloc]initWithData:json encoding:NSUTF8StringEncoding].UTF8String);
        return passed?0:8;
    }
}
