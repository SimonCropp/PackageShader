static class MetadataHelper
{
    public static List<string> GetInternalVisibleToAttributes(MetadataReader reader)
    {
        var results = new List<string>();
        var provider = new SimpleTypeProvider();

        foreach (var attrHandle in reader.GetCustomAttributes(EntityHandle.AssemblyDefinition))
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var typeName = GetAttributeTypeName(reader, attr);

            if (typeName.EndsWith("InternalsVisibleToAttribute"))
            {
                var decoded = attr.DecodeValue(provider);
                if (decoded.FixedArguments.Length > 0 &&
                    decoded.FixedArguments[0].Value is string assemblyName)
                {
                    results.Add(assemblyName);
                }
            }
        }

        results.Sort();
        return results;
    }

    static string GetAttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = reader.GetMemberReference((MemberReferenceHandle) attr.Constructor);
            if (memberRef.Parent.Kind == HandleKind.TypeReference)
            {
                var typeRef = reader.GetTypeReference((TypeReferenceHandle) memberRef.Parent);
                return reader.GetString(typeRef.Name);
            }
        }

        return string.Empty;
    }

    class SimpleTypeProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public object? GetSystemType() => null;
        public object? GetSZArrayType(object? elementType) => null;
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
        public object? GetTypeFromSerializedName(string name) => null;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object? type) => false;
    }

    public static string FormatAssemblyName(MetadataReader reader)
    {
        var assemblyDef = reader.GetAssemblyDefinition();
        var name = reader.GetString(assemblyDef.Name);
        var version = assemblyDef.Version;
        var culture = reader.GetString(assemblyDef.Culture);
        var cultureStr = string.IsNullOrEmpty(culture) ? "neutral" : culture;
        var publicKey = reader.GetBlobBytes(assemblyDef.PublicKey);
        var tokenStr = FormatPublicKeyToken(publicKey);

        return $"{name}, Version={version}, Culture={cultureStr}, PublicKeyToken={tokenStr}";
    }

    public static string FormatAssemblyRefName(MetadataReader reader, AssemblyReference assemblyRef)
    {
        var name = reader.GetString(assemblyRef.Name);
        var version = assemblyRef.Version;
        var culture = reader.GetString(assemblyRef.Culture);
        var cultureStr = string.IsNullOrEmpty(culture) ? "neutral" : culture;
        var publicKeyOrToken = reader.GetBlobBytes(assemblyRef.PublicKeyOrToken);
        var tokenStr = publicKeyOrToken.Length == 8
            ? BitConverter.ToString(publicKeyOrToken).Replace("-", "").ToLowerInvariant()
            : FormatPublicKeyToken(publicKeyOrToken);

        return $"{name}, Version={version}, Culture={cultureStr}, PublicKeyToken={tokenStr}";
    }

    public static string FormatPublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return "null";
        }
        var hash = SHA1.HashData(publicKey);

        // Token is last 8 bytes reversed
        var token = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            token[i] = hash[hash.Length - 1 - i];
        }

        return BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
    }

    public static List<string> GetAssemblyReferences(MetadataReader reader) =>
        reader.AssemblyReferences
            .Select(reader.GetAssemblyReference)
            .Select(_ => FormatAssemblyRefName(reader, _))
            .OrderBy(_ => _)
            .ToList();
}