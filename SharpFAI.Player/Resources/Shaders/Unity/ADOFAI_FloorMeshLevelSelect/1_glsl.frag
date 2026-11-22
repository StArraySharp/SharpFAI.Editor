#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 _TileTex_ST;
uniform 	vec4 _UV2Offset;
uniform 	float _TileScale;
uniform 	float _Fade;
uniform 	vec4 _GradientPivot;
uniform 	vec4 _GradientColor1;
uniform 	vec4 _GradientColor2;
uniform 	float _GradientScale;
uniform  sampler2D _TileTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD1;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec3 u_xlat1;
vec4 u_xlat2;
vec2 u_xlat6;
float u_xlat9;
void main()
{
    u_xlat0.x = _UV2Offset.z * 3.14152908;
    u_xlat1.x = cos(u_xlat0.x);
    u_xlat0.x = sin(u_xlat0.x);
    u_xlat0.xy = u_xlat0.xx * vs_TEXCOORD1.yx;
    u_xlat2.x = vs_TEXCOORD1.x * u_xlat1.x + (-u_xlat0.x);
    u_xlat2.y = vs_TEXCOORD1.y * u_xlat1.x + u_xlat0.y;
    u_xlat0.xy = u_xlat2.xy / vec2(_TileScale);
    u_xlat6.xy = _TileTex_ST.zw + _UV2Offset.xy;
    u_xlat0.xy = u_xlat6.xy + u_xlat0.xy;
    u_xlat0 = texture(_TileTex, u_xlat0.xy);
    u_xlat1.xyz = u_xlat0.xyz * vs_COLOR0.xyz;
    u_xlat0.xyz = (-u_xlat0.xyz) * vs_COLOR0.xyz + vs_COLOR0.xyz;
    u_xlat0.xyz = vec3(vec3(_Fade, _Fade, _Fade)) * u_xlat0.xyz + u_xlat1.xyz;
    u_xlat1.xyz = u_xlat0.xyz * vs_COLOR0.www;
    u_xlat2.xy = vs_TEXCOORD1.xy + (-_GradientPivot.xy);
    u_xlat2.xy = u_xlat2.xy * u_xlat2.xy;
    u_xlat9 = u_xlat2.y + u_xlat2.x;
    u_xlat9 = sqrt(u_xlat9);
    u_xlat9 = u_xlat9 * _GradientScale;
    u_xlat9 = clamp(u_xlat9, 0.0, 1.0);
    u_xlat2 = (-_GradientColor1) + _GradientColor2;
    u_xlat2 = vec4(u_xlat9) * u_xlat2 + _GradientColor1;
    u_xlat0.xyz = (-u_xlat0.xyz) * vs_COLOR0.www + u_xlat2.xyz;
    SV_Target0.xyz = u_xlat2.www * u_xlat0.xyz + u_xlat1.xyz;
    SV_Target0.w = vs_COLOR0.w;
    return;
}
