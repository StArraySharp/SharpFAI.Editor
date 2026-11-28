#version 330 core

in vec2 vUv;
in vec4 vTint;

uniform sampler2D uParticleTex;
uniform bool uStraightAlpha;
uniform sampler2D uParticleMask;
uniform vec2 uParticleViewport;
uniform int uParticleMaskMode;

out vec4 FragColor;

void main()
{
    // 复刻 Legacy Shaders/Particles/Alpha Blended Premultiply 的片元：
    //     return i.color * tex2D(_MainTex, i.texcoord) * i.color.a;
    // 注意最后乘的是**顶点色的 alpha**，不是贴图的 alpha。
    // 配合 Blend One OneMinusSrcAlpha（预乘 alpha），完全不透明处会直接覆盖背景。
    // Mirrors the fragment half of Legacy Shaders/Particles/Alpha Blended Premultiply:
    //     return i.color * tex2D(_MainTex, i.texcoord) * i.color.a;
    // Note the multiplier is the **vertex alpha**, not the texture's. Together with
    // Blend One OneMinusSrcAlpha (premultiplied alpha) that makes opaque particles cover the
    // background outright instead of merely adding to it.
    vec4 texel = texture(uParticleTex, vUv);
    if (uStraightAlpha)
        texel.rgb *= texel.a;
    FragColor = vTint * texel * vTint.a;
    if (uParticleMaskMode != 0)
    {
        float coverage = texture(uParticleMask, gl_FragCoord.xy / uParticleViewport).a;
        FragColor *= uParticleMaskMode == 1 ? coverage : 1.0 - coverage;
    }

    if (FragColor.a <= 0.0001 && FragColor.r + FragColor.g + FragColor.b <= 0.0001)
    {
        discard;
    }
}
