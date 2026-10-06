// Compile and link the real vertex/fragment entry points without creating a window.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <cstdio>

int main(int argc,const char**argv){
    @autoreleasepool{
        if(argc!=4)return 2;
        NSError* error=nil;
        id<MTLDevice> device=MTLCreateSystemDefaultDevice();
        MTLCompileOptions* options=[MTLCompileOptions new];options.mathMode=MTLMathModeSafe;
        id<MTLLibrary> libraries[2];
        for(int i=0;i<2;i++){
            NSString* source=[NSString stringWithContentsOfFile:@(argv[i+1]) encoding:NSUTF8StringEncoding error:&error];
            libraries[i]=[device newLibraryWithSource:source options:options error:&error];
            if(!libraries[i]){fprintf(stderr,"%s\n",error.description.UTF8String);return 3;}
        }
        uint32_t remap=0x10;
        MTLFunctionConstantValues* constants=[MTLFunctionConstantValues new];
        [constants setConstantValue:&remap type:MTLDataTypeUInt atIndex:1];
        MTLRenderPipelineDescriptor* desc=[MTLRenderPipelineDescriptor new];
        desc.vertexFunction=[libraries[0] newFunctionWithName:@"xlatMtlMain"];
        desc.fragmentFunction=[libraries[1] newFunctionWithName:@"xlatMtlMain" constantValues:constants error:&error];
        desc.vertexDescriptor=[MTLVertexDescriptor vertexDescriptor];
        const MTLVertexFormat formats[]={MTLVertexFormatFloat4,MTLVertexFormatFloat4,MTLVertexFormatFloat2,MTLVertexFormatFloat3};
        const NSUInteger offsets[]={0,16,32,40};
        for(int i=0;i<4;i++){
            desc.vertexDescriptor.attributes[i].format=formats[i];
            desc.vertexDescriptor.attributes[i].offset=offsets[i];
            desc.vertexDescriptor.attributes[i].bufferIndex=7;
        }
        desc.vertexDescriptor.layouts[7].stride=64;
        for(int i=0;i<2;i++)desc.colorAttachments[i].pixelFormat=MTLPixelFormatRGBA8Unorm;
        MTLRenderPipelineReflection* reflection=nil;
        id<MTLRenderPipelineState> pipeline=[device newRenderPipelineStateWithDescriptor:desc options:MTLPipelineOptionArgumentInfo|MTLPipelineOptionBufferTypeInfo reflection:&reflection error:&error];
        if(!pipeline){fprintf(stderr,"%s\n",error.description.UTF8String);return 4;}
        NSMutableDictionary* report=[NSMutableDictionary dictionary];
        for(int stage=0;stage<2;stage++){
            NSMutableArray* buffers=[NSMutableArray array];
            NSArray* arguments=stage?reflection.fragmentArguments:reflection.vertexArguments;
            for(MTLArgument* argument in arguments){
                if(argument.type==MTLArgumentTypeBuffer&&argument.active)
                    [buffers addObject:@{@"name":argument.name,@"slot":@(argument.index),@"bytes":@(argument.bufferDataSize)}];
            }
            report[stage?@"fragment_buffers":@"vertex_buffers"]=buffers;
        }
        report[@"passed"]=@YES;report[@"windows_opened"]=@0;
        NSData* json=[NSJSONSerialization dataWithJSONObject:report options:NSJSONWritingPrettyPrinted error:&error];
        if(![json writeToFile:@(argv[3]) atomically:YES])return 5;
        puts([[NSString alloc]initWithData:json encoding:NSUTF8StringEncoding].UTF8String);
    }
    return 0;
}
