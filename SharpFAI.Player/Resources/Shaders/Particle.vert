#version 330 core

// 拖尾粒子：实例化 Billboard 四边形，UV 覆盖整张粒子贴图
// Trail particle: instanced billboard quad whose UV spans the whole particle texture.
layout(location = 0) in vec2 aCorner;

// 逐实例属性 / Per-instance attributes
layout(location = 1) in vec2 iCenter;
layout(location = 2) in vec2 iHalfSize;
layout(location = 3) in vec4 iColor;
layout(location = 4) in vec2 iRotationFrame;

uniform mat4 uViewProjection;
uniform float uColumns;

out vec2 vUv;
out vec4 vTint;

void main()
{
    float c = cos(iRotationFrame.x);
    float s = sin(iRotationFrame.x);
    vec2 offset = mat2(c, s, -s, c) * (aCorner * iHalfSize);
    gl_Position = uViewProjection * vec4(iCenter + offset, 0.0, 1.0);

    // aCorner 是 [-1,1] -> [0,1]
    // aCorner is in [-1,1] -> [0,1]
    vUv = aCorner * 0.5 + 0.5;
    vUv.x = (vUv.x + iRotationFrame.y) / uColumns;
    vTint = iColor;
}
