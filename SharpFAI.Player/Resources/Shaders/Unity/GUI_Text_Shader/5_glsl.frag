#version 330 core
#extension GL_ARB_explicit_attrib_location : require

uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    SV_Target0.w = u_xlat0.w * vs_COLOR0.w;
    SV_Target0.xyz = vs_COLOR0.xyz;
    return;
}
