using Alias.Lib.Metadata.Tables;
using Alias.Lib.PE;

namespace Alias.Lib.Metadata;

/// <summary>
/// Reads and provides access to metadata from a PE image.
/// </summary>
public sealed class MetadataReader
{
    private readonly PEImage _image;
    private readonly byte[] _metadataData;

    public StringHeap StringHeap { get; }
    public BlobHeap BlobHeap { get; }
    public GuidHeap GuidHeap { get; }
    public TableHeap TableHeap { get; }

    /// <summary>
    /// Raw metadata section data.
    /// </summary>
    public byte[] MetadataData => _metadataData;

    /// <summary>
    /// The PE image this metadata belongs to.
    /// </summary>
    public PEImage Image => _image;

    private MetadataReader(PEImage image, byte[] metadataData,
        StringHeap stringHeap, BlobHeap blobHeap, GuidHeap guidHeap, TableHeap tableHeap)
    {
        _image = image;
        _metadataData = metadataData;
        StringHeap = stringHeap;
        BlobHeap = blobHeap;
        GuidHeap = guidHeap;
        TableHeap = tableHeap;
    }

    /// <summary>
    /// Reads metadata from a PE image.
    /// </summary>
    public static MetadataReader Read(PEImage image)
    {
        // Get the metadata section
        var metadataSection = image.GetSectionAtRva(image.MetadataRva)
            ?? throw new BadImageFormatException("Metadata section not found");

        // Calculate offset within section
        var metadataOffset = image.MetadataRva - metadataSection.VirtualAddress;

        // Extract metadata data
        var metadataData = new byte[image.MetadataSize];
        Array.Copy(metadataSection.Data, metadataOffset, metadataData, 0, image.MetadataSize);

        // Parse stream headers to find heap locations
        StringHeap? stringHeap = null;
        BlobHeap? blobHeap = null;
        GuidHeap? guidHeap = null;
        TableHeap? tableHeap = null;

        foreach (var header in image.StreamHeaders)
        {
            var heapData = new byte[header.Size];
            Array.Copy(metadataData, header.Offset, heapData, 0, header.Size);

            switch (header.Name)
            {
                case "#Strings":
                    stringHeap = new(heapData);
                    break;
                case "#Blob":
                    blobHeap = new(heapData);
                    break;
                case "#GUID":
                    guidHeap = new(heapData);
                    break;
                case "#~":
                case "#-":
                    tableHeap = new(heapData);
                    break;
            }
        }

        stringHeap ??= new([0]);
        blobHeap ??= new([0]);
        guidHeap ??= new([]);
        if (tableHeap == null)
            throw new BadImageFormatException("Table heap not found");

        // Parse table heap
        tableHeap.Parse();

        // Set index sizes based on heap sizes
        stringHeap.IndexSize = tableHeap.StringIndexSize;
        blobHeap.IndexSize = tableHeap.BlobIndexSize;
        guidHeap.IndexSize = tableHeap.GuidIndexSize;

        return new(image, metadataData, stringHeap, blobHeap, guidHeap, tableHeap);
    }

    #region Assembly Table

    /// <summary>
    /// Gets the full assembly name (e.g., "Name, Version=1.0.0.0, Culture=neutral, PublicKeyToken=...").
    /// </summary>
    public string GetAssemblyName()
    {
        if (!TableHeap.HasTable(Table.Assembly))
            return string.Empty;

        var row = ReadAssemblyRow(1);
        var name = StringHeap.Read(row.NameIndex);
        var culture = StringHeap.Read(row.CultureIndex);
        var publicKey = BlobHeap.Read(row.PublicKeyIndex);

        return FormatAssemblyName(name, row.MajorVersion, row.MinorVersion,
            row.BuildNumber, row.RevisionNumber, culture, ComputePublicKeyToken(publicKey));
    }

    /// <summary>
    /// Gets just the simple assembly name.
    /// </summary>
    public string? GetSimpleAssemblyName()
    {
        if (!TableHeap.HasTable(Table.Assembly))
            return null;

        var row = ReadAssemblyRow(1);
        return StringHeap.Read(row.NameIndex);
    }

    private static string FormatAssemblyName(string name, ushort major, ushort minor,
        ushort build, ushort revision, string culture, byte[] publicKeyToken)
    {
        var cultureStr = string.IsNullOrEmpty(culture) ? "neutral" : culture;
        var tokenStr = publicKeyToken.Length > 0
            ? BitConverter.ToString(publicKeyToken).Replace("-", "").ToLowerInvariant()
            : "null";

        return $"{name}, Version={major}.{minor}.{build}.{revision}, Culture={cultureStr}, PublicKeyToken={tokenStr}";
    }

    private static byte[] ComputePublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
            return [];

        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);

        // Token is last 8 bytes reversed
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];
        return token;
    }

    /// <summary>
    /// Gets the assembly public key.
    /// </summary>
    public byte[] GetAssemblyPublicKey()
    {
        if (!TableHeap.HasTable(Table.Assembly))
            return [];

        var row = ReadAssemblyRow(1);
        return BlobHeap.Read(row.PublicKeyIndex);
    }

    /// <summary>
    /// Reads an assembly row.
    /// </summary>
    public AssemblyRow ReadAssemblyRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.Assembly, rid);
        return AssemblyRow.Read(data, TableHeap.BlobIndexSize, TableHeap.StringIndexSize);
    }

    #endregion

    #region AssemblyRef Table

    /// <summary>
    /// Reads an assembly reference row.
    /// </summary>
    public AssemblyRefRow ReadAssemblyRefRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.AssemblyRef, rid);
        return AssemblyRefRow.Read(data, TableHeap.BlobIndexSize, TableHeap.StringIndexSize);
    }

    /// <summary>
    /// Finds an assembly reference by name.
    /// </summary>
    public (uint rid, AssemblyRefRow row)? FindAssemblyRef(string name)
    {
        var count = TableHeap.GetRowCount(Table.AssemblyRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadAssemblyRefRow(i);
            var refName = StringHeap.Read(row.NameIndex);
            if (string.Equals(refName, name, StringComparison.OrdinalIgnoreCase))
                return (i, row);
        }
        return null;
    }

    #endregion

    #region TypeDef Table

    /// <summary>
    /// Gets all type definitions.
    /// </summary>
    public IEnumerable<(uint rid, string name, string ns)> GetTypeDefs()
    {
        var count = TableHeap.GetRowCount(Table.TypeDef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadTypeDefRow(i);
            var name = StringHeap.Read(row.NameIndex);
            var ns = StringHeap.Read(row.NamespaceIndex);
            yield return (i, name, ns);
        }
    }

    /// <summary>
    /// Reads a type definition row.
    /// </summary>
    public TypeDefRow ReadTypeDefRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.TypeDef, rid);
        return TypeDefRow.Read(data,
            TableHeap.StringIndexSize,
            TableHeap.GetCodedIndexSize(CodedIndex.TypeDefOrRef),
            TableHeap.GetTableIndexSize(Table.Field),
            TableHeap.GetTableIndexSize(Table.Method));
    }

    #endregion

    #region CustomAttribute Table

    /// <summary>
    /// Gets all custom attributes.
    /// </summary>
    public IEnumerable<(uint rid, CustomAttributeRow row)> GetCustomAttributes()
    {
        var count = TableHeap.GetRowCount(Table.CustomAttribute);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadCustomAttributeRow(i);
            yield return (i, row);
        }
    }

    /// <summary>
    /// Reads a custom attribute row.
    /// </summary>
    public CustomAttributeRow ReadCustomAttributeRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.CustomAttribute, rid);
        return CustomAttributeRow.Read(data,
            TableHeap.GetCodedIndexSize(CodedIndex.HasCustomAttribute),
            TableHeap.GetCodedIndexSize(CodedIndex.CustomAttributeType),
            TableHeap.BlobIndexSize);
    }

    #endregion

    #region TypeRef Table

    /// <summary>
    /// Reads a type reference row.
    /// </summary>
    public TypeRefRow ReadTypeRefRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.TypeRef, rid);
        return TypeRefRow.Read(data,
            TableHeap.GetCodedIndexSize(CodedIndex.ResolutionScope),
            TableHeap.StringIndexSize);
    }

    /// <summary>
    /// Finds a type reference by name and namespace.
    /// </summary>
    public uint? FindTypeRef(string name, string ns)
    {
        var count = TableHeap.GetRowCount(Table.TypeRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadTypeRefRow(i);
            var rowName = StringHeap.Read(row.NameIndex);
            var rowNs = StringHeap.Read(row.NamespaceIndex);

            if (string.Equals(rowName, name, StringComparison.Ordinal) &&
                string.Equals(rowNs, ns, StringComparison.Ordinal))
                return i;
        }
        return null;
    }

    #endregion

    #region MemberRef Table

    /// <summary>
    /// Reads a member reference row.
    /// </summary>
    public MemberRefRow ReadMemberRefRow(uint rid)
    {
        var data = TableHeap.ReadRow(Table.MemberRef, rid);
        return MemberRefRow.Read(data,
            TableHeap.GetCodedIndexSize(CodedIndex.MemberRefParent),
            TableHeap.StringIndexSize,
            TableHeap.BlobIndexSize);
    }

    /// <summary>
    /// Finds a member reference by parent type RID and name.
    /// </summary>
    public uint? FindMemberRef(uint parentTypeRefRid, string name)
    {
        // Encode parent as TypeRef coded index
        var parentToken = new MetadataToken(TokenType.TypeRef, parentTypeRefRid);
        var parentEncoded = CodedIndexHelper.EncodeToken(CodedIndex.MemberRefParent, parentToken);

        var count = TableHeap.GetRowCount(Table.MemberRef);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadMemberRefRow(i);
            if (row.ClassIndex == parentEncoded)
            {
                var memberName = StringHeap.Read(row.NameIndex);
                if (string.Equals(memberName, name, StringComparison.Ordinal))
                    return i;
            }
        }
        return null;
    }

    #endregion

    #region Assembly Custom Attributes

    /// <summary>
    /// Gets custom attributes on the assembly.
    /// </summary>
    public IEnumerable<CustomAttributeInfo> GetAssemblyCustomAttributes()
    {
        // Assembly has token 0x20000001
        var assemblyToken = new MetadataToken(TokenType.Assembly, 1);
        var encodedParent = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomAttribute, assemblyToken);

        var count = TableHeap.GetRowCount(Table.CustomAttribute);
        for (uint i = 1; i <= count; i++)
        {
            var row = ReadCustomAttributeRow(i);
            if (row.ParentIndex == encodedParent)
            {
                var attrTypeName = GetCustomAttributeTypeName(row.TypeIndex);
                var argument = ParseCustomAttributeArgument(row.ValueIndex);
                yield return new(i, attrTypeName, argument);
            }
        }
    }

    private string GetCustomAttributeTypeName(uint typeIndex)
    {
        var token = CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, typeIndex);

        if (token.TokenType == TokenType.MemberRef)
        {
            var memberRow = ReadMemberRefRow(token.RID);
            var memberClassToken = CodedIndexHelper.DecodeToken(CodedIndex.MemberRefParent, memberRow.ClassIndex);

            if (memberClassToken.TokenType == TokenType.TypeRef)
            {
                var typeRefRow = ReadTypeRefRow(memberClassToken.RID);
                return StringHeap.Read(typeRefRow.NameIndex);
            }
        }

        return string.Empty;
    }

    private string ParseCustomAttributeArgument(uint valueIndex)
    {
        var blob = BlobHeap.Read(valueIndex);
        if (blob.Length < 2)
            return string.Empty;

        // Skip prolog (2 bytes: 0x01 0x00)
        if (blob[0] != 0x01 || blob[1] != 0x00)
            return string.Empty;

        // Try to read a string argument
        var offset = 2;
        if (offset >= blob.Length)
            return string.Empty;

        // Read compressed length
        int length;
        if ((blob[offset] & 0x80) == 0)
        {
            length = blob[offset];
            offset++;
        }
        else if ((blob[offset] & 0xC0) == 0x80)
        {
            if (offset + 1 >= blob.Length) return string.Empty;
            length = ((blob[offset] & 0x3F) << 8) | blob[offset + 1];
            offset += 2;
        }
        else
        {
            if (offset + 3 >= blob.Length) return string.Empty;
            length = ((blob[offset] & 0x1F) << 24) | (blob[offset + 1] << 16) |
                     (blob[offset + 2] << 8) | blob[offset + 3];
            offset += 4;
        }

        if (offset + length > blob.Length)
            return string.Empty;

        return System.Text.Encoding.UTF8.GetString(blob, offset, length);
    }

    #endregion
}

/// <summary>
/// Information about an assembly reference.
/// </summary>
public record AssemblyRefInfo(uint RID, string Name, string FullName);

/// <summary>
/// Information about a custom attribute.
/// </summary>
public record CustomAttributeInfo(uint RID, string AttributeTypeName, string ConstructorArgument);
