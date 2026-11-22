#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform  sampler2D _MainTex;
in  vec2 vs_TEXCOORD0;
in  vec4 vs_COLOR0;
layout(location = 0) out vec4 SV_Target0;
vec3 u_xlat0;
float u_xlat1;
bool u_xlatb1;
vec3 u_xlat2;
bvec3 u_xlatb2;
bool u_xlatb3;
float u_xlat4;
float u_xlat6;
bool u_xlatb6;
void main()
{
    u_xlat0.xy = vs_TEXCOORD0.xy + vec2(-0.5, -0.5);
    u_xlat4 = max(abs(u_xlat0.x), abs(u_xlat0.y));
    u_xlat4 = float(1.0) / u_xlat4;
    u_xlat6 = min(abs(u_xlat0.x), abs(u_xlat0.y));
    u_xlat4 = u_xlat4 * u_xlat6;
    u_xlat6 = u_xlat4 * u_xlat4;
    u_xlat1 = u_xlat6 * 0.0208350997 + -0.0851330012;
    u_xlat1 = u_xlat6 * u_xlat1 + 0.180141002;
    u_xlat1 = u_xlat6 * u_xlat1 + -0.330299497;
    u_xlat6 = u_xlat6 * u_xlat1 + 0.999866009;
    u_xlat1 = u_xlat6 * u_xlat4;
    u_xlat1 = u_xlat1 * -2.0 + 1.57079637;
    u_xlatb3 = abs(u_xlat0.x)<abs(u_xlat0.y);
    u_xlat1 = u_xlatb3 ? u_xlat1 : float(0.0);
    u_xlat4 = u_xlat4 * u_xlat6 + u_xlat1;
    u_xlatb6 = u_xlat0.x<(-u_xlat0.x);
    u_xlat6 = u_xlatb6 ? -3.14159274 : float(0.0);
    u_xlat4 = u_xlat6 + u_xlat4;
    u_xlat6 = min(u_xlat0.x, u_xlat0.y);
    u_xlatb6 = u_xlat6<(-u_xlat6);
    u_xlat1 = max(u_xlat0.x, u_xlat0.y);
    u_xlat0.x = dot(u_xlat0.xy, u_xlat0.xy);
    u_xlat0.x = sqrt(u_xlat0.x);
    u_xlatb1 = u_xlat1>=(-u_xlat1);
    u_xlatb6 = u_xlatb6 && u_xlatb1;
    u_xlat4 = (u_xlatb6) ? (-u_xlat4) : u_xlat4;
    u_xlat0.y = u_xlat4 * 0.159154937;
    u_xlat0.xy = u_xlat0.xy + vec2(0.400000006, 0.5);
    u_xlatb6 = (-u_xlat0.y)>=u_xlat0.y;
    u_xlat6 = (u_xlatb6) ? 1.0 : -1.0;
    u_xlat1 = u_xlat6 * (-u_xlat0.y);
    u_xlat1 = fract(u_xlat1);
    u_xlat6 = (-u_xlat6) * u_xlat1 + 1.0;
    u_xlatb1 = u_xlat0.y<0.0;
    u_xlat0.z = (u_xlatb1) ? u_xlat6 : u_xlat0.y;
    u_xlatb2.xz = greaterThanEqual(u_xlat0.xxzz, (-u_xlat0.xxzz)).xz;
    u_xlat2.x = (u_xlatb2.x) ? float(1.0) : float(-1.0);
    u_xlat2.z = (u_xlatb2.z) ? float(1.0) : float(-1.0);
    u_xlat0.xz = u_xlat2.xz * u_xlat0.xz;
    u_xlat0.xz = fract(u_xlat0.xz);
    u_xlat0.xy = u_xlat0.xz * u_xlat2.xz;
    u_xlat0.xy = u_xlat0.xy * vs_COLOR0.xy;
    SV_Target0 = texture(_MainTex, u_xlat0.xy);
    return;
}
