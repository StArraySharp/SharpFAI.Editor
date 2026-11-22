#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _TileX;
uniform 	float _TileY;
uniform  sampler2D _MainTex;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec2 u_xlat0;
vec2 u_xlat1;
bvec2 u_xlatb1;
vec2 u_xlat4;
bvec2 u_xlatb4;
void main()
{
    u_xlat0.xy = vs_TEXCOORD0.xy * vec2(_TileX, _TileY);
    u_xlatb4.xy = greaterThanEqual((-u_xlat0.xyxy), u_xlat0.xyxy).xy;
    u_xlat4.x = (u_xlatb4.x) ? float(1.0) : float(-1.0);
    u_xlat4.y = (u_xlatb4.y) ? float(1.0) : float(-1.0);
    u_xlat1.xy = u_xlat4.xy * (-u_xlat0.xy);
    u_xlat1.xy = fract(u_xlat1.xy);
    u_xlat4.xy = (-u_xlat4.xy) * u_xlat1.xy + vec2(1.0, 1.0);
    u_xlatb1.xy = lessThan(u_xlat0.xyxx, vec4(0.0, 0.0, 0.0, 0.0)).xy;
    {
        vec2 hlslcc_movcTemp = u_xlat0;
        hlslcc_movcTemp.x = (u_xlatb1.x) ? u_xlat4.x : u_xlat0.x;
        hlslcc_movcTemp.y = (u_xlatb1.y) ? u_xlat4.y : u_xlat0.y;
        u_xlat0 = hlslcc_movcTemp;
    }
    u_xlatb4.xy = greaterThanEqual(u_xlat0.xyxy, (-u_xlat0.xyxy)).xy;
    u_xlat4.x = (u_xlatb4.x) ? float(1.0) : float(-1.0);
    u_xlat4.y = (u_xlatb4.y) ? float(1.0) : float(-1.0);
    u_xlat0.xy = u_xlat4.xy * u_xlat0.xy;
    u_xlat0.xy = fract(u_xlat0.xy);
    u_xlat0.xy = u_xlat0.xy * u_xlat4.xy;
    SV_Target0 = texture(_MainTex, u_xlat0.xy);
    return;
}
