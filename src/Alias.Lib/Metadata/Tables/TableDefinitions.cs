namespace Alias.Lib.Metadata.Tables;

/// <summary>
/// Metadata table identifiers.
/// </summary>
public enum Table : byte
{
    Module = 0x00,
    TypeRef = 0x01,
    TypeDef = 0x02,
    FieldPtr = 0x03,
    Field = 0x04,
    MethodPtr = 0x05,
    Method = 0x06,
    ParamPtr = 0x07,
    Param = 0x08,
    InterfaceImpl = 0x09,
    MemberRef = 0x0a,
    Constant = 0x0b,
    CustomAttribute = 0x0c,
    FieldMarshal = 0x0d,
    DeclSecurity = 0x0e,
    ClassLayout = 0x0f,
    FieldLayout = 0x10,
    StandAloneSig = 0x11,
    EventMap = 0x12,
    EventPtr = 0x13,
    Event = 0x14,
    PropertyMap = 0x15,
    PropertyPtr = 0x16,
    Property = 0x17,
    MethodSemantics = 0x18,
    MethodImpl = 0x19,
    ModuleRef = 0x1a,
    TypeSpec = 0x1b,
    ImplMap = 0x1c,
    FieldRVA = 0x1d,
    EncLog = 0x1e,
    EncMap = 0x1f,
    Assembly = 0x20,
    AssemblyProcessor = 0x21,
    AssemblyOS = 0x22,
    AssemblyRef = 0x23,
    AssemblyRefProcessor = 0x24,
    AssemblyRefOS = 0x25,
    File = 0x26,
    ExportedType = 0x27,
    ManifestResource = 0x28,
    NestedClass = 0x29,
    GenericParam = 0x2a,
    MethodSpec = 0x2b,
    GenericParamConstraint = 0x2c,
    // Portable PDB tables
    Document = 0x30,
    MethodDebugInformation = 0x31,
    LocalScope = 0x32,
    LocalVariable = 0x33,
    LocalConstant = 0x34,
    ImportScope = 0x35,
    StateMachineMethod = 0x36,
    CustomDebugInformation = 0x37,
}

/// <summary>
/// Coded index types for compact metadata references.
/// </summary>
public enum CodedIndex
{
    TypeDefOrRef,
    HasConstant,
    HasCustomAttribute,
    HasFieldMarshal,
    HasDeclSecurity,
    MemberRefParent,
    HasSemantics,
    MethodDefOrRef,
    MemberForwarded,
    Implementation,
    CustomAttributeType,
    ResolutionScope,
    TypeOrMethodDef,
    HasCustomDebugInformation,
}

/// <summary>
/// Token types for metadata tokens.
/// </summary>
public enum TokenType : uint
{
    Module = 0x00000000,
    TypeRef = 0x01000000,
    TypeDef = 0x02000000,
    Field = 0x04000000,
    Method = 0x06000000,
    Param = 0x08000000,
    InterfaceImpl = 0x09000000,
    MemberRef = 0x0a000000,
    CustomAttribute = 0x0c000000,
    Permission = 0x0e000000,
    Signature = 0x11000000,
    Event = 0x14000000,
    Property = 0x17000000,
    ModuleRef = 0x1a000000,
    TypeSpec = 0x1b000000,
    Assembly = 0x20000000,
    AssemblyRef = 0x23000000,
    File = 0x26000000,
    ExportedType = 0x27000000,
    ManifestResource = 0x28000000,
    GenericParam = 0x2a000000,
    MethodSpec = 0x2b000000,
    GenericParamConstraint = 0x2c000000,
    Document = 0x30000000,
    LocalScope = 0x32000000,
    LocalVariable = 0x33000000,
    LocalConstant = 0x34000000,
    ImportScope = 0x35000000,
    String = 0x70000000,
}

/// <summary>
/// Information about a metadata table.
/// </summary>
public struct TableInfo
{
    public uint Offset;
    public uint RowCount;
    public uint RowSize;

    public bool IsLarge => RowCount > ushort.MaxValue;
}

/// <summary>
/// A metadata token combining table type and row id.
/// </summary>
public readonly struct MetadataToken
{
    public readonly uint Value;

    public MetadataToken(uint value) => Value = value;

    public MetadataToken(TokenType type, uint rid) => Value = (uint)type | rid;

    public uint RID => Value & 0x00ffffff;
    public TokenType TokenType => (TokenType)(Value & 0xff000000);

    public static MetadataToken Zero => new(0);

    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>
/// Helper methods for coded indexes.
/// </summary>
public static class CodedIndexHelper
{
    private const int TableCount = 58;

    /// <summary>
    /// Gets the size (2 or 4 bytes) of a coded index based on table row counts.
    /// </summary>
    public static int GetSize(CodedIndex codedIndex, Func<Table, int> getRowCount)
    {
        var (bits, tables) = GetCodedIndexInfo(codedIndex);

        int max = 0;
        foreach (var table in tables)
        {
            // Skip placeholder/unused table entries (0xFF)
            if ((byte)table == 0xFF) continue;
            max = Math.Max(getRowCount(table), max);
        }

        return max < 1 << (16 - bits) ? 2 : 4;
    }

    /// <summary>
    /// Gets bit count and tables for a coded index type.
    /// </summary>
    public static (int bits, Table[] tables) GetCodedIndexInfo(CodedIndex codedIndex) =>
        codedIndex switch
        {
            CodedIndex.TypeDefOrRef => (2, [Table.TypeDef, Table.TypeRef, Table.TypeSpec]),
            CodedIndex.HasConstant => (2, [Table.Field, Table.Param, Table.Property]),
            CodedIndex.HasCustomAttribute => (5, [
                Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param,
                Table.InterfaceImpl, Table.MemberRef, Table.Module, Table.DeclSecurity,
                Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File,
                Table.ExportedType, Table.ManifestResource, Table.GenericParam,
                Table.GenericParamConstraint, Table.MethodSpec
            ]),
            CodedIndex.HasFieldMarshal => (1, [Table.Field, Table.Param]),
            CodedIndex.HasDeclSecurity => (2, [Table.TypeDef, Table.Method, Table.Assembly]),
            CodedIndex.MemberRefParent => (3, [Table.TypeDef, Table.TypeRef, Table.ModuleRef, Table.Method, Table.TypeSpec]),
            CodedIndex.HasSemantics => (1, [Table.Event, Table.Property]),
            CodedIndex.MethodDefOrRef => (1, [Table.Method, Table.MemberRef]),
            CodedIndex.MemberForwarded => (1, [Table.Field, Table.Method]),
            CodedIndex.Implementation => (2, [Table.File, Table.AssemblyRef, Table.ExportedType]),
            // CustomAttributeType uses tags 2 and 3 (not 0 and 1) per ECMA-335 II.24.2.6
            CodedIndex.CustomAttributeType => (3, [(Table)0xFF, (Table)0xFF, Table.Method, Table.MemberRef]),
            CodedIndex.ResolutionScope => (2, [Table.Module, Table.ModuleRef, Table.AssemblyRef, Table.TypeRef]),
            CodedIndex.TypeOrMethodDef => (1, [Table.TypeDef, Table.Method]),
            CodedIndex.HasCustomDebugInformation => (5, [
                Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param,
                Table.InterfaceImpl, Table.MemberRef, Table.Module, Table.DeclSecurity,
                Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File,
                Table.ExportedType, Table.ManifestResource, Table.GenericParam,
                Table.GenericParamConstraint, Table.MethodSpec, Table.Document,
                Table.LocalScope, Table.LocalVariable, Table.LocalConstant, Table.ImportScope
            ]),
            _ => throw new ArgumentException($"Unknown coded index: {codedIndex}")
        };

    /// <summary>
    /// Decodes a coded index value to a metadata token.
    /// </summary>
    public static MetadataToken DecodeToken(CodedIndex codedIndex, uint data)
    {
        var (bits, tables) = GetCodedIndexInfo(codedIndex);
        var mask = (1u << bits) - 1;
        var tableIndex = (int)(data & mask);
        var rid = data >> bits;

        if (tableIndex >= tables.Length)
            return MetadataToken.Zero;

        var table = tables[tableIndex];

        // Check for placeholder/unused table entries (0xFF)
        if ((byte)table == 0xFF)
            return MetadataToken.Zero;

        var tokenType = TableToTokenType(table);
        return new(tokenType, rid);
    }

    /// <summary>
    /// Encodes a metadata token to a coded index value.
    /// </summary>
    public static uint EncodeToken(CodedIndex codedIndex, MetadataToken token)
    {
        if (token.RID == 0)
            return 0;

        var (bits, tables) = GetCodedIndexInfo(codedIndex);
        var table = TokenTypeToTable(token.TokenType);

        for (int i = 0; i < tables.Length; i++)
        {
            if (tables[i] == table)
                return (token.RID << bits) | (uint)i;
        }

        throw new ArgumentException($"Token type {token.TokenType} not valid for coded index {codedIndex}");
    }

    private static TokenType TableToTokenType(Table table) =>
        table switch
        {
            Table.Module => TokenType.Module,
            Table.TypeRef => TokenType.TypeRef,
            Table.TypeDef => TokenType.TypeDef,
            Table.Field => TokenType.Field,
            Table.Method => TokenType.Method,
            Table.Param => TokenType.Param,
            Table.InterfaceImpl => TokenType.InterfaceImpl,
            Table.MemberRef => TokenType.MemberRef,
            Table.CustomAttribute => TokenType.CustomAttribute,
            Table.DeclSecurity => TokenType.Permission,
            Table.StandAloneSig => TokenType.Signature,
            Table.Event => TokenType.Event,
            Table.Property => TokenType.Property,
            Table.ModuleRef => TokenType.ModuleRef,
            Table.TypeSpec => TokenType.TypeSpec,
            Table.Assembly => TokenType.Assembly,
            Table.AssemblyRef => TokenType.AssemblyRef,
            Table.File => TokenType.File,
            Table.ExportedType => TokenType.ExportedType,
            Table.ManifestResource => TokenType.ManifestResource,
            Table.GenericParam => TokenType.GenericParam,
            Table.MethodSpec => TokenType.MethodSpec,
            Table.GenericParamConstraint => TokenType.GenericParamConstraint,
            Table.Document => TokenType.Document,
            Table.LocalScope => TokenType.LocalScope,
            Table.LocalVariable => TokenType.LocalVariable,
            Table.LocalConstant => TokenType.LocalConstant,
            Table.ImportScope => TokenType.ImportScope,
            _ => throw new ArgumentException($"No token type for table {table}")
        };

    private static Table TokenTypeToTable(TokenType tokenType) =>
        tokenType switch
        {
            TokenType.Module => Table.Module,
            TokenType.TypeRef => Table.TypeRef,
            TokenType.TypeDef => Table.TypeDef,
            TokenType.Field => Table.Field,
            TokenType.Method => Table.Method,
            TokenType.Param => Table.Param,
            TokenType.InterfaceImpl => Table.InterfaceImpl,
            TokenType.MemberRef => Table.MemberRef,
            TokenType.CustomAttribute => Table.CustomAttribute,
            TokenType.Permission => Table.DeclSecurity,
            TokenType.Signature => Table.StandAloneSig,
            TokenType.Event => Table.Event,
            TokenType.Property => Table.Property,
            TokenType.ModuleRef => Table.ModuleRef,
            TokenType.TypeSpec => Table.TypeSpec,
            TokenType.Assembly => Table.Assembly,
            TokenType.AssemblyRef => Table.AssemblyRef,
            TokenType.File => Table.File,
            TokenType.ExportedType => Table.ExportedType,
            TokenType.ManifestResource => Table.ManifestResource,
            TokenType.GenericParam => Table.GenericParam,
            TokenType.MethodSpec => Table.MethodSpec,
            TokenType.GenericParamConstraint => Table.GenericParamConstraint,
            TokenType.Document => Table.Document,
            TokenType.LocalScope => Table.LocalScope,
            TokenType.LocalVariable => Table.LocalVariable,
            TokenType.LocalConstant => Table.LocalConstant,
            TokenType.ImportScope => Table.ImportScope,
            _ => throw new ArgumentException($"No table for token type {tokenType}")
        };
}
