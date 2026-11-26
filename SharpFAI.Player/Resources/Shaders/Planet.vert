#version 330 core

// 行星精灵：实例化四边形 + 图集帧索引采样
// Planet sprite: instanced quad sampling one frame of the atlas.
layout(location = 0) in vec2 aCorner;

uniform mat4 uViewProjection;
uniform vec3 uViewRight;
uniform vec3 uViewUp;
uniform vec2 uCenter;
uniform vec2 uHalfSize;
uniform float uRotation;
uniform vec4 uTint;

uniform int uFrame;
uniform int uFrames;
uniform int uVertical;
uniform vec4 uSphereRect;   // xy = 球体在帧内的起点, zw = 尺寸 (0~1)

out vec2 vUv;
out vec4 vTint;

void main()
{
    float s = sin(uRotation);
    float c = cos(uRotation);
    vec2 local = aCorner * uHalfSize;
    vec2 rotated = vec2(local.x * c - local.y * s, local.x * s + local.y * c);

    vec3 position = vec3(uCenter, 0.0) + uViewRight * rotated.x + uViewUp * rotated.y;
    gl_Position = uViewProjection * vec4(position, 1.0);

    vec2 corner = aCorner * 0.5 + 0.5;

    // 竖排图集在上传时做过垂直翻转，frame 0 落在纹理底部
    // Vertical atlases are flipped on upload, so frame 0 sits at the bottom
    vec2 frameOrigin = (uVertical == 1)
        ? vec2(0.0, 1.0 - float(uFrame + 1) / float(uFrames))
        : vec2(float(uFrame) / float(uFrames), 0.0);

    vec2 frameSize = (uVertical == 1)
        ? vec2(1.0, 1.0 / float(uFrames))
        : vec2(1.0 / float(uFrames), 1.0);

    vec2 sphereOrigin = uSphereRect.xy;
    if (uVertical == 1)
    {
        sphereOrigin.y = 1.0 - (uSphereRect.y + uSphereRect.w);
    }

    vUv = frameOrigin + (sphereOrigin + corner * uSphereRect.zw) * frameSize;
    vTint = uTint;
}
