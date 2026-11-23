// Offline D3D11 runner for TestLegacyVhsCompatibility; built with the installed Windows SDK.
#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <fstream>
#include <vector>
#include <iostream>
#include <string>
#include <algorithm>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
void check(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("HRESULT " + std::to_string((unsigned)hr)); }
template<class T> T read(std::ifstream& f) { T v; if (!f.read((char*)&v,sizeof(v))) throw std::runtime_error("Truncated input"); return v; }
ComPtr<ID3DBlob> compile(const std::string& text,const char* entry,const char* profile) {
    ComPtr<ID3DBlob> code,errors;
    HRESULT hr=D3DCompile(text.data(),text.size(),nullptr,nullptr,nullptr,entry,profile,D3DCOMPILE_ENABLE_STRICTNESS|D3DCOMPILE_IEEE_STRICTNESS,0,&code,&errors);
    if (FAILED(hr)) { if(errors) std::cerr<<(char*)errors->GetBufferPointer(); check(hr); } return code;
}
int main(int argc,char** argv) { try {
    if(argc!=4) throw std::runtime_error("Expected shader,input,output paths");
    std::ifstream sf(argv[1],std::ios::binary); std::string shader((std::istreambuf_iterator<char>(sf)),{});
    ComPtr<ID3DBlob> psCode;
    if(shader.size()>4 && shader.substr(0,4)=="DXBC") {
        check(D3DCreateBlob(shader.size(),&psCode)); memcpy(psCode->GetBufferPointer(),shader.data(),shader.size());
        ComPtr<ID3DBlob> assembly; check(D3DDisassemble(shader.data(),shader.size(),0,nullptr,&assembly));
        std::ofstream listing(std::string(argv[1])+".asm"); listing.write((char*)assembly->GetBufferPointer(),assembly->GetBufferSize()-1);
        std::cout<<"Original DXBC bytecode\n";
    } else psCode=compile(shader,"main","ps_5_0");
    // Keep TEXCOORD0 in output register 0, matching the original PS input signature.
    auto vsCode=compile("cbuffer M:register(b0){float tracking,seconds,nativeCoordinates,diagnostic;} struct V{float2 uv:TEXCOORD0;float4 p:SV_POSITION;}; V main(uint id:SV_VertexID){V o; float2 p=float2((id<<1)&2,id&2);o.p=float4(p*2-1,0,1);o.uv=float2(p.x,nativeCoordinates!=0?1-p.y:p.y);return o;}","main","vs_5_0");
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> ctx; D3D_FEATURE_LEVEL level;
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&ctx);
    if(FAILED(hr)) { check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,&level,&ctx)); std::cout<<"D3D11 WARP\n"; }
    else std::cout<<"D3D11 hardware feature level "<<std::hex<<level<<std::dec<<"\n";
    ComPtr<IDXGIDevice> dxgiDevice; check(device.As(&dxgiDevice));
    ComPtr<IDXGIAdapter> adapter; check(dxgiDevice->GetAdapter(&adapter));
    DXGI_ADAPTER_DESC adapterDesc; check(adapter->GetDesc(&adapterDesc));
    char adapterName[512]; WideCharToMultiByte(CP_UTF8,0,adapterDesc.Description,-1,adapterName,sizeof(adapterName),nullptr,nullptr);
    std::cout<<adapterName<<" vendor="<<std::hex<<adapterDesc.VendorId<<" device="<<adapterDesc.DeviceId<<std::dec<<"\n";
    ComPtr<ID3D11VertexShader> vs; ComPtr<ID3D11PixelShader> ps;
    check(device->CreateVertexShader(vsCode->GetBufferPointer(),vsCode->GetBufferSize(),nullptr,&vs));
    check(device->CreatePixelShader(psCode->GetBufferPointer(),psCode->GetBufferSize(),nullptr,&ps));
    ctx->VSSetShader(vs.Get(),nullptr,0); ctx->PSSetShader(ps.Get(),nullptr,0);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    D3D11_RASTERIZER_DESC rd={}; rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;
    ComPtr<ID3D11RasterizerState> raster; check(device->CreateRasterizerState(&rd,&raster));ctx->RSSetState(raster.Get());
    std::ifstream input(argv[2],std::ios::binary); std::ofstream output(argv[3],std::ios::binary);
    ComPtr<ID3D11ShaderResourceView> views[2][3];
    for(int tex=0;tex<3;tex++) {
        int w=read<int>(input),h=read<int>(input),levels=read<int>(input);
        std::vector<std::vector<unsigned char>> data(levels);
        for(int mip=0;mip<levels;mip++) { int mw=std::max(1,w>>mip),mh=std::max(1,h>>mip);data[mip].resize(mw*mh*4);input.read((char*)data[mip].data(),data[mip].size()); }
        for(int orientation=0;orientation<2;orientation++) {
            std::vector<D3D11_SUBRESOURCE_DATA> init(levels);
            if(orientation) for(int mip=0;mip<levels;mip++) {
                int stride=std::max(1,w>>mip)*4,mh=std::max(1,h>>mip);
                for(int y=0;y<mh/2;y++) for(int x=0;x<stride;x++) std::swap(data[mip][y*stride+x],data[mip][(mh-1-y)*stride+x]);
            }
            for(int mip=0;mip<levels;mip++) {init[mip].pSysMem=data[mip].data();init[mip].SysMemPitch=std::max(1,w>>mip)*4;}
            D3D11_TEXTURE2D_DESC td={};td.Width=w;td.Height=h;td.MipLevels=levels;td.ArraySize=1;td.Format=DXGI_FORMAT_R8G8B8A8_UNORM;td.SampleDesc.Count=1;td.Usage=D3D11_USAGE_IMMUTABLE;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            ComPtr<ID3D11Texture2D> t; check(device->CreateTexture2D(&td,init.data(),&t));check(device->CreateShaderResourceView(t.Get(),nullptr,&views[orientation][tex]));
        }
    }
    D3D11_SAMPLER_DESC sd={};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> srcSampler,atlasSampler;
    check(device->CreateSamplerState(&sd,&srcSampler));sd.Filter=D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;check(device->CreateSamplerState(&sd,&atlasSampler));
    ID3D11SamplerState* samplers[]={srcSampler.Get(),atlasSampler.Get()};ctx->PSSetSamplers(0,2,samplers);
    // Original DXBC reads TRACKING/CONTRAST from b0.c2 and Unity _Time from b1.c0.
    // The reconstructed shader and fullscreen VS read the diagnostic controls at b0.c0.
    D3D11_BUFFER_DESC bd={};bd.ByteWidth=48;bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    ComPtr<ID3D11Buffer> cb,timeBuffer;check(device->CreateBuffer(&bd,nullptr,&cb));bd.ByteWidth=16;check(device->CreateBuffer(&bd,nullptr,&timeBuffer));
    ID3D11Buffer* cbs[]={cb.Get(),timeBuffer.Get()};ctx->PSSetConstantBuffers(0,2,cbs);ctx->VSSetConstantBuffers(0,1,cbs);
    const int width=382,height=576;D3D11_VIEWPORT vp={0,0,(float)width,(float)height,0,1};ctx->RSSetViewports(1,&vp);
    ComPtr<ID3D11Texture2D> target[2],staging[2];ComPtr<ID3D11RenderTargetView> rt[2];
    for(int kind=0;kind<2;kind++) {D3D11_TEXTURE2D_DESC td={};td.Width=width;td.Height=height;td.MipLevels=td.ArraySize=td.SampleDesc.Count=1;td.Format=kind?DXGI_FORMAT_R8G8B8A8_UNORM:DXGI_FORMAT_R32G32B32A32_FLOAT;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_RENDER_TARGET;check(device->CreateTexture2D(&td,nullptr,&target[kind]));check(device->CreateRenderTargetView(target[kind].Get(),nullptr,&rt[kind]));td.BindFlags=0;td.Usage=D3D11_USAGE_STAGING;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;check(device->CreateTexture2D(&td,nullptr,&staging[kind]));}
    int cases=read<int>(input);
    // Original bytecode uses distinct s0/s1/s2, whereas the reference shares s1.
    ID3D11SamplerState* originalSamplers[]={srcSampler.Get(),atlasSampler.Get(),atlasSampler.Get()};ctx->PSSetSamplers(0,3,originalSamplers);
    for(int n=0;n<cases;n++) {float constants[12]={};for(int i=0;i<4;i++)constants[i]=read<float>(input);constants[8]=constants[0];
        float time[4]={constants[1]/20,constants[1],constants[1]*2,constants[1]*3};ctx->UpdateSubresource(timeBuffer.Get(),0,nullptr,time,0,0);
        int orientation=constants[2]!=0;ID3D11ShaderResourceView* resources[]={views[orientation][0].Get(),views[orientation][1].Get(),views[orientation][2].Get()};ctx->PSSetShaderResources(0,3,resources);ctx->UpdateSubresource(cb.Get(),0,nullptr,constants,0,0);
        for(int kind=0;kind<2;kind++) {ID3D11RenderTargetView* targets[]={rt[kind].Get()};ctx->OMSetRenderTargets(1,targets,nullptr);ctx->Draw(3,0);ctx->OMSetRenderTargets(0,nullptr,nullptr);ctx->CopyResource(staging[kind].Get(),target[kind].Get());D3D11_MAPPED_SUBRESOURCE map;check(ctx->Map(staging[kind].Get(),0,D3D11_MAP_READ,0,&map));int stride=width*(kind?4:16);
            // Canonical output order is GL bottom-up, for both input conventions.
            for(int y=height-1;y>=0;y--)output.write((char*)map.pData+y*map.RowPitch,stride);ctx->Unmap(staging[kind].Get(),0);
        }
    }
    if(!input||!output)throw std::runtime_error("I/O failure");return 0;
} catch(const std::exception& e) {std::cerr<<e.what()<<"\n";return 1;} }
