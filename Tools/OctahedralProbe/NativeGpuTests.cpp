// Runs the exact sample .compute / .hlsl on D3D11 without an Editor license.
// This validates GPU math and texture layouts, not Unity camera rendering or its scheduler.
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "dxgi.lib")
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;
static void check(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D failure: " + std::to_string((unsigned)result)); }
static void require(bool condition, const std::string& text) { if (!condition) throw std::runtime_error(text); }
struct V3 { float x, y, z; };
struct Color { float r, g, b, a = 1; };
static V3 operator+(V3 a, V3 b) { return {a.x+b.x,a.y+b.y,a.z+b.z}; }
static V3 operator*(V3 a, float b) { return {a.x*b,a.y*b,a.z*b}; }
static V3 cross(V3 a, V3 b) { return {a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x}; }
static V3 normal(V3 a) { float r=1/std::sqrt(a.x*a.x+a.y*a.y+a.z*a.z); return a*r; }
static const V3 faceU[6]={{0,0,-1},{0,0,1},{1,0,0},{1,0,0},{1,0,0},{-1,0,0}};
static const V3 faceV[6]={{0,-1,0},{0,-1,0},{0,0,1},{0,0,-1},{0,-1,0},{0,-1,0}};
static Color gradient(V3 d) { return {d.x*.5f+.5f,d.y*.5f+.5f,d.z*.5f+.5f}; }
static float error(Color a, Color b) { return std::max({std::abs(a.r-b.r),std::abs(a.g-b.g),std::abs(a.b-b.b)}); }
static uint16_t half(float value)
{
    uint32_t bits; std::memcpy(&bits,&value,4);
    uint32_t sign=(bits>>16)&0x8000, mantissa=bits&0x7fffff;
    int exponent=int((bits>>23)&255)-127+15;
    if (exponent<=0) return uint16_t(sign); // Test radiance is normal or zero.
    if (exponent>=31) return uint16_t(sign|0x7c00);
    uint32_t rounded=(mantissa+0x1000)>>13;
    if (rounded==0x400) { rounded=0; ++exponent; }
    return uint16_t(sign|(uint32_t(exponent)<<10)|rounded);
}
static float unhalf(uint16_t h)
{
    uint32_t sign=(h&0x8000u)<<16, e=(h>>10)&31u, m=h&1023u, bits;
    if (e==0) {
        if (!m) bits=sign;
        else { int exp=-14; while (!(m&1024)) { m<<=1; --exp; } bits=sign|uint32_t(exp+127)<<23|(m&1023)<<13; }
    } else bits=sign|(e==31?255u:e+112u)<<23|m<<13;
    float value; std::memcpy(&value,&bits,4); return value;
}
struct Kernel
{
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3D11ShaderReflection> reflection;
    ComPtr<ID3D11Buffer> constants;
    std::vector<unsigned char> data;
};
struct Texture
{
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    std::vector<ComPtr<ID3D11UnorderedAccessView>> uavs;
    int size, mips, faces;
};
class GPU
{
public:
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11SamplerState> sampler;
    Kernel cube, face, oct, sample, importer, rawCube, rawFace, rawRestore;
    std::string adapter;

    explicit GPU(const wchar_t* path, const wchar_t* importPath, const wchar_t* rawPath)
    {
        D3D_FEATURE_LEVEL level;
        check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));
        require(level>=D3D_FEATURE_LEVEL_11_0,"D3D11 feature level 11 is required.");
        ComPtr<IDXGIDevice> dxgi; check(device.As(&dxgi));
        ComPtr<IDXGIAdapter> ad; check(dxgi->GetAdapter(&ad));
        DXGI_ADAPTER_DESC desc; check(ad->GetDesc(&desc));
        for (wchar_t c : std::wstring(desc.Description)) adapter.push_back(c<128?char(c):'?');
        D3D11_SAMPLER_DESC s={}; s.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        s.AddressU=s.AddressV=s.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; s.MaxLOD=D3D11_FLOAT32_MAX;
        check(device->CreateSamplerState(&s,&sampler));
        compile(cube,path,"FilterCube"); compile(face,path,"CopyFace"); compile(oct,path,"FilterOct"); compile(sample,path,"SampleAtlas");
        compile(importer,importPath,"ImportOctProbe");
        compile(rawCube,rawPath,"RawCubeToOct"); compile(rawFace,rawPath,"RawFaceToOct"); compile(rawRestore,rawPath,"RawOctToFace");
    }

    void compile(Kernel& k,const wchar_t* path,const char* entry)
    {
        ComPtr<ID3DBlob> bytecode, errors;
        HRESULT result=D3DCompileFromFile(path,nullptr,D3D_COMPILE_STANDARD_FILE_INCLUDE,entry,"cs_5_0",D3DCOMPILE_ENABLE_STRICTNESS,0,&bytecode,&errors);
        if (FAILED(result)) throw std::runtime_error(errors?static_cast<char*>(errors->GetBufferPointer()):"Shader compilation failed");
        check(device->CreateComputeShader(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),nullptr,&k.shader));
        check(D3DReflect(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),IID_PPV_ARGS(&k.reflection)));
        auto cb=k.reflection->GetConstantBufferByName("$Globals");
        D3D11_SHADER_BUFFER_DESC desc={}; check(cb->GetDesc(&desc));
        k.data.resize((desc.Size+15)&~15);
        D3D11_BUFFER_DESC bd={}; bd.ByteWidth=(UINT)k.data.size(); bd.Usage=D3D11_USAGE_DEFAULT; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        check(device->CreateBuffer(&bd,nullptr,&k.constants));
    }

    template<class T> void set(Kernel& k,const char* name,const T& value)
    {
        auto variable=k.reflection->GetConstantBufferByName("$Globals")->GetVariableByName(name);
        D3D11_SHADER_VARIABLE_DESC desc={}; check(variable->GetDesc(&desc));
        require(sizeof(T)<=desc.Size,"Constant size mismatch.");
        std::memcpy(k.data.data()+desc.StartOffset,&value,sizeof(T));
    }

    Texture make(int size,int mips,int faces=1,bool output=true)
    {
        Texture t; t.size=size; t.mips=mips; t.faces=faces;
        D3D11_TEXTURE2D_DESC d={}; d.Width=d.Height=size; d.MipLevels=mips; d.ArraySize=faces;
        d.Format=DXGI_FORMAT_R16G16B16A16_FLOAT; d.SampleDesc.Count=1; d.Usage=D3D11_USAGE_DEFAULT;
        d.BindFlags=D3D11_BIND_SHADER_RESOURCE|(output?D3D11_BIND_UNORDERED_ACCESS:0);
        if (faces==6) d.MiscFlags=D3D11_RESOURCE_MISC_TEXTURECUBE;
        check(device->CreateTexture2D(&d,nullptr,&t.texture));
        check(device->CreateShaderResourceView(t.texture.Get(),nullptr,&t.srv));
        if (output) for (int mip=0;mip<mips;++mip) {
            D3D11_UNORDERED_ACCESS_VIEW_DESC u={}; u.Format=d.Format; u.ViewDimension=D3D11_UAV_DIMENSION_TEXTURE2D; u.Texture2D.MipSlice=mip;
            ComPtr<ID3D11UnorderedAccessView> view; check(device->CreateUnorderedAccessView(t.texture.Get(),&u,&view)); t.uavs.push_back(view);
        }
        return t;
    }

    void uploadFace(Texture& t,int faceIndex,const std::function<Color(V3)>& evaluate)
    {
        std::vector<uint16_t> data(t.size*t.size*4);
        for (int y=0;y<t.size;++y) for (int x=0;x<t.size;++x) {
            V3 d=normal(cross(faceV[faceIndex],faceU[faceIndex])+faceU[faceIndex]*((x+.5f)/t.size*2-1)+faceV[faceIndex]*((y+.5f)/t.size*2-1));
            Color c=evaluate(d); int i=(y*t.size+x)*4;
            data[i]=half(c.r); data[i+1]=half(c.g); data[i+2]=half(c.b); data[i+3]=half(1);
        }
        context->UpdateSubresource(t.texture.Get(),D3D11CalcSubresource(0,t.faces==6?faceIndex:0,t.mips),nullptr,data.data(),t.size*8,0);
    }

    void bind(Kernel& k,const char* name,ID3D11ShaderResourceView* view)
    {
        D3D11_SHADER_INPUT_BIND_DESC desc={}; check(k.reflection->GetResourceBindingDescByName(name,&desc));
        context->CSSetShaderResources(desc.BindPoint,1,&view);
    }
    void bindUAV(Kernel& k,const char* name,ID3D11UnorderedAccessView* view)
    {
        D3D11_SHADER_INPUT_BIND_DESC desc={}; check(k.reflection->GetResourceBindingDescByName(name,&desc));
        context->CSSetUnorderedAccessViews(desc.BindPoint,1,&view,nullptr);
    }
    void dispatch(Kernel& k,int x,int y)
    {
        context->UpdateSubresource(k.constants.Get(),0,nullptr,k.data.data(),0,0);
        D3D11_SHADER_INPUT_BIND_DESC desc={}; check(k.reflection->GetResourceBindingDescByName("$Globals",&desc));
        ID3D11Buffer* cb=k.constants.Get(); context->CSSetConstantBuffers(desc.BindPoint,1,&cb);
        ID3D11SamplerState* ss=sampler.Get(); context->CSSetSamplers(0,1,&ss);
        context->CSSetShader(k.shader.Get(),nullptr,0); context->Dispatch(x,y,1);
        ID3D11ShaderResourceView* srvs[8]={}; context->CSSetShaderResources(0,8,srvs);
        ID3D11UnorderedAccessView* uavs[8]={}; context->CSSetUnorderedAccessViews(0,8,uavs,nullptr);
    }

    void filter(Texture& source,Texture& atlas,int slot,bool octSource,int samples=256)
    {
        auto& k=octSource?oct:cube;
        for (int mip=0;mip<=4;++mip) {
            int size=64>>mip;
            set(k,"_TileSize",size); set(k,"_RawSize",64); set(k,"_SampleCount",samples);
            std::array<int,2> origin={slot%(atlas.size/64)*size,slot/(atlas.size/64)*size}; set(k,"_Origin",origin);
            float x=mip/4.f, p=(1.7f-std::sqrt(2.89f-2.8f*x))/1.4f; set(k,"_Roughness",p*p);
            bind(k,octSource?"_OctTexture":"_CubeTexture",source.srv.Get()); bindUAV(k,"_Output",atlas.uavs[mip].Get());
            dispatch(k,(size+7)/8,(size+7)/8);
        }
    }
    void copyFace(Texture& source,Texture& raw,int index)
    {
        set(face,"_TileSize",64); set(face,"_Face",index);
        bind(face,"_FaceTexture",source.srv.Get()); bindUAV(face,"_Output",raw.uavs[0].Get()); dispatch(face,8,8);
    }

    void radiance(Kernel& k,Texture& source,Texture& destination,int index=0)
    {
        set(k,"_RadianceSize",destination.size);
        if (&k==&rawRestore) set(k,"_RadianceSourceSize",source.size);
        if (&k!=&rawCube) set(k,"_RadianceFace",index);
        bind(k,&k==&rawCube?"_RawCubeInput":"_RawInput",source.srv.Get());
        bindUAV(k,"_RawOutput",destination.uavs[0].Get());
        dispatch(k,(destination.size+7)/8,(destination.size+7)/8);
    }

    void importProbe(Texture& source, Texture& destination, int sourceSlot, int destinationSlot)
    {
        auto& k=importer;
        float scale=64.f/source.size;
        std::array<float,4> region={scale,scale,(sourceSlot%(source.size/64))*scale,(sourceSlot/(source.size/64))*scale};
        set(k,"_SourceRegion",region);set(k,"_SourceSize",float(source.size));set(k,"_SourceMaxMip",4.f);set(k,"_DestinationMaxMip",6.f);
        int padding=16;
        for(int mip=0;mip<=6;++mip){
            int size=256>>mip;
            if(mip>3)padding*=2;
            set(k,"_TileSize",size);set(k,"_BaseInterior",240.f);set(k,"_Padding",float(padding));set(k,"_Mip",float(mip));
            std::array<int,2> origin={destinationSlot%(destination.size/256)*size,destinationSlot/(destination.size/256)*size};set(k,"_Origin",origin);
            bind(k,"_Source",source.srv.Get());bindUAV(k,"_Output",destination.uavs[mip].Get());dispatch(k,(size+7)/8,(size+7)/8);
        }
    }

    double benchmark(const std::function<void()>& work, int iterations=20)
    {
        // Record first so CPU reflection/parameter-building work does not starve the timed GPU batch.
        ComPtr<ID3D11DeviceContext> immediate=context, deferred;
        check(device->CreateDeferredContext(0,&deferred));
        ComPtr<ID3D11CommandList> commands;
        context=deferred;
        try { work(); check(context->FinishCommandList(FALSE,&commands)); }
        catch (...) { context=immediate; throw; }
        context=immediate;
        D3D11_QUERY_DESC d={D3D11_QUERY_TIMESTAMP_DISJOINT,0};
        ComPtr<ID3D11Query> disjoint,begin,end;
        check(device->CreateQuery(&d,&disjoint)); d.Query=D3D11_QUERY_TIMESTAMP;
        check(device->CreateQuery(&d,&begin));check(device->CreateQuery(&d,&end));
        for(int i=0;i<3;++i)context->ExecuteCommandList(commands.Get(),FALSE);
        context->Begin(disjoint.Get());context->End(begin.Get());
        for(int i=0;i<iterations;++i)context->ExecuteCommandList(commands.Get(),FALSE);
        context->End(end.Get());context->End(disjoint.Get());context->Flush();
        auto wait=[&](ID3D11Query* query,void* data,UINT bytes){
            ULONGLONG deadline=GetTickCount64()+10000;
            for(;;){HRESULT result=context->GetData(query,data,bytes,0);check(result);if(result==S_OK)return;
                require(GetTickCount64()<deadline,"GPU timestamp readback timed out");Sleep(1);}
        };
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT frequency={};UINT64 a=0,b=0;
        wait(disjoint.Get(),&frequency,sizeof(frequency));wait(begin.Get(),&a,sizeof(a));wait(end.Get(),&b,sizeof(b));
        require(!frequency.Disjoint&&frequency.Frequency>0,"GPU timestamps were disjoint");
        return double(b-a)/frequency.Frequency*1000/iterations;
    }

    std::vector<Color> read(Texture& t,int mip)
    {
        D3D11_TEXTURE2D_DESC d={}; t.texture->GetDesc(&d);
        d.BindFlags=0; d.MiscFlags=0; d.Usage=D3D11_USAGE_STAGING; d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging; check(device->CreateTexture2D(&d,nullptr,&staging));
        context->CopyResource(staging.Get(),t.texture.Get());
        D3D11_MAPPED_SUBRESOURCE mapped={}; check(context->Map(staging.Get(),mip,D3D11_MAP_READ,0,&mapped));
        int size=t.size>>mip; std::vector<Color> pixels(size*size);
        for (int y=0;y<size;++y) {
            const auto* row=(const uint16_t*)((const unsigned char*)mapped.pData+y*mapped.RowPitch);
            for (int x=0;x<size;++x) pixels[y*size+x]={unhalf(row[x*4]),unhalf(row[x*4+1]),unhalf(row[x*4+2]),unhalf(row[x*4+3])};
        }
        context->Unmap(staging.Get(),mip); return pixels;
    }

    std::vector<Color> query(Texture& atlas,int slot,const std::vector<Color>& queries)
    {
        D3D11_BUFFER_DESC d={}; d.ByteWidth=UINT(queries.size()*16); d.Usage=D3D11_USAGE_DEFAULT;
        d.BindFlags=D3D11_BIND_SHADER_RESOURCE; d.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; d.StructureByteStride=16;
        D3D11_SUBRESOURCE_DATA initial={queries.data(),0,0}; ComPtr<ID3D11Buffer> input,output;
        check(device->CreateBuffer(&d,&initial,&input)); d.BindFlags=D3D11_BIND_UNORDERED_ACCESS; check(device->CreateBuffer(&d,nullptr,&output));
        ComPtr<ID3D11ShaderResourceView> srv; check(device->CreateShaderResourceView(input.Get(),nullptr,&srv));
        ComPtr<ID3D11UnorderedAccessView> uav; check(device->CreateUnorderedAccessView(output.Get(),nullptr,&uav));
        set(sample,"_QueryCount",(int)queries.size()); set(sample,"_MaxMip",4.f); set(sample,"_AtlasSize",float(atlas.size));
        float scale=64.f/atlas.size; std::array<float,4> region={scale,scale,(slot%(atlas.size/64))*scale,(slot/(atlas.size/64))*scale};
        set(sample,"_ProbeScaleOffset",region);
        bind(sample,"_AtlasTexture",atlas.srv.Get()); bind(sample,"_Queries",srv.Get()); bindUAV(sample,"_Samples",uav.Get());
        dispatch(sample,(UINT(queries.size())+63)/64,1);
        d.BindFlags=0; d.MiscFlags=0; d.Usage=D3D11_USAGE_STAGING; d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Buffer> staging; check(device->CreateBuffer(&d,nullptr,&staging)); context->CopyResource(staging.Get(),output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped={}; check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        std::vector<Color> result(queries.size()); std::memcpy(result.data(),mapped.pData,result.size()*16); context->Unmap(staging.Get(),0); return result;
    }
};

static V3 pixelDirection(int x,int y,int size=64)
{
    float u=(x-.5f)/(size-2), v=(y-.5f)/(size-2), a=u,b=v;
    if (u<0) { if(v<0){a=1+u;b=1+v;}else if(v<1){a=-u;b=1-v;}else{a=1+u;b=v-1;} }
    else if(u<1) { if(v<0){a=1-u;b=-v;}else if(v>=1){a=1-u;b=2-v;} }
    else { if(v<0){a=u-1;b=1+v;}else if(v<1){a=2-u;b=1-v;}else{a=u-1;b=v-1;} }
    a=a*2-1; b=b*2-1; V3 n={a,b,1-std::abs(a)-std::abs(b)}; float t=std::max(-n.z,0.f);
    n.x+=n.x>=0?-t:t; n.y+=n.y>=0?-t:t; return normal(n);
}
static float compare(const std::vector<Color>& a,const std::vector<Color>& b)
{
    require(a.size()==b.size(),"Size mismatch"); float maximum=0;
    for(size_t i=0;i<a.size();++i) maximum=std::max(maximum,error(a[i],b[i])); return maximum;
}
static void ppm(const fs::path& path,const std::vector<Color>& pixels,int size)
{
    std::ofstream output(path,std::ios::binary); output<<"P6\n"<<size<<" "<<size<<"\n255\n";
    for(int y=size-1;y>=0;--y) for(int x=0;x<size;++x) {
        auto p=pixels[y*size+x]; unsigned char bytes[3]={ (unsigned char)std::lround(std::clamp(p.r,0.f,1.f)*255), (unsigned char)std::lround(std::clamp(p.g,0.f,1.f)*255), (unsigned char)std::lround(std::clamp(p.b,0.f,1.f)*255) }; output.write((char*)bytes,3);
    }
}

static Color bilinear(const std::vector<Color>& image,int size,float u,float v)
{
    float x=u*size-.5f,y=v*size-.5f;int x0=(int)std::floor(x),y0=(int)std::floor(y);float tx=x-x0,ty=y-y0;
    auto at=[&](int a,int b){return image[std::clamp(b,0,size-1)*size+std::clamp(a,0,size-1)];};
    auto lerp=[](Color a,Color b,float t){return Color{a.r+(b.r-a.r)*t,a.g+(b.g-a.g)*t,a.b+(b.b-a.b)*t};};
    return lerp(lerp(at(x0,y0),at(x0+1,y0),tx),lerp(at(x0,y0+1),at(x0+1,y0+1),tx),ty);
}

static std::array<float,2> repeatUV(float u,float v)
{
    if(u<0)return v<0?std::array<float,2>{1+u,1+v}:(v<1?std::array<float,2>{-u,1-v}:std::array<float,2>{1+u,v-1});
    if(u<1)return v<0?std::array<float,2>{1-u,-v}:(v<1?std::array<float,2>{u,v}:std::array<float,2>{1-u,2-v});
    return v<0?std::array<float,2>{u-1,1+v}:(v<1?std::array<float,2>{2-u,1-v}:std::array<float,2>{u-1,v-1});
}

int wmain(int argc,wchar_t** argv)
{
    if(argc!=5){ std::cerr<<"Usage: NativeGpuTests shader.compute outputDirectory import.compute radiance.compute\n"; return 2; }
    fs::path output=argv[2]; fs::create_directories(output);
    std::ofstream report(output/"native-results.json");
    int passed=0,failed=0;
    try {
        GPU gpu(argv[1],argv[3],argv[4]); std::cout<<"GPU: "<<gpu.adapter<<"\n";
        report<<"{\n  \"backend\": \"D3D11 hardware, RGBAHalf, sample, production upload and raw radiance helpers; raw Core packing uses test adapter\",\n  \"gpu\": \""<<gpu.adapter<<"\",\n  \"cases\": [\n";
        auto test=[&](const char* name,const std::function<float()>& body) {
            if(passed+failed) report<<",\n";
            try { float metric=body(); ++passed; std::cout<<"PASS "<<name<<" (metric="<<metric<<")\n"; report<<"    {\"name\":\""<<name<<"\",\"passed\":true,\"metric\":"<<metric<<"}"; }
            catch(const std::exception& e){ ++failed; std::cout<<"FAIL "<<name<<": "<<e.what()<<"\n"; report<<"    {\"name\":\""<<name<<"\",\"passed\":false}"; }
        };
        auto cube=gpu.make(64,1,6,false), face=gpu.make(64,1,1,false), raw=gpu.make(64,1), atlas=gpu.make(128,8), reference=gpu.make(64,7), progressive=gpu.make(64,7);
        test("all four compute entry points compile",[]{return 0.f;});
        test("shared cube updates preserve independent probe tiles",[&]{
            for(int f=0;f<6;++f) gpu.uploadFace(cube,f,[](V3){return Color{2,0,0};}); gpu.filter(cube,atlas,0,false);
            for(int f=0;f<6;++f) gpu.uploadFace(cube,f,[](V3){return Color{0,0,1};}); gpu.filter(cube,atlas,1,false);
            auto p=gpu.read(atlas,0); float e=std::max(error(p[32*128+32],{2,0,0}),error(p[32*128+96],{0,0,1})); require(e<.003f,"Probe data overwritten"); return e;
        });
        test("constant HDR radiance is preserved at every GGX mip",[&]{
            for(int f=0;f<6;++f) gpu.uploadFace(cube,f,[](V3){return Color{.25f,1.5f,3};}); gpu.filter(cube,reference,0,false); float e=0;
            for(int mip=0;mip<=4;++mip) for(auto p:gpu.read(reference,mip)) e=std::max(e,error(p,{.25f,1.5f,3})); require(e<.005f,"Constant energy changed"); return e;
        });
        test("cube direction reprojection and border match analytic reference",[&]{
            for(int f=0;f<6;++f) gpu.uploadFace(cube,f,gradient); gpu.filter(cube,reference,0,false); auto p=gpu.read(reference,0); float e=0;
            for(int y=0;y<64;++y)for(int x=0;x<64;++x)e=std::max(e,error(p[y*64+x],gradient(pixelDirection(x,y))));
            ppm(output/"cube-direction.ppm",p,64); require(e<.02f,"Direction mapping wrong: "+std::to_string(e)); return e;
        });
        test("six individual faces reconstruct the same Oct image",[&]{
            for(int f:{5,0,3,1,4,2}){gpu.uploadFace(face,f,gradient);gpu.copyFace(face,raw,f);} gpu.filter(raw,progressive,0,true);
            auto p=gpu.read(progressive,0); ppm(output/"face-direction.ppm",p,64); float e=compare(gpu.read(reference,0),p); require(e<.025f,"Face reprojection error: "+std::to_string(e)); return e;
        });
        test("Oct-source GGX matches cube-source GGX across roughness mips",[&]{
            float e=0;for(int mip=1;mip<=4;++mip)e=std::max(e,compare(gpu.read(reference,mip),gpu.read(progressive,mip)));require(e<.035f,"GGX error: "+std::to_string(e));return e;
        });
        test("fractional LOD and Oct boundary do not bleed adjacent tiles",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3){return Color{1,0,0};});gpu.filter(cube,atlas,0,false);
            std::vector<Color> q;for(float lod:{-1.f,0.f,.3f,1.7f,3.5f,4.f,99.f})for(V3 d:std::vector<V3>{{0,0,-1},{.001f,1,-1},{-.001f,1,-1},{1,.001f,-1},{1,-.001f,-1}})q.push_back({d.x,d.y,d.z,lod});
            float e=0;for(auto p:gpu.query(atlas,0,q))e=std::max(e,error(p,{1,0,0}));require(e<.003f,"Cross-probe bleed: "+std::to_string(e));return e;
        });
        test("fractional LOD matches independent integer-LOD samples",[&]{
            float e=0;for(V3 d:std::vector<V3>{{0,0,1},{1,0,0},{0,0,-1}}){auto p=gpu.query(reference,0,{{d.x,d.y,d.z,1},{d.x,d.y,d.z,2},{d.x,d.y,d.z,1.3f}});Color expected={p[0].r*.7f+p[1].r*.3f,p[0].g*.7f+p[1].g*.3f,p[0].b*.7f+p[1].b*.3f};e=std::max(e,error(p[2],expected));}require(e<.003f,"LOD interpolation mismatch");return e;
        });
        test("roughness removes high-frequency directional variance",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3 d){float v=std::sin(d.x*37)*std::sin(d.y*37)*std::sin(d.z*37)>0?1.f:0.f;return Color{v,v,v};});gpu.filter(cube,reference,0,false);
            auto variance=[](const std::vector<Color>& p){double s=0,sq=0;for(auto c:p){s+=c.r;sq+=c.r*c.r;}return sq/p.size()-std::pow(s/p.size(),2);};
            auto a=gpu.read(reference,0),b=gpu.read(reference,3);double smooth=variance(a),rough=variance(b);ppm(output/"roughness-mip0.ppm",a,64);ppm(output/"roughness-mip3.ppm",b,8);
            require(smooth>.05&&rough<smooth*.3,"GGX variance did not decrease");return float(rough/smooth);
        });
        test("all partial face updates preserve pixels owned by other faces",[&]{
            for(int f:{0,1,2,3,4,5}){gpu.uploadFace(face,f,[](V3){return Color{.25f,.5f,.75f};});gpu.copyFace(face,raw,f);}auto before=gpu.read(raw,0);
            gpu.uploadFace(face,0,[](V3){return Color{1,0,0};});gpu.copyFace(face,raw,0);auto after=gpu.read(raw,0);float e=0;int changed=0;
            for(int y=0;y<64;++y)for(int x=0;x<64;++x){V3 d=pixelDirection(x,y);bool owns=d.x>=0&&std::abs(d.x)>=std::abs(d.y)&&std::abs(d.x)>=std::abs(d.z);if(!owns)e=std::max(e,error(before[y*64+x],after[y*64+x]));else ++changed;}
            require(e<.001f&&changed>100,"CopyFace overwrote another region");return e;
        });
        auto imported=gpu.make(512,10);
        test("production Oct import helper compiles",[]{return 0.f;});
        test("production import consumes a shared source before it is reused",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3){return Color{2,0,0};});gpu.filter(cube,reference,0,false);gpu.importProbe(reference,imported,0,0);
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3){return Color{0,1,0};});gpu.filter(cube,reference,0,false);gpu.importProbe(reference,imported,0,1);
            float e=0;for(int mip=0;mip<=6;++mip){auto p=gpu.read(imported,mip);int width=512>>mip,tile=256>>mip;
                e=std::max({e,error(p[(tile/2)*width+tile/2],{2,0,0}),error(p[(tile/2)*width+tile+tile/2],{0,1,0})});}
            require(e<.005f,"Shared import source overwrote another probe");return e;
        });
        test("production import converts border layout and roughness mip range",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,gradient);gpu.filter(cube,reference,0,false);gpu.importProbe(reference,imported,0,0);
            std::vector<std::vector<Color>> source;for(int mip=0;mip<=4;++mip)source.push_back(gpu.read(reference,mip));
            float e=0;int padding=16;
            for(int mip=0;mip<=6;++mip){if(mip>3)padding*=2;int tile=256>>mip,atlasSize=512>>mip;auto p=gpu.read(imported,mip);
                float lod=mip/6.f*4;int lo=(int)std::floor(lod),hi=std::min(lo+1,4);float fraction=lod-lo;
                for(int y=0;y<tile;++y)for(int x=0;x<tile;++x){auto uv=repeatUV(((x+.5f)/tile*(240+padding)-padding*.5f)/240,((y+.5f)/tile*(240+padding)-padding*.5f)/240);
                    auto sample=[&](int level){float size=float(64>>level);return bilinear(source[level],int(size),(uv[0]*(size-2)+1)/size,(uv[1]*(size-2)+1)/size);};
                    Color a=sample(lo),b=sample(hi),expected={a.r+(b.r-a.r)*fraction,a.g+(b.g-a.g)*fraction,a.b+(b.b-a.b)*fraction};e=std::max(e,error(p[y*atlasSize+x],expected));}
            }require(e<.004f,"Production border/LOD import mismatch: "+std::to_string(e));return e;
        });
        test("production import selects the requested source tile",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3){return Color{0,0,2};});gpu.filter(cube,atlas,1,false);gpu.importProbe(atlas,imported,1,0);
            float e=0;for(int mip=0;mip<=6;++mip){auto p=gpu.read(imported,mip);int tile=256>>mip,width=512>>mip;
                for(int y=0;y<tile;++y)for(int x=0;x<tile;++x)e=std::max(e,error(p[y*width+x],{0,0,2}));}
            require(e<.005f,"Wrong source atlas region");return e;
        });
        auto rawSource=gpu.make(128,1), rawProgressive=gpu.make(128,1), restoredFace=gpu.make(64,1);
        test("raw Oct production conversion kernels compile",[]{return 0.f;});
        test("raw Cube to Oct direction field and one-texel border",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,gradient);
            gpu.radiance(gpu.rawCube,cube,rawSource);auto p=gpu.read(rawSource,0);float e=0;
            for(int y=0;y<128;++y)for(int x=0;x<128;++x)e=std::max(e,error(p[y*128+x],gradient(pixelDirection(x,y,128))));
            require(e<.02f,"Raw Oct direction/border error: "+std::to_string(e));return e;
        });
        test("raw single-face reconstruction agrees with complete Cube conversion",[&]{
            for(int f:{4,0,5,1,3,2}){gpu.uploadFace(face,f,gradient);gpu.radiance(gpu.rawFace,face,rawProgressive,f);}
            float e=compare(gpu.read(rawSource,0),gpu.read(rawProgressive,0));require(e<.01f,"Raw face reprojection mismatch");return e;
        });
        test("raw Oct restores all six Cube faces including asymmetric directions",[&]{
            float e=0;for(int f=0;f<6;++f){gpu.radiance(gpu.rawRestore,rawSource,restoredFace,f);auto p=gpu.read(restoredFace,0);
                for(int y=0;y<64;++y)for(int x=0;x<64;++x){V3 d=normal(cross(faceV[f],faceU[f])+faceU[f]*((x+.5f)/64*2-1)+faceV[f]*((y+.5f)/64*2-1));e=std::max(e,error(p[y*64+x],gradient(d)));}}
            require(e<.025f,"Raw restore direction error: "+std::to_string(e));return e;
        });
        test("raw radiance Cube roundtrip preserves constant HDR on every face",[&]{
            for(int f=0;f<6;++f)gpu.uploadFace(cube,f,[](V3){return Color{.25f,1.5f,3};});gpu.radiance(gpu.rawCube,cube,rawSource);
            float e=0;for(int f=0;f<6;++f){gpu.radiance(gpu.rawRestore,rawSource,restoredFace,f);for(auto p:gpu.read(restoredFace,0))e=std::max(e,error(p,{.25f,1.5f,3}));}
            require(e<.005f,"Raw HDR radiance changed");return e;
        });
        test("raw face updates preserve all other directional regions",[&]{
            auto before=gpu.read(rawProgressive,0);gpu.uploadFace(face,0,[](V3){return Color{3,0,0};});gpu.radiance(gpu.rawFace,face,rawProgressive,0);auto after=gpu.read(rawProgressive,0);float e=0;
            for(int y=0;y<128;++y)for(int x=0;x<128;++x){V3 d=pixelDirection(x,y,128);bool owns=d.x>=0&&std::abs(d.x)>=std::abs(d.y)&&std::abs(d.x)>=std::abs(d.z);// CPU/GPU rounding may assign exact major-axis ties to adjacent faces.
                float boundary=std::min(std::abs(std::abs(d.x)-std::abs(d.y)),std::abs(std::abs(d.x)-std::abs(d.z)));
                if(!owns && boundary>1e-5f)e=std::max(e,error(before[y*128+x],after[y*128+x]));}
            require(e<.001f,"Raw update overwrote another face");return e;
        });
        // Microbenchmark only: source preparation and all Camera/Unity/Lit work are excluded.
        try {
            std::vector<Texture> faceFixtures;
            for(int f=0;f<6;++f){faceFixtures.push_back(gpu.make(64,1,1,false));gpu.uploadFace(faceFixtures.back(),f,gradient);gpu.uploadFace(cube,f,gradient);}
            double cubeMs=gpu.benchmark([&]{gpu.filter(cube,reference,0,false);});
            double faceMs=gpu.benchmark([&]{for(int f=0;f<6;++f)gpu.copyFace(faceFixtures[f],raw,f);gpu.filter(raw,progressive,0,true);});
            std::ofstream performance(output/"microbenchmark.json");
            performance<<"{\n  \"scope\":\"GPU timestamp microbenchmark; excludes camera capture, uploads, Unity scheduling and Lit\",\n"
                <<"  \"gpu\":\""<<gpu.adapter<<"\",\n  \"faceSize\":64,\n  \"tileSize\":64,\n  \"ggxSamples\":256,\n  \"roughnessLevels\":5,\n  \"iterations\":20,\n"
                <<"  \"sharedCubePrefilterMs\":"<<cubeMs<<",\n  \"sixFacesReprojectAndOctPrefilterMs\":"<<faceMs<<"\n}\n";
            std::cout<<"GPU microbenchmark (capture excluded): cube="<<cubeMs<<" ms, six faces + Oct="<<faceMs<<" ms\n";
        } catch(const std::exception& e) {
            // Optional timestamps (e.g. a disjoint GPU clock) must not turn correctness passes into failures.
            std::cout<<"Microbenchmark unavailable: "<<e.what()<<"\n";
            std::ofstream(output/"microbenchmark.json")<<"{\"available\":false}\n";
        }
        report<<"\n  ],\n  \"passed\": "<<passed<<",\n  \"failed\": "<<failed<<"\n}\n";
        std::cout<<passed<<" passed, "<<failed<<" failed\n";return failed?1:0;
    } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 2;}
}
