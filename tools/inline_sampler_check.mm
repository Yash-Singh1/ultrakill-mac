// Compute-only checks. No windows, input, application lifecycle, or audio.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <algorithm>
#include <cmath>
#include <vector>
#include <cstdio>

int main(int argc,const char** argv) {
 @autoreleasepool {
  if(argc!=3)return 2;
  NSError* error=nil;auto device=MTLCreateSystemDefaultDevice();
  auto options=[MTLCompileOptions new];options.mathMode=MTLMathModeSafe;
  auto source=[NSString stringWithContentsOfFile:@(argv[1]) encoding:NSUTF8StringEncoding error:&error];
  auto library=[device newLibraryWithSource:source options:options error:&error];
  if(!library){fprintf(stderr,"%s\n",error.description.UTF8String);return 3;}
  auto queue=[device newCommandQueue];const int count=4096,width=32;
  std::vector<std::vector<unsigned char>> mips;
  auto desc=[MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:width height:width mipmapped:YES];desc.usage=MTLTextureUsageShaderRead;
  auto texture=[device newTextureWithDescriptor:desc];
  for(int level=0,w=32;w>0;level++,w/=2){
   std::vector<unsigned char> texels(w*w*4);
   for(int y=0;y<w;y++)for(int x=0;x<w;x++) {
    int p=(y*w+x)*4;texels[p]=(x*7+level*31)%256;texels[p+1]=(y*7+level*47)%256;texels[p+2]=((x^y)*7+level*23)%256;texels[p+3]=255;
   }
   [texture replaceRegion:MTLRegionMake2D(0,0,w,w) mipmapLevel:level withBytes:texels.data() bytesPerRow:w*4];mips.push_back(texels);
  }
  std::vector<float> uv(count*4);
  for(int i=0;i<count;i++){
   uv[i*4]=(i%32+.3f)/32+(i%9-4);uv[i*4+1]=((i/32)%32+.7f)/32+(i%11-5);
   uv[i*4+2]=i%3==0?0:i%3==1?1.2f:2.7f;
  }
  auto fixtures=[device newBufferWithBytes:uv.data() length:uv.size()*4 options:MTLResourceStorageModeShared];
  auto results=[device newBufferWithLength:count*16 options:MTLResourceStorageModeShared];
  auto sampler=[&](bool repeat,bool linear){
   auto sd=[MTLSamplerDescriptor new];sd.minFilter=sd.magFilter=linear?MTLSamplerMinMagFilterLinear:MTLSamplerMinMagFilterNearest;
   sd.mipFilter=MTLSamplerMipFilterNearest;sd.sAddressMode=sd.tAddressMode=repeat?MTLSamplerAddressModeRepeat:MTLSamplerAddressModeClampToEdge;
   return [device newSamplerStateWithDescriptor:sd];
  };
  auto pointClamp=sampler(false,false),pointRepeat=sampler(true,false),wrong=sampler(false,true);
  std::vector<float> globals(40,0);auto cb=[device newBufferWithLength:160 options:MTLResourceStorageModeShared];
  auto execute=[&](NSString* name,id<MTLSamplerState> bound0,id<MTLSamplerState> bound1){
   auto pipeline=[device newComputePipelineStateWithFunction:[library newFunctionWithName:name] error:&error];
   if(!pipeline){fprintf(stderr,"%s\n",error.description.UTF8String);return false;}
   auto command=[queue commandBuffer];auto encoder=[command computeCommandEncoder];
   [encoder setComputePipelineState:pipeline];[encoder setBuffer:fixtures offset:0 atIndex:0];[encoder setBuffer:results offset:0 atIndex:1];[encoder setBuffer:cb offset:0 atIndex:2];
   [encoder setTexture:texture atIndex:0];[encoder setSamplerState:bound0 atIndex:0];[encoder setSamplerState:bound1 atIndex:1];
   [encoder dispatchThreads:MTLSizeMake(count,1,1) threadsPerThreadgroup:MTLSizeMake(64,1,1)];[encoder endEncoding];[command commit];[command waitUntilCompleted];
   if(command.error){fprintf(stderr,"%s\n",command.error.description.UTF8String);return false;}return true;
  };
  auto cpu=[&](int state,int i,int c){
   int level=std::min(5,(int)floor(uv[i*4+2]+.5f)),w=32>>level;bool repeat=state!=84;
   auto fetch=[&](int x,int y){
    auto address=[&](int a){return repeat?(a%w+w)%w:std::clamp(a,0,w-1);};
    return mips[level][(address(y)*w+address(x))*4+c]/255.f;
   };
   float x=uv[i*4]*w,y=uv[i*4+1]*w;
   if(state!=1)return fetch(floor(x),floor(y));
   x-=.5f;y-=.5f;int ix=floor(x),iy=floor(y);float fx=x-ix,fy=y-iy;
   return (fetch(ix,iy)*(1-fx)+fetch(ix+1,iy)*fx)*(1-fy)+(fetch(ix,iy+1)*(1-fx)+fetch(ix+1,iy+1)*fx)*fy;
  };
  NSMutableDictionary* report=[NSMutableDictionary dictionary];bool passed=true;
  for(int state: {0,1,84}){
   auto name=[NSString stringWithFormat:@"state_%d",state];if(!execute(name,wrong,wrong))return 4;
   float* output=(float*)results.contents;int mismatches=0;double maximum=0;
   for(int i=0;i<count;i++)for(int c=0;c<4;c++){
    // UNORM hardware interpolation can quantize the bilinear weights. Allow
    // one 8-bit channel step for CPU comparison; also compare with the exact
    // hardware-filtered authored sampler below at a much tighter tolerance.
    double delta=fabs(output[i*4+c]-cpu(state,i,c));maximum=fmax(maximum,delta);if(!std::isfinite(output[i*4+c])||delta>(state==1?1.0/255:0.00001))mismatches++;
   }
   std::vector<float> fixed(output,output+count*4);
   auto referenceName=[NSString stringWithFormat:@"reference_%d",state];
   if(!execute(referenceName,sampler(state!=84,state==1),wrong))return 4;
   output=(float*)results.contents;int nativeMismatches=0;
   for(int j=0;j<count*4;j++)if(fabs(output[j]-fixed[j])>0.00001)nativeMismatches++;
   if(!execute(referenceName,wrong,wrong))return 4;
   output=(float*)results.contents;int wrongMismatches=0;
   for(int j=0;j<count*4;j++)if(fabs(output[j]-fixed[j])>0.00001)wrongMismatches++;
   report[name]=@{@"values":@(count*4),@"cpu_mismatches":@(mismatches),@"max_cpu_difference":@(maximum),@"authored_sampler_mismatches":@(nativeMismatches),@"wrong_sampler_control_mismatches":@(wrongMismatches)};
   passed&=mismatches==0&&nativeMismatches==0&&wrongMismatches>0;
  }
  // Real recursive portal program. Test both its fixed recursive sampler and
  // its texture-dependent main sampler, which must retain the bound state.
  for(int branch=0;branch<2;branch++){
   std::fill(globals.begin(),globals.end(),0);globals[29]=branch;memcpy(cb.contents,globals.data(),160);
   for(int i=0;i<count;i++)uv[i*4+2]=0;memcpy(fixtures.contents,uv.data(),uv.size()*4);
   if(!execute(@"portal_before",pointClamp,pointRepeat))return 5;
   std::vector<float> reference((float*)results.contents,(float*)results.contents+count*4);
   if(!execute(@"portal_after",pointClamp,wrong))return 5;
   float* output=(float*)results.contents;int mismatches=0,cpuMismatches=0;
   for(int j=0;j<count*4;j++)if(fabs(output[j]-reference[j])>0.00001)mismatches++;
   for(int i=0;i<count;i++)for(int c=0;c<4;c++)if(fabs(output[i*4+c]-cpu(branch==0?0:84,i,c))>0.00001)cpuMismatches++;
   report[[NSString stringWithFormat:@"portal_%d",branch]]=@{@"mismatches":@(mismatches),@"cpu_mismatches":@(cpuMismatches)};passed&=mismatches==0&&cpuMismatches==0;
  }
  // Real portal-composite shader, with identity projection and four corners.
  std::fill(globals.begin(),globals.end(),0);
  globals[8]=-1;globals[9]=-1;globals[12]=1;globals[13]=-1;
  globals[16]=1;globals[17]=1;globals[20]=-1;globals[21]=1;
  globals[24]=globals[29]=globals[34]=globals[39]=1;memcpy(cb.contents,globals.data(),160);
  for(int i=0;i<count;i++){uv[i*4]=(i%32+.3f)/32;uv[i*4+1]=((i/32)%32+.7f)/32;uv[i*4+2]=0;}
  memcpy(fixtures.contents,uv.data(),uv.size()*4);
  if(!execute(@"composite_before",pointClamp,pointRepeat))return 6;
  std::vector<float> reference((float*)results.contents,(float*)results.contents+count*4);
  if(!execute(@"composite_after",wrong,wrong))return 6;
  float* output=(float*)results.contents;int mismatches=0,cpuMismatches=0;
  for(int j=0;j<count*4;j++)if(fabs(output[j]-reference[j])>0.00001)mismatches++;
  for(int i=0;i<count;i++)for(int c=0;c<4;c++)if(fabs(output[i*4+c]-cpu(84,i,c))>0.00001)cpuMismatches++;
  report[@"composite"]=@{@"mismatches":@(mismatches),@"cpu_mismatches":@(cpuMismatches)};passed&=mismatches==0&&cpuMismatches==0;
  report[@"passed"]=@(passed);report[@"windows_opened"]=@0;
  report[@"scope"]=@"CPU sampling reference at three mip levels; real portal programs compared with their decoded authored sampler bindings. No Windows GPU execution or full scene parity.";
  auto json=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:&error];[json writeToFile:@(argv[2]) atomically:YES];puts([[NSString alloc]initWithData:json encoding:NSUTF8StringEncoding].UTF8String);
  return passed?0:7;
 }
}
