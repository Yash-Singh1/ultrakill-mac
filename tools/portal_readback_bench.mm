#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <chrono>
#include <cmath>
#include <cstdio>
using Clock=std::chrono::steady_clock;
static double ms(Clock::time_point t){return std::chrono::duration<double,std::milli>(Clock::now()-t).count();}
int main(){@autoreleasepool{
 id<MTLDevice> device=MTLCreateSystemDefaultDevice();
 if(!device)return 2;
 NSString *source=@"#include <metal_stdlib>\nusing namespace metal; kernel void work(device float* data [[buffer(0)]],constant uint& loops [[buffer(1)]],uint i [[thread_position_in_grid]]) { float x=float(i%31)*0.01f; for(uint k=0;k<loops;k++)x=sin(x+0.2f)*0.7f+0.03f; data[i]=x; }";
 NSError* error=nil;
 id<MTLLibrary> library=[device newLibraryWithSource:source options:nil error:&error];
 if(!library){fprintf(stderr,"%s\n",error.localizedDescription.UTF8String);return 3;}
 id<MTLComputePipelineState> pipeline=[device newComputePipelineStateWithFunction:[library newFunctionWithName:@"work"] error:&error];
 if(!pipeline)return 4;
 id<MTLCommandQueue> queue=[device newCommandQueue];
 id<MTLBuffer> buffer=[device newBufferWithLength:16384*sizeof(float) options:MTLResourceStorageModeShared];
 double blocking=0,nonblocking=0;int pending=0;bool finite=true;unsigned loops=2048;
 for(int trial=0;trial<12;trial++){
  id<MTLCommandBuffer> command=[queue commandBuffer];id<MTLComputeCommandEncoder> encoder=[command computeCommandEncoder];
  [encoder setComputePipelineState:pipeline];[encoder setBuffer:buffer offset:0 atIndex:0];[encoder setBytes:&loops length:sizeof(loops) atIndex:1];
  for(int work=0;work<24;work++)[encoder dispatchThreads:MTLSizeMake(16384,1,1) threadsPerThreadgroup:MTLSizeMake(pipeline.threadExecutionWidth,1,1)];[encoder endEncoding];[command commit];
  auto start=Clock::now();
  if(trial%2==0){[command waitUntilCompleted];blocking+=ms(start);}
  else {bool ready=command.status==MTLCommandBufferStatusCompleted;pending+=!ready;nonblocking+=ms(start);[command waitUntilCompleted];}
  if(command.status!=MTLCommandBufferStatusCompleted)return 6;
  finite=finite&&std::isfinite(((float*)buffer.contents)[0]);
 }
 printf("{\"device\":\"%s\",\"trials_per_mode\":6,\"blocking_decision_ms\":%.6f,\"nonblocking_decision_ms\":%.6f,\"pending_requests\":%d,\"output_finite\":%s,\"scope\":\"Offscreen Metal GPU queue test, not a gameplay FPS benchmark\"}\n",device.name.UTF8String,blocking/6,nonblocking/6,pending,finite?"true":"false");
 return finite?0:7;
}}
