// Independent equations traced from Legacy004 3_ps.orig.asm, not Advanced229.
// Six-decimal disassembly literals lose hash-sensitive bits. Expanded constants
// below use the corresponding original Linux literals (same float32 values).
cbuffer Material : register(b0) { float tracking; float seconds; float nativeCoordinates; float diagnostic; };
Texture2D sourceImage : register(t0);
Texture2D noiseImage : register(t1);
Texture2D trackingImage : register(t2);
SamplerState sourceSampler : register(s0);
SamplerState atlasSampler : register(s1);
float hashValue(float2 p) {
    float a = dot(p, float2(12.9898005,78.2330017)) * 0.318471313;
    a = (a >= -a ? 1 : -1) * frac(abs(a)) * 3.1400001;
    return frac(sin(a) * 43758.5469);
}
float4 main(float2 uv : TEXCOORD0, float4 position : SV_POSITION) : SV_Target {
    float t = seconds / 20.0;
    float row = floor(uv.y * 288.0);
    float h1 = hashValue(float2(t*0.4, row*0.00347222225));
    float h2 = hashValue(float2(t*0.0130000003, row*0.00145833334));
    float h0 = hashValue(float2(t*20,t*20));
    if (diagnostic == 1) return float4(h0,h1,h2,1);
    if (diagnostic == 2) return sourceImage.Sample(sourceSampler,uv);
    float a = saturate((uv.y-h0*15)*33.3333359);
    float b = saturate((uv.y-(h0*15+0.0599999987))*33.3333282);
    float band = a*a*(3-2*a)-b*b*(3-2*b);
    float x = uv.x;
    if (uv.y < 0.0250000004) x += (uv.y-0.0500000007)*sin(uv.y*512+t*12);
    if (uv.y < 0.0149999997) x += (uv.y-0.0500000007)*sin(uv.y*512+t*64);
    x += band*sin(uv.y*150+t*48)*0.015625;
    x += (sin(h1)*0.00499999989+h2*0.00400000019)*(1-x);
    float s = sin(t*128);
    float2 chromaUv = float2(floor(x*52)*0.0192307699 + (s*0.0078125-0.00999999978)*s*0.0078125,
                           row*0.00347222225 + cos(t*128)*s*0.0078125*0.0078125);
    float3 chroma = sourceImage.Sample(sourceSampler,chromaUv).rgb;
    float u = chroma.b*0.43599999 + (-chroma.r*0.147-chroma.g*0.289000005);
    float v = -chroma.b*0.100000001 + (chroma.r*0.61500001-chroma.g*0.514999986);
    float gain = (max(sin(row*0.0486111119+t*16),0)+max(sin(row*0.0833333358+t*64),0))*0.5+0.5;
    u *= gain; v *= gain;
    float3 base = clamp(sourceImage.Sample(sourceSampler,float2(x,uv.y)).rgb,0.0799999982,0.949999988);
    float luma = dot(base,float3(0.298999995,0.587000012,0.114));
    float3 rgb = clamp(float3(luma+v*1.13999999, (luma-u*0.395000011)-v*0.58099997, luma+u*2.03200006),0.0799999982,0.949999988);
    float2 tile = floor(frac(abs(t*float2(30,3.75)))*8);
    float2 atlasUv = float2(uv.x*0.125+tile.x*0.125,uv.y*0.125+1-tile.y*0.125);
    float3 noise = noiseImage.Sample(atlasSampler,atlasUv).rgb;
    float3 low = rgb*2.0999999*noise;
    float3 high = 1-((1-rgb*1.04999995)*2)*(1-noise);
    float3 result = float3(rgb.r<0.476190507?low.r:high.r,rgb.g<0.476190507?low.g:high.g,rgb.b<0.476190507?low.b:high.b);
    float2 trackUv = float2(atlasUv.x,(1-tile.y*0.125)+(1-uv.y*0.125));
    return float4((1-uv.y)*tracking*trackingImage.Sample(atlasSampler,trackUv).rgb+result,1);
}
