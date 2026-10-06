// No GUI. Compare original DXBC-derived Metal with rebuilt HLSL-derived Metal.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <random>
#include <vector>

static constexpr int count=4096, stride=2048;
static void matrix(float* buffer,int offset,float angle,float sx,float sy,float sz,float tx,float ty,float tz) {
    float c=cosf(angle),s=sinf(angle);
    const float columns[16]={c*sx,0,-s*sx,0, 0,sy,0,0, s*sz,0,c*sz,0, tx,ty,tz,1};
    memcpy(buffer+offset,columns,sizeof(columns));
}

int main(int argc,const char**argv) {
    @autoreleasepool {
        if(argc!=4) return 2;
        NSError* error=nil;
        id<MTLDevice> device=MTLCreateSystemDefaultDevice();
        NSString* source=[NSString stringWithContentsOfFile:@(argv[1]) encoding:NSUTF8StringEncoding error:&error];
        MTLCompileOptions* options=[MTLCompileOptions new]; options.mathMode=MTLMathModeSafe;
        id<MTLLibrary> library=[device newLibraryWithSource:source options:options error:&error];
        if(!library){fprintf(stderr,"%s\n",error.description.UTF8String);return 3;}
        std::vector<float> fixtures(count*stride,0);
        std::mt19937 random(71983);
        auto uniform=[&](float low,float high){return std::uniform_real_distribution<float>(low,high)(random);};
        for(int i=0;i<count;i++) {
            float* f=fixtures.data()+i*stride;
            f[56/4]=i%3==0?0:1; f[60/4]=i%2; f[144/4]=i%5==0?0:uniform(.001f,1);
            float* camera=f+128; for(int j=0;j<3;j++)camera[16+j]=uniform(-50,50);
            float* lights=f+256;
            for(int light=0;light<8;light++){
                for(int j=0;j<3;j++){
                    lights[28+light*4+j]=i%9==0?0:uniform(0,2);
                    lights[60+light*4+j]=uniform(-50,50);
                    lights[124+light*4+j]=uniform(-1,1);
                }
                lights[60+light*4+3]=light%2;
                lights[92+light*4]=uniform(-1,.5);
                lights[92+light*4+1]=uniform(1,4);
                lights[92+light*4+2]=uniform(0,.05);
            }
            float* draw=f+512;
            float angle=uniform(-3,3),sx=uniform(.2,3),sy=uniform(.2,3),sz=uniform(.2,3);
            float tx=uniform(-30,30),ty=uniform(-30,30),tz=uniform(-30,30);
            matrix(draw,0,angle,sx,sy,sz,tx,ty,tz);
            float c=cosf(angle),s=sinf(angle);
            const float inverse[16]={c/sx,0,s/sz,0, 0,1/sy,0,0, -s/sx,0,c/sz,0,
                (-c*tx+s*tz)/sx,-ty/sy,(-s*tx-c*tz)/sz,1};
            memcpy(draw+16,inverse,sizeof(inverse));
            float* frame=f+640;
            for(int j=0;j<3;j++)frame[j]=uniform(0,.5);
            matrix(frame,36,uniform(-3,3),1,1,1,uniform(-10,10),uniform(-10,10),uniform(-10,10));
            matrix(frame,68,uniform(-3,3),uniform(.1,2),uniform(.1,2),uniform(.1,2),0,0,0);
            frame[68+11]=uniform(-1,1); frame[68+15]=uniform(.02,5);
            for(int j=0;j<3;j++)f[768+j]=uniform(0,1);
            float* material=f+896;
            for(int j=0;j<2;j++){material[j]=uniform(.1,10);material[2+j]=uniform(-8,8);}
            for(int j=0;j<4;j++)material[4+j]=uniform(0,1);
            material[8]=i%3==0?0:i%3==1?1:uniform(0,1);
            material[11]=i%3==0?0:i%3==1?1:uniform(0,1);
            material[12]=uniform(0,10);material[13]=material[12]+uniform(1,100);
            for(int j=0;j<3;j++){f[1024+j]=uniform(-20,20);f[1036+j]=uniform(-1,1);}
            f[1027]=1;
            for(int j=0;j<4;j++)f[1028+j]=uniform(0,1);
            f[1032]=uniform(-10,10); f[1033]=uniform(-10,10);
            for(int j=0;j<4;j++){f[1040+j]=uniform(0,2);f[1044+j]=uniform(0,1);}
            float warp=uniform(.02,30);
            f[1048]=uniform(-100,100)*warp; f[1049]=uniform(-100,100)*warp; f[1055]=warp;
            // Exercise the outline threshold with exact and adjacent float values.
            if(i<768){
                const float thresholds[]={0,1e-4f,std::nextafter(1e-4f,0.f),std::nextafter(1e-4f,1.f),1};
                f[144/4]=thresholds[i%5]; f[1043]=i%7==0?0:1;
                f[1048]=((i%32)+.5f)/32*warp; f[1049]=((i/32)+.5f)/32*warp;
                f[56/4]=1;
            }
        }
        id<MTLBuffer> input=[device newBufferWithBytes:fixtures.data() length:fixtures.size()*sizeof(float) options:MTLResourceStorageModeShared];
        NSData* rgba=[NSData dataWithContentsOfFile:@(argv[2])];
        if(rgba.length!=4096)return 4;
        MTLTextureDescriptor* td=[MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:32 height:32 mipmapped:NO];
        td.usage=MTLTextureUsageShaderRead;
        id<MTLTexture> texture=[device newTextureWithDescriptor:td];
        [texture replaceRegion:MTLRegionMake2D(0,0,32,32) mipmapLevel:0 withBytes:rgba.bytes bytesPerRow:128];
        id<MTLCommandQueue> queue=[device newCommandQueue];
        NSMutableDictionary* report=[NSMutableDictionary dictionary];
        bool passed=true;
        for(NSString* stage in @[@"vertex",@"fragment"]){
            int vectors=[stage isEqual:@"vertex"]?6:2;
            id<MTLBuffer> outputs[2];
            for(int program=0;program<2;program++){
                NSString* name=[NSString stringWithFormat:@"%@_%@_check",program?@"rebuilt":@"original",stage];
                id<MTLComputePipelineState> pipeline=[device newComputePipelineStateWithFunction:[library newFunctionWithName:name] error:&error];
                if(!pipeline){fprintf(stderr,"%s\n",error.description.UTF8String);return 5;}
                outputs[program]=[device newBufferWithLength:count*vectors*16 options:MTLResourceStorageModeShared];
                id<MTLCommandBuffer> command=[queue commandBuffer];
                id<MTLComputeCommandEncoder> encoder=[command computeCommandEncoder];
                [encoder setComputePipelineState:pipeline];
                [encoder setBuffer:input offset:0 atIndex:0]; [encoder setBuffer:outputs[program] offset:0 atIndex:1];
                [encoder setTexture:texture atIndex:0];
                [encoder dispatchThreads:MTLSizeMake(count,1,1) threadsPerThreadgroup:MTLSizeMake(64,1,1)];
                [encoder endEncoding];[command commit];[command waitUntilCompleted];
                if(command.error){fprintf(stderr,"%s\n",command.error.description.UTF8String);return 6;}
            }
            float* a=(float*)outputs[0].contents;float* b=(float*)outputs[1].contents;
            NSArray* names=vectors==6?@[@"clip_position",@"lit_color",@"fog",@"warped_uv",@"world_and_warp",@"world_normal"]:@[@"color_alpha",@"outline"];
            NSMutableArray* fields=[NSMutableArray array];
            for(int v=0;v<vectors;v++){
                double maxAbs=0;int failures=0,nonfinite=0,exact=0;
                for(int i=0;i<count;i++)for(int c=0;c<4;c++){
                    int index=(i*vectors+v)*4+c;
                    double difference=fabs((double)a[index]-b[index]);
                    if(!std::isfinite(a[index])||!std::isfinite(b[index]))nonfinite++;
                    if(a[index]==b[index])exact++;
                    maxAbs=fmax(maxAbs,difference);
                    if(difference>1e-4+1e-5*fmax(fabs(a[index]),fabs(b[index])))failures++;
                }
                passed=passed&&!failures&&!nonfinite;
                [fields addObject:@{@"field":names[v],@"max_absolute_difference":@(maxAbs),@"outside_tolerance":@(failures),@"nonfinite":@(nonfinite),@"exact_components":@(exact),@"components":@(count*4)}];
            }
            report[stage]=fields;
        }
        report[@"fixtures_per_stage"]=@(count);report[@"seed"]=@71983;report[@"passed"]=@(passed);
        report[@"absolute_tolerance"]=@.0001;report[@"relative_tolerance"]=@.00001;
        report[@"windows_opened"]=@0;
        report[@"scope"]=@"Original Windows DXBC translated to Metal versus reconstructed HLSL compiled to DXBC and translated to Metal. No Windows GPU execution.";
        NSData* json=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:&error];
        if(![json writeToFile:@(argv[3]) atomically:YES])return 7;
        puts([[NSString alloc]initWithData:json encoding:NSUTF8StringEncoding].UTF8String);
        return passed?0:8;
    }
}
