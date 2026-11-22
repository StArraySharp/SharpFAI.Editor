#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 _CommentColor;
uniform 	float _Flash;
uniform 	float _Alpha;
uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec3 u_xlat1;
float u_xlat6;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat1.xyz = (-u_xlat0.xyz) * vs_COLOR0.xyz + vec3(1.0, 1.0, 1.0);
    u_xlat0 = u_xlat0 * vs_COLOR0;
    u_xlat0.xyz = vec3(vec3(_Flash, _Flash, _Flash)) * u_xlat1.xyz + u_xlat0.xyz;
    u_xlat6 = u_xlat0.w * _Alpha;
    u_xlat1.xyz = (-u_xlat0.xyz) + _CommentColor.xyz;
    u_xlat0.xyz = _CommentColor.www * u_xlat1.xyz + u_xlat0.xyz;
    SV_Target0.xyz = vec3(u_xlat6) * u_xlat0.xyz;
    SV_Target0.w = u_xlat6;
    return;
}
