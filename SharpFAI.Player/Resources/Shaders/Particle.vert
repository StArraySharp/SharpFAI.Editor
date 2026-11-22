#version 330 core

// 拖尾粒子：实例化 Billboard 四边形，UV 覆盖整张粒子贴图
// Trail particle: instanced billboard quad whose UV spans the whole particle texture.
layout(location = 0) in vec2 aCorner;

// 逐实例属性 / Per-instance attributes
layout(location = 1) in vec2 iCenter;
layout(location = 2) in vec2 iHalfSize;
layout(location = 3) in vec4 iColor;

uniform mat4 uViewProjection;

out vec2 vUv;
out vec4 vTint;

void main()
{
    gl_Position = uViewProjection * vec4(iCenter + aCorner * iHalfSize, 0.0, 1.0);

    // aCorner 是 [-1,1] -> [0,1]
    // aCorner is in [-1,1] -> [0,1]
    vUv = aCorner * 0.5 + 0.5;
    vTint = iColor;
}
