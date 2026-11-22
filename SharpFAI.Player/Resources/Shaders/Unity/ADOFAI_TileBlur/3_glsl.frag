#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 _MainTex_TexelSize;
uniform 	float _BlurSize;
uniform  sampler2D _MainTex;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
vec4 u_xlat3;
vec4 u_xlat4;
vec2 u_xlat6;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat1.x = 0.0;
    u_xlat1.y = _MainTex_TexelSize.y * _BlurSize;
    u_xlat2.xy = u_xlat1.xy + vs_TEXCOORD0.xy;
    u_xlat1.xw = (-u_xlat1.xy) + vs_TEXCOORD0.xy;
    u_xlat3 = texture(_MainTex, u_xlat1.xw);
    u_xlat2 = texture(_MainTex, u_xlat2.xy);
    u_xlat2 = u_xlat2 * vec4(0.150000006, 0.150000006, 0.150000006, 0.150000006);
    u_xlat0 = u_xlat0 * vec4(0.159999996, 0.159999996, 0.159999996, 0.159999996) + u_xlat2;
    u_xlat0 = u_xlat3 * vec4(0.150000006, 0.150000006, 0.150000006, 0.150000006) + u_xlat0;
    u_xlat1.z = _MainTex_TexelSize.x;
    u_xlat2 = u_xlat1.zyzy * vec4(0.0, 2.0, 0.0, 3.0) + vs_TEXCOORD0.xyxy;
    u_xlat3 = texture(_MainTex, u_xlat2.xy);
    u_xlat2 = texture(_MainTex, u_xlat2.zw);
    u_xlat0 = u_xlat3 * vec4(0.119999997, 0.119999997, 0.119999997, 0.119999997) + u_xlat0;
    u_xlat3 = (-u_xlat1.zyzy) * vec4(0.0, 2.0, 0.0, 3.0) + vs_TEXCOORD0.xyxy;
    u_xlat4 = texture(_MainTex, u_xlat3.xy);
    u_xlat3 = texture(_MainTex, u_xlat3.zw);
    u_xlat0 = u_xlat4 * vec4(0.119999997, 0.119999997, 0.119999997, 0.119999997) + u_xlat0;
    u_xlat0 = u_xlat2 * vec4(0.0900000036, 0.0900000036, 0.0900000036, 0.0900000036) + u_xlat0;
    u_xlat0 = u_xlat3 * vec4(0.0900000036, 0.0900000036, 0.0900000036, 0.0900000036) + u_xlat0;
    u_xlat1.xw = u_xlat1.zy * vec2(0.0, 4.0) + vs_TEXCOORD0.xy;
    u_xlat6.xy = (-u_xlat1.zy) * vec2(0.0, 4.0) + vs_TEXCOORD0.xy;
    u_xlat2 = texture(_MainTex, u_xlat6.xy);
    u_xlat1 = texture(_MainTex, u_xlat1.xw);
    u_xlat0 = u_xlat1 * vec4(0.0599999987, 0.0599999987, 0.0599999987, 0.0599999987) + u_xlat0;
    SV_Target0 = u_xlat2 * vec4(0.0599999987, 0.0599999987, 0.0599999987, 0.0599999987) + u_xlat0;
    return;
}
