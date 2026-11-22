using NUnit.Framework;
using SharpFAI.Player.Engine.ShaderGen;
using System;
using System.IO;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// <see cref="UnityShaderAdapter"/> 的回归测试：拿游戏自带的 hlslcc 输出跑一遍，确认产物
/// 结构正确。数据源缺失时跳过，方便在没装游戏的机器上跑测试。
/// </summary>
[TestFixture]
public class TestUnityShaderAdapter
{
    private const string GameShaderRoot =
        @"D:\Softwares\Steam\steamapps\content\app_977950\depot_977954\Bundles_Ripped\Assets\Shader";

    private static string? FloorMeshSource()
    {
        string path = Path.Combine(GameShaderRoot, "ADOFAI_FloorMesh", "2_glsl.orig.glsl");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Test]
    public void FloorMeshConvertsToCompilableLookingStages()
    {
        string? source = FloorMeshSource();

        if (source is null)
        {
            Assert.Ignore($"Game shaders not found: {GameShaderRoot}");
            return;
        }

        UnityShaderAdapter.AdapterResult result = UnityShaderAdapter.Convert(source, "_UV2Offset", "_Sprite");

        TestContext.Out.WriteLine("--- vertex ---");
        TestContext.Out.WriteLine(result.VertexSource);
        TestContext.Out.WriteLine("--- fragment ---");
        TestContext.Out.WriteLine(result.FragmentSource);

        // #version 必须在首行，且带 core profile（layout(location) 需要）
        Assert.That(result.VertexSource.Split('\n')[0], Does.StartWith("#version"));
        Assert.That(result.VertexSource.Split('\n')[0], Does.Contain("core"));
        Assert.That(result.FragmentSource.Split('\n')[0], Does.StartWith("#version"));

        // 官方输入被换成引擎属性名
        Assert.That(result.VertexSource, Does.Contain("aPosition"));
        Assert.That(result.VertexSource, Does.Contain("layout(location = 0)"));
        Assert.That(result.VertexSource, Does.Not.Contain("in_POSITION0"));

        // 桥接参数：uniform 消失，改用顶点属性 + varying
        Assert.That(result.VertexSource, Does.Not.Contain("uniform 	vec4 _UV2Offset;"));
        Assert.That(result.VertexSource, Does.Contain("vUv2Offset"));
        Assert.That(result.FragmentSource, Does.Contain("in vec4 vUv2Offset;"));

        // Unity 内置矩阵改名
        Assert.That(result.VertexSource, Does.Contain("uViewProjection"));
        Assert.That(result.VertexSource, Does.Not.Contain("u_ViewProjection"));

        // 没有残留的 Unity 兼容宏
        foreach (string stage in new[] { result.VertexSource, result.FragmentSource })
        {
            Assert.That(stage, Does.Not.Contain("HLSLCC_ENABLE_UNIFORM_BUFFERS"));
            Assert.That(stage, Does.Not.Contain("UNITY_LOCATION"));
            Assert.That(stage, Does.Not.Contain("UNITY_BINDING"));
        }
    }

    [Test]
    public void BridgedAttributesGetConsecutiveLocationsAfterTheEngineSlots()
    {
        string? source = FloorMeshSource();

        if (source is null)
        {
            Assert.Ignore($"Game shaders not found: {GameShaderRoot}");
            return;
        }

        UnityShaderAdapter.AdapterResult result =
            UnityShaderAdapter.Convert(source, "_UV2Offset", "_Sprite");

        // 引擎占 0..4，in_TEXCOORD1（uv2 瓷砖坐标）占 5，桥接参数从 6 起
        Assert.That(result.Attributes.First(a => a.Name == "aTileCoord").Location, Is.EqualTo(5));

        UnityShaderAdapter.ShaderAttribute uv = result.Attributes.First(a => a.Name == "aUv2Offset");
        UnityShaderAdapter.ShaderAttribute sprite = result.Attributes.First(a => a.Name == "aSprite");

        Assert.That(uv.Location, Is.EqualTo(6));
        Assert.That(sprite.Location, Is.EqualTo(7));
    }

    [Test]
    public void PositionAndColorMapToTheEngineSlots()
    {
        string? source = FloorMeshSource();

        if (source is null)
        {
            Assert.Ignore($"Game shaders not found: {GameShaderRoot}");
            return;
        }

        UnityShaderAdapter.AdapterResult result = UnityShaderAdapter.Convert(source);

        Assert.That(result.Attributes.First(a => a.Name == "aPosition").Location, Is.EqualTo(0));
        Assert.That(result.Attributes.First(a => a.Name == "aVertexColor").Location, Is.EqualTo(4));
    }

    [Test]
    public void EveryAdofaiShaderConvertsWithoutThrowing()
    {
        if (!Directory.Exists(GameShaderRoot))
        {
            Assert.Ignore($"Game shaders not found: {GameShaderRoot}");
            return;
        }

        string[] files = Directory.GetFiles(GameShaderRoot, "*.glsl", SearchOption.AllDirectories);
        TestContext.Out.WriteLine($"glsl files = {files.Length}");

        int converted = 0;
        var failures = new System.Collections.Generic.List<string>();

        foreach (string file in files)
        {
            try
            {
                UnityShaderAdapter.Convert(File.ReadAllText(file));
                converted++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        TestContext.Out.WriteLine($"converted = {converted}, failed = {failures.Count}");

        foreach (string failure in failures.Take(10))
        {
            TestContext.Out.WriteLine(failure);
        }

        // 这一轮只要求「不抛异常」，编译正确性由引擎侧验证
        Assert.That(failures, Is.Empty, $"{failures.Count} of {files.Length} shaders failed to convert");
    }
}
