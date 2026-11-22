#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uModel[4];
uniform 	vec4 uViewProjection[4];
uniform 	vec4 _Color;
layout(location = 0) in vec4 aPosition;
layout(location = 4) in vec4 aVertexColor;
layout(location = 2) in vec2 aTexCoord;
out vec4 vs_COLOR0;
out vec2 vs_TEXCOORD0;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat0 = u_xlat0 + uModel[3];
    u_xlat1 = u_xlat0.yyyy * uViewProjection[1];
    u_xlat1 = uViewProjection[0] * u_xlat0.xxxx + u_xlat1;
    u_xlat1 = uViewProjection[2] * u_xlat0.zzzz + u_xlat1;
    gl_Position = uViewProjection[3] * u_xlat0.wwww + u_xlat1;
    vs_COLOR0 = aVertexColor * _Color;
    vs_TEXCOORD0.xy = aTexCoord.xy;
    return;
}
