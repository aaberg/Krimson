using Namotion.Reflection;
using NJsonSchema.Annotations;
using NJsonSchema.Generation;
using static System.String;

namespace Krimson.Serializers.ConfluentJson.NJsonSchema;

[UsedImplicitly]
class SchemaFullNameGenerator : ISchemaNameGenerator {
    public static readonly SchemaFullNameGenerator Instance = new();
    
    public virtual string Generate(Type type) {
        var cachedType      = type.ToCachedType();
        var schemaAttribute = cachedType.GetInheritedAttribute<JsonSchemaAttribute>();

        if (!IsNullOrEmpty(schemaAttribute?.Name))
            return schemaAttribute.Name;

        if (cachedType.Type.IsClass)
            return cachedType.Type.FullName!;

        throw new InvalidOperationException("Type is not a class or record");
    }
}