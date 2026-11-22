#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _TileX;
uniform 	float _TileY;
uniform 	float _GapX;
uniform 	float _GapY;
uniform  sampler2D _MainTex;
in  vec2 vs_TEXCOORD0;
in  vec4 vs_COLOR0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec2 u_xlat1;
bvec2 u_xlatb1;
vec2 u_xlat4;
void main()
{
    u_xlat0.xy = vs_TEXCOORD0.xy * vec2(_TileX, _TileY);
    u_xlat0.xy = fract(u_xlat0.xy);
    u_xlat4.xy = (-vec2(_GapX, _GapY)) + vec2(1.0, 1.0);
    u_xlatb1.xy = greaterThanEqual(u_xlat4.xyxx, u_xlat0.xyxx).xy;
    u_xlat0.xy = u_xlat0.xy / u_xlat4.xy;
    u_xlat0 = texture(_MainTex, u_xlat0.xy);
    u_xlat0 = u_xlat0 * vs_COLOR0;
    u_xlat1.x = u_xlatb1.x ? float(1.0) : 0.0;
    u_xlat1.y = u_xlatb1.y ? float(1.0) : 0.0;
;
    u_xlat1.x = u_xlat1.y * u_xlat1.x;
    SV_Target0.w = u_xlat0.w * u_xlat1.x;
    SV_Target0.xyz = u_xlat0.xyz;
    return;
}
