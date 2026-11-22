#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _ScrollX;
uniform 	float _ScrollY;
uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec2 u_xlat2;
bvec2 u_xlatb2;
void main()
{
    u_xlat0.xy = vs_TEXCOORD0.xy + vec2(_ScrollX, _ScrollY);
    u_xlatb2.xy = greaterThanEqual(u_xlat0.xyxy, (-u_xlat0.xyxy)).xy;
    u_xlat2.x = (u_xlatb2.x) ? float(1.0) : float(-1.0);
    u_xlat2.y = (u_xlatb2.y) ? float(1.0) : float(-1.0);
    u_xlat0.xy = u_xlat2.xy * u_xlat0.xy;
    u_xlat0.xy = fract(u_xlat0.xy);
    u_xlat0.xy = u_xlat0.xy * u_xlat2.xy;
    u_xlat0 = texture(_MainTex, u_xlat0.xy);
    u_xlat0 = u_xlat0 * vs_COLOR0;
    SV_Target0.xyz = u_xlat0.www * u_xlat0.xyz;
    SV_Target0.w = u_xlat0.w;
    return;
}
