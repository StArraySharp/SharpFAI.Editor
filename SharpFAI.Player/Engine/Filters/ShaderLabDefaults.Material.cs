using System.Numerics;

namespace SharpFAI.Player.Engine.Filters;

internal sealed partial class ShaderLabDefaults
{
    internal static void Apply(string shaderLab, IFilterMaterial material,
        Func<string, IFilterTexture?> builtInTexture) => Parse(shaderLab).Apply(material, builtInTexture);

    internal void Apply(IFilterMaterial material, Func<string, IFilterTexture?> builtInTexture)
    {
        foreach (var property in Properties)
        {
            switch (property.Type)
            {
                case "Float":
                case "Range": material.SetFloat(property.Name, property.Scalar); break;
                case "Int":
                case "Integer": material.SetInt(property.Name, property.Integer); break;
                case "Vector":
                case "Color":
                    var (x, y, z, w) = property.Vector;
                    material.SetVector(property.Name, new Vector4(x, y, z, w));
                    break;
                case "2D":
                    if (builtInTexture(property.Texture!) is { } texture)
                        material.SetTexture(property.Name, texture);
                    break;
            }
        }
    }
}
