#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uModel[4];
uniform 	vec4 uViewProjection[4];
uniform 	float _StartAlpha;
uniform 	float _EndAlpha;
uniform 	float _StartWidth;
uniform 	float _EndWidth;
uniform 	vec3 _StartPosition;
uniform 	vec3 _EndPosition;
uniform 	int _NumVerts;
uniform 	float _CCW;
uniform 	float _HoldWidth;
uniform 	float _StartTileSize;
uniform 	float _EndTileSize;
uniform 	float _EntryAngle;
uniform 	float _AngleLength;
layout(location = 0) in vec4 aPosition;
layout(location = 4) in vec4 aVertexColor;
layout(location = 2) in vec2 aTexCoord;
layout(location = 5) in vec2 aTileCoord;
out vec4 vs_COLOR0;
out vec2 vs_TEXCOORD0;
vec4 u_xlat0;
bool u_xlatb0;
vec4 u_xlat1;
vec4 u_xlat2;
float u_xlat3;
float u_xlat4;
float u_xlat8;
float u_xlat12;
float u_xlat13;
void main()
{
    u_xlat0.x = aTileCoord.x + aTileCoord.x;
    u_xlatb0 = u_xlat0.x>=(-u_xlat0.x);
    u_xlat0.xy = (bool(u_xlatb0)) ? vec2(2.0, 0.5) : vec2(-2.0, -0.5);
    u_xlat4 = u_xlat0.y * aTileCoord.x;
    u_xlat4 = fract(u_xlat4);
    u_xlat0.x = dot(u_xlat0.xx, vec2(u_xlat4));
    u_xlat0.x = u_xlat0.x + -1.0;
    u_xlat4 = (-_StartWidth) + _EndWidth;
    u_xlat8 = aTileCoord.x * 0.5;
    u_xlat8 = floor(u_xlat8);
    u_xlat12 = float(_NumVerts);
    u_xlat1.x = u_xlat12 + -2.0;
    u_xlat12 = aTileCoord.x / u_xlat12;
    u_xlat1.x = u_xlat1.x * 0.5;
    u_xlat8 = u_xlat8 / u_xlat1.x;
    u_xlat4 = u_xlat8 * u_xlat4 + _StartWidth;
    u_xlat4 = u_xlat4 * _HoldWidth;
    u_xlat0.x = u_xlat0.x * u_xlat4;
    u_xlat0.x = u_xlat0.x * 0.5;
    u_xlat4 = (-_StartTileSize) + _EndTileSize;
    u_xlat4 = u_xlat8 * u_xlat4 + _StartTileSize;
    u_xlat1.xyz = (-vec3(_StartPosition.x, _StartPosition.y, _StartPosition.z)) + _EndPosition.xyz;
    u_xlat1.xyz = vec3(u_xlat8) * u_xlat1.xyz + vec3(_StartPosition.x, _StartPosition.y, _StartPosition.z);
    u_xlat13 = _CCW * _AngleLength;
    u_xlat8 = u_xlat13 * u_xlat8 + _EntryAngle;
    u_xlat2.x = sin(u_xlat8);
    u_xlat3 = cos(u_xlat8);
    u_xlat8 = u_xlat4 * u_xlat3 + u_xlat1.y;
    u_xlat4 = u_xlat4 * u_xlat2.x + u_xlat1.x;
    u_xlat4 = u_xlat0.x * u_xlat2.x + u_xlat4;
    u_xlat0.x = u_xlat0.x * u_xlat3 + u_xlat8;
    u_xlat2 = u_xlat0.xxxx * uViewProjection[1];
    u_xlat2 = uViewProjection[0] * vec4(u_xlat4) + u_xlat2;
    u_xlat1 = uViewProjection[2] * u_xlat1.zzzz + u_xlat2;
    u_xlat0.x = aPosition.y * uModel[1].w;
    u_xlat0.x = uModel[0].w * aPosition.x + u_xlat0.x;
    u_xlat0.x = uModel[2].w * aPosition.z + u_xlat0.x;
    u_xlat0.x = uModel[3].w * aPosition.w + u_xlat0.x;
    gl_Position = uViewProjection[3] * u_xlat0.xxxx + u_xlat1;
    u_xlat1.x = float(1.0);
    u_xlat1.y = float(1.0);
    u_xlat1.z = float(1.0);
    u_xlat2.x = float(0.0);
    u_xlat2.y = float(0.0);
    u_xlat2.z = float(0.0);
    u_xlat1.w = _StartAlpha;
    u_xlat2.w = (-u_xlat1.w) + _EndAlpha;
    u_xlat0 = vec4(u_xlat12) * u_xlat2 + u_xlat1;
    vs_COLOR0 = u_xlat0 * aVertexColor;
    vs_TEXCOORD0.xy = aTexCoord.xy;
    return;
}
