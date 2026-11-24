using System.Globalization;
using System.Numerics;
using SharpFAI.Player.Engine.Filters;

namespace SharpFAI.Player.Tests;

public sealed class TestShaderLabDefaults
{
    [Test]
    public void PlayerDoesNotEmbedOriginalShaderLabOrTextureMetadata()
    {
        string[] resources = typeof(ShaderLabDefaults).Assembly.GetManifestResourceNames();
        Assert.That(resources.Where(name => name.EndsWith(".shaderlab", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)), Is.Empty);
    }

    [Test]
    public void PlayerDoesNotEmbedZeroSuffixResources()
    {
        var assembly = typeof(ShaderLabDefaults).Assembly;
        var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        Assert.That(resources.Where(name => name.EndsWith("_0", StringComparison.Ordinal) ||
            Path.GetFileNameWithoutExtension(name).EndsWith("_0", StringComparison.Ordinal)), Is.Empty);

        foreach (var (variant, original) in GeneratedFilterAssetDefaults.TextureAliases)
            Assert.That(resources.Contains(original), Is.True, variant);
    }

    [Test]
    public void AppliesNumericTypesAndRangeParentheses()
    {
        var material = Apply("""
            Shader "Test" { Properties {
                _Float ("Float", Float) = -.5e+1
                _Trailing ("Trailing", Float) = 5.
                _Leading ("Leading", Float) = +.5
                _Int ("Int", Int) = -12
                _Integer ("Integer", Integer) = 2E1
                _Range ("Range", Range(-1e2, +.5)) = -2.5e-2
                _Vector ("Vector", Vector) = (-1, .5, 5., 2e1)
                _Color ("Color", Color) = (1, .25, -.5, 1)
            } SubShader { } }
            """);
        Assert.Multiple(() =>
        {
            Assert.That(material.Floats, Has.Count.EqualTo(4));
            Assert.That(material.Floats["_Float"], Is.EqualTo(-5));
            Assert.That(material.Floats["_Trailing"], Is.EqualTo(5));
            Assert.That(material.Floats["_Leading"], Is.EqualTo(.5f));
            Assert.That(material.Floats["_Range"], Is.EqualTo(-.025f));
            Assert.That(material.Ints["_Int"], Is.EqualTo(-12));
            Assert.That(material.Ints["_Integer"], Is.EqualTo(20));
            Assert.That(material.Vectors["_Vector"], Is.EqualTo(new Vector4(-1, .5f, 5, 20)));
            Assert.That(material.Vectors["_Color"], Is.EqualTo(new Vector4(1, .25f, -.5f, 1)));
        });
    }

    [TestCase("fr-FR")]
    [TestCase("de-DE")]
    public void UsesInvariantNumbers(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var material = Apply("Properties { _Value (\"Value\", Float) = 1.25e-1 }");
            Assert.That(material.Floats["_Value"], Is.EqualTo(.125f));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestCase("white")]
    [TestCase("black")]
    [TestCase("gray")]
    [TestCase("grey")]
    [TestCase("bump")]
    [TestCase("red")]
    [TestCase("")]
    public void ResolvesTextureDefaultsAndLeavesMainTexForBlit(string textureName)
    {
        var texture = new Texture();
        var calls = new List<string>();
        var material = new Material();
        ShaderLabDefaults.Parse($$"""
            Properties {
                _MainTex ("Source", 2D) = "white" {}
                _Other ("Other", 2D) = "{{textureName}}" { }
            }
            """).Apply(material, name => { calls.Add(name); return texture; });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(new[] { textureName }));
            Assert.That(material.Textures.Keys, Is.EquivalentTo(new[] { "_Other" }));
            Assert.That(material.Textures["_Other"], Is.SameAs(texture));
        });
    }

    [Test]
    public void UnknownTexturesDoNotReplaceExistingBindings()
    {
        var texture = new Texture();
        var material = new Material();
        material.SetTexture("_Unknown", texture);
        ShaderLabDefaults.Parse("Properties { _Unknown (\"Unknown\", 2D) = \"custom\" {} }")
            .Apply(material, _ => null);
        Assert.That(material.Textures["_Unknown"], Is.SameAs(texture));
    }

    [Test]
    public void IgnoresCommentsAttributesStringsAndOutsideProperties()
    {
        var material = Apply("""
            // Properties { _Fake ("Fake", Float) = 100 }
            Shader "Properties { ignored }" {
                /* Properties { _Fake ("Fake", Float) = 100 } */
                Properties {
                    // _Line ("Line", Float) = 100
                    /* _Block ("Block", Float) = 100 */
                    [HideInInspector] [Header("Properties } // /* \"quoted\"")]
                    [Enum(First, 0, Second, 1)] _Value
                        ("Display // /* } \"quote\"", /* separator */ Range(-1, 1)) = .5
                    _Image ("Image", 2D) = "white" { _Nested ("Nested", Float) = 100 }
                }
                SubShader { Tags { "Properties" = "{ ignored }" }
                    _Outside ("Outside", Float) = 100
                }
            }
            """);
        Assert.That(material.Floats, Is.EquivalentTo(new Dictionary<string, float> { ["_Value"] = .5f }));
    }

    [Test]
    public void ParsedDefaultsCanBeAppliedIndependentlyToNewMaterials()
    {
        var defaults = ShaderLabDefaults.Parse("Properties { _Value (\"Value\", Float) = 3 }");
        var first = new Material();
        var second = new Material();
        defaults.Apply(first, _ => throw new AssertionException("Unexpected texture lookup"));
        first.SetFloat("_Value", 9);
        defaults.Apply(second, _ => throw new AssertionException("Unexpected texture lookup"));
        Assert.Multiple(() =>
        {
            Assert.That(first.Floats["_Value"], Is.EqualTo(9));
            Assert.That(second.Floats["_Value"], Is.EqualTo(3));
        });
    }

    [Test]
    public void SkipsUnsupportedOrMalformedDefaultsAndContinues()
    {
        var material = Apply("""
            Properties {
                _Cube ("Cube", Cube) = "white" {}
                _Array ("Array", 2DArray) = "white" {}
                _BadFloat ("Bad", Float) = nope
                _Infinity ("Bad", Float) = 1e999
                _Nan ("Bad", Float) = NaN
                _BadInt ("Bad", Int) = 2147483648
                _Fraction ("Bad", Integer) = 1.5
                _BadVector ("Bad", Vector) = (1, 2, 3)
                _BadRange ("Bad", Range(0, nope)) = 2
                _Valid ("Valid", Float) = 4
            }
            """);
        Assert.Multiple(() =>
        {
            Assert.That(material.Floats.Keys, Is.EquivalentTo(new[] { "_Valid" }));
            Assert.That(material.Floats["_Valid"], Is.EqualTo(4));
            Assert.That(material.Ints, Is.Empty);
            Assert.That(material.Vectors, Is.Empty);
            Assert.That(material.Textures, Is.Empty);
        });
    }

    [TestCase("")]
    [TestCase("// Properties {}")]
    [TestCase("Shader \"Test\" { SubShader { Tags { \"Properties\" = \"ignored\" } } }")]
    [TestCase("/* unterminated")]
    [TestCase("Properties { _Truncated (\"Label\", Vector) = (1, 2")]
    public void MissingOrTruncatedPropertiesAreSafe(string source)
    {
        var material = Apply(source);
        Assert.Multiple(() =>
        {
            Assert.That(material.Floats, Is.Empty);
            Assert.That(material.Ints, Is.Empty);
            Assert.That(material.Vectors, Is.Empty);
            Assert.That(material.Textures, Is.Empty);
        });
    }

    private static Material Apply(string source)
    {
        var material = new Material();
        ShaderLabDefaults.Apply(source, material, _ => null);
        return material;
    }

    private sealed class Texture : IFilterTexture
    {
        public int Width => 1;
        public int Height => 1;
    }

    private sealed class Material : IFilterMaterial
    {
        public Dictionary<string, float> Floats { get; } = new();
        public Dictionary<string, int> Ints { get; } = new();
        public Dictionary<string, Vector4> Vectors { get; } = new();
        public Dictionary<string, IFilterTexture?> Textures { get; } = new();
        public void SetFloat(string name, float value) => Floats[name] = value;
        public void SetInt(string name, int value) => Ints[name] = value;
        public void SetVector(string name, Vector4 value) => Vectors[name] = value;
        public void SetTexture(string name, IFilterTexture? value) => Textures[name] = value;
    }
}
