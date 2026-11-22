#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uScreenParams;
uniform 	vec4 uModel[4];
uniform 	vec4 uViewProjection[4];
uniform vec2 uCameraOrigin;
uniform sampler2D uFloorData;
uniform bool uStreamFloors;
uniform samplerBuffer uFloorInstances;
uniform samplerBuffer uFloorShapes;
uniform 	float _FlashColor;
layout(location = 0) in vec4 aPosition;
layout(location = 4) in vec4 aVertexColor;
layout(location = 2) in vec2 aTexCoord;
layout(location = 5) in vec2 aTileCoord;

layout(location = 1) in vec4 aIconData;
layout(location = 3) in vec4 aStyleParams;
layout(location = 6) in vec4 aShadowColor;
layout(location = 7) in vec4 aTextureSlots;
layout(location = 8) in vec4 aTileOffset;

out vec4 vs_COLOR0;
out vec2 vs_TEXCOORD0;
out vec2 vs_TEXCOORD1;
flat out vec4 vs_ICONDATA;
flat out vec4 vs_STYLEPARAMS;
flat out vec4 vs_SHADOWCOLOR;
flat out ivec4 vs_TEXTURESLOTS;
flat out vec3 vs_TILEOFFSET;
flat out int vs_OUTLINESLOT;
flat out float vs_OPACITY;
flat out float vs_GLOW;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    vec4 position = aPosition;
    vec4 iconData = aIconData;
    int instance = 0;
    int vertex = 0;
    if (uStreamFloors)
    {
        // The element buffer carries the instance number and the vertex inside its shared shape.
        instance = (gl_VertexID >> 10) * 6;
        vec4 instanceAddress = texelFetch(uFloorInstances, instance);
        position.w = instanceAddress.y;
        vertex = int(instanceAddress.x) + 3 * (gl_VertexID & 1023);
        iconData = texelFetch(uFloorInstances, instance + 1);
    }
    int floorId = int(position.w);
    // Four RGBA32F texels per floor: color, transform, appearance, and active material.
    ivec2 address = ivec2((floorId % 256) * 4, floorId / 256);
    vec4 variantOffset = texelFetch(uFloorData, address + ivec2(3, 0), 0);
    if (variantOffset.x != iconData.y)
    {
        gl_Position = vec4(2.0, 2.0, 0.0, 1.0);
        return;
    }
    vec4 floorColor = texelFetch(uFloorData, address, 0);
    vec4 transform = texelFetch(uFloorData, address + ivec2(1, 0), 0);
    vec4 scaleOpacity = texelFetch(uFloorData, address + ivec2(2, 0), 0);
    vec4 vertexColor = aVertexColor;
    vec4 uv = vec4(aTexCoord, aTileCoord);
    vec4 style = aStyleParams;
    vec4 shadow = aShadowColor;
    vec4 textures = aTextureSlots;
    vec4 tileOffset = aTileOffset;
    if (uStreamFloors)
    {
        position.xyz = texelFetch(uFloorShapes, vertex).xyz;
        vertexColor = texelFetch(uFloorShapes, vertex + 1);
        uv = texelFetch(uFloorShapes, vertex + 2);
        style = texelFetch(uFloorInstances, instance + 2);
        shadow = texelFetch(uFloorInstances, instance + 3);
        textures = texelFetch(uFloorInstances, instance + 4);
        tileOffset = texelFetch(uFloorInstances, instance + 5);
    }
    vec2 local = position.xy * scaleOpacity.xy;
    // Subtract the camera before adding sub-tile offsets to preserve distant geometry.
    vec2 relative = (transform.xy - uCameraOrigin) + variantOffset.yz;
    relative += mat2(transform.z, transform.w, -transform.w, transform.z) * local;

    u_xlat0 = relative.yyyy * uModel[1];
    u_xlat0 = uModel[0] * relative.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * position.zzzz + u_xlat0;
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
    u_xlat0.xyz = (-floorColor.xyz) + vec3(1.0, 1.0, 1.0);
    u_xlat0.xyz = vec3(_FlashColor) * u_xlat0.xyz + floorColor.xyz;
    vs_COLOR0.xyz = u_xlat0.xyz * vertexColor.xyz;
    vs_COLOR0.w = vertexColor.w * floorColor.w;
    vs_TEXCOORD0.xy = uv.xy;
    vs_TEXCOORD1.xy = uv.zw;
    vs_ICONDATA = vec4(iconData.x, 0.75, iconData.zw);
    vs_STYLEPARAMS = style;
    vs_SHADOWCOLOR = vec4(shadow.xyz * floorColor.xyz, shadow.w);
    vs_TEXTURESLOTS = ivec4(textures);
    vs_TILEOFFSET = tileOffset.xyz;
    vs_OUTLINESLOT = int(tileOffset.w);
    vs_OPACITY = scaleOpacity.z;
    vs_GLOW = scaleOpacity.w;
    return;
}
