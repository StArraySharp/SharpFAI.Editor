#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 _MainTex_TexelSize;
uniform 	float _BlurSize;
uniform 	float _Pass;
uniform  sampler2D _MainTex;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
float u_xlat3;
vec4 u_xlat4;
float u_xlat6;
bool u_xlatb8;
vec2 u_xlat11;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat1.x = _MainTex_TexelSize.x * _BlurSize;
    u_xlat1.y = float(0.0);
    u_xlat11.y = float(0.0);
    u_xlat2.xy = u_xlat1.xy + vs_TEXCOORD0.xy;
    u_xlat2 = texture(_MainTex, u_xlat2.xy);
    u_xlat3 = roundEven(u_xlat2.w);
    u_xlatb8 = _Pass==0.0;
    u_xlat2.w = (u_xlatb8) ? u_xlat3 : u_xlat2.w;
    u_xlat2 = u_xlat2 * vec4(0.150000006, 0.150000006, 0.150000006, 0.150000006);
    u_xlat3 = roundEven(u_xlat0.w);
    u_xlat0.w = (u_xlatb8) ? u_xlat3 : u_xlat0.w;
    u_xlat0 = u_xlat0 * vec4(0.159999996, 0.159999996, 0.159999996, 0.159999996) + u_xlat2;
    u_xlat2.xy = (-u_xlat1.xy) + vs_TEXCOORD0.xy;
    u_xlat2 = texture(_MainTex, u_xlat2.xy);
    u_xlat6 = roundEven(u_xlat2.w);
    u_xlat2.w = (u_xlatb8) ? u_xlat6 : u_xlat2.w;
    u_xlat0 = u_xlat2 * vec4(0.150000006, 0.150000006, 0.150000006, 0.150000006) + u_xlat0;
    u_xlat11.x = u_xlat1.x * 2.0;
    u_xlat2.xz = u_xlat1.xx * vec2(3.0, 4.0);
    u_xlat1.xy = u_xlat11.xy + vs_TEXCOORD0.xy;
    u_xlat11.xy = (-u_xlat11.xy) + vs_TEXCOORD0.xy;
    u_xlat4 = texture(_MainTex, u_xlat11.xy);
    u_xlat1 = texture(_MainTex, u_xlat1.xy);
    u_xlat3 = roundEven(u_xlat1.w);
    u_xlat1.w = (u_xlatb8) ? u_xlat3 : u_xlat1.w;
    u_xlat0 = u_xlat1 * vec4(0.119999997, 0.119999997, 0.119999997, 0.119999997) + u_xlat0;
    u_xlat1.x = roundEven(u_xlat4.w);
    u_xlat4.w = (u_xlatb8) ? u_xlat1.x : u_xlat4.w;
    u_xlat0 = u_xlat4 * vec4(0.119999997, 0.119999997, 0.119999997, 0.119999997) + u_xlat0;
    u_xlat2.y = float(0.0);
    u_xlat2.w = float(0.0);
    u_xlat1 = u_xlat2 + vs_TEXCOORD0.xyxy;
    u_xlat2 = (-u_xlat2) + vs_TEXCOORD0.xyxy;
    u_xlat4 = texture(_MainTex, u_xlat1.xy);
    u_xlat1 = texture(_MainTex, u_xlat1.zw);
    u_xlat3 = roundEven(u_xlat4.w);
    u_xlat4.w = (u_xlatb8) ? u_xlat3 : u_xlat4.w;
    u_xlat0 = u_xlat4 * vec4(0.0900000036, 0.0900000036, 0.0900000036, 0.0900000036) + u_xlat0;
    u_xlat4 = texture(_MainTex, u_xlat2.xy);
    u_xlat2 = texture(_MainTex, u_xlat2.zw);
    u_xlat3 = roundEven(u_xlat4.w);
    u_xlat4.w = (u_xlatb8) ? u_xlat3 : u_xlat4.w;
    u_xlat0 = u_xlat4 * vec4(0.0900000036, 0.0900000036, 0.0900000036, 0.0900000036) + u_xlat0;
    u_xlat3 = roundEven(u_xlat1.w);
    u_xlat1.w = (u_xlatb8) ? u_xlat3 : u_xlat1.w;
    u_xlat0 = u_xlat1 * vec4(0.0599999987, 0.0599999987, 0.0599999987, 0.0599999987) + u_xlat0;
    u_xlat1.x = roundEven(u_xlat2.w);
    u_xlat2.w = (u_xlatb8) ? u_xlat1.x : u_xlat2.w;
    SV_Target0 = u_xlat2 * vec4(0.0599999987, 0.0599999987, 0.0599999987, 0.0599999987) + u_xlat0;
    return;
}
