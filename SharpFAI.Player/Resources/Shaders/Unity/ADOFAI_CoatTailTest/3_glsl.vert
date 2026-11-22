#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 u_Time;
uniform 	vec4 uScreenParams;
uniform 	vec4 uModel[4];
uniform 	vec4 uViewProjection[4];
uniform 	vec4 _Color;
uniform 	vec2 _Magnitude;
uniform 	vec2 _Frequency;
uniform 	vec2 _WaveLength;
layout(location = 0) in vec4 aPosition;
layout(location = 4) in vec4 aVertexColor;
layout(location = 2) in vec2 aTexCoord;
out vec4 vs_COLOR0;
out vec2 vs_TEXCOORD0;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    u_xlat0.xy = u_Time.yy * vec2(_Frequency.x, _Frequency.y);
    u_xlat0.xy = aPosition.yx * _WaveLength.xy + u_xlat0.xy;
    u_xlat0.xy = sin(u_xlat0.xy);
    u_xlat0.xy = u_xlat0.xy * _Magnitude.xy;
    u_xlat0.z = 0.0;
    u_xlat0.xyz = u_xlat0.xyz + aPosition.xyz;
    u_xlat1 = u_xlat0.yyyy * uModel[1];
    u_xlat1 = uModel[0] * u_xlat0.xxxx + u_xlat1;
    u_xlat0 = uModel[2] * u_xlat0.zzzz + u_xlat1;
    u_xlat0 = u_xlat0 + uModel[3];
    u_xlat1 = u_xlat0.yyyy * uViewProjection[1];
    u_xlat1 = uViewProjection[0] * u_xlat0.xxxx + u_xlat1;
    u_xlat1 = uViewProjection[2] * u_xlat0.zzzz + u_xlat1;
    u_xlat0 = uViewProjection[3] * u_xlat0.wwww + u_xlat1;
    u_xlat0.xy = u_xlat0.xy / u_xlat0.ww;
    u_xlat1.xy = uScreenParams.xy * vec2(0.5, 0.5);
    u_xlat0.xy = u_xlat0.xy * u_xlat1.xy;
    u_xlat0.xy = roundEven(u_xlat0.xy);
    u_xlat0.xy = u_xlat0.xy / u_xlat1.xy;
    gl_Position.xy = u_xlat0.ww * u_xlat0.xy;
    gl_Position.zw = u_xlat0.zw;
    vs_COLOR0 = aVertexColor * _Color;
    vs_TEXCOORD0.xy = aTexCoord.xy;
    return;
}
