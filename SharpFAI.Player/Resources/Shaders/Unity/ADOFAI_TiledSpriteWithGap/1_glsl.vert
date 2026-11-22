#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uModel[4];
uniform 	vec4 uViewProjection[4];
layout(location = 0) in vec4 aPosition;
layout(location = 2) in vec2 aTexCoord;
layout(location = 4) in vec4 aVertexColor;
out vec2 vs_TEXCOORD0;
out vec4 vs_COLOR0;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    vs_TEXCOORD0.xy = aTexCoord.xy;
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat0 = u_xlat0 + uModel[3];
    u_xlat1 = u_xlat0.yyyy * uViewProjection[1];
    u_xlat1 = uViewProjection[0] * u_xlat0.xxxx + u_xlat1;
    u_xlat1 = uViewProjection[2] * u_xlat0.zzzz + u_xlat1;
    gl_Position = uViewProjection[3] * u_xlat0.wwww + u_xlat1;
    vs_COLOR0 = aVertexColor;
    return;
}
