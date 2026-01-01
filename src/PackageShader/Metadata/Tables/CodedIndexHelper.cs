/// <summary>
/// Helper methods for coded indexes.
/// </summary>
static class CodedIndexHelper
{
    const int TableCount = 58;

    /// <summary>
    /// Gets the size (2 or 4 bytes) of a coded index based on table row counts.
    /// </summary>
    public static int GetSize(CodedIndex codedIndex, Func<TableIndex, int> getRowCount)
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
    public static (int bits, TableIndex[] tables) GetCodedIndexInfo(CodedIndex codedIndex) =>
        codedIndex switch
        {
            CodedIndex.TypeDefOrRef => (2, [TableIndex.TypeDef, TableIndex.TypeRef, TableIndex.TypeSpec]),
            CodedIndex.HasConstant => (2, [TableIndex.Field, TableIndex.Param, TableIndex.Property]),
            CodedIndex.HasCustomAttribute => (5, [
                TableIndex.MethodDef, TableIndex.Field, TableIndex.TypeRef, TableIndex.TypeDef, TableIndex.Param,
                TableIndex.InterfaceImpl, TableIndex.MemberRef, TableIndex.Module, TableIndex.DeclSecurity,
                TableIndex.Property, TableIndex.Event, TableIndex.StandAloneSig, TableIndex.ModuleRef,
                TableIndex.TypeSpec, TableIndex.Assembly, TableIndex.AssemblyRef, TableIndex.File,
                TableIndex.ExportedType, TableIndex.ManifestResource, TableIndex.GenericParam,
                TableIndex.GenericParamConstraint, TableIndex.MethodSpec
            ]),
            CodedIndex.HasFieldMarshal => (1, [TableIndex.Field, TableIndex.Param]),
            CodedIndex.HasDeclSecurity => (2, [TableIndex.TypeDef, TableIndex.MethodDef, TableIndex.Assembly]),
            CodedIndex.MemberRefParent => (3, [TableIndex.TypeDef, TableIndex.TypeRef, TableIndex.ModuleRef, TableIndex.MethodDef, TableIndex.TypeSpec]),
            CodedIndex.HasSemantics => (1, [TableIndex.Event, TableIndex.Property]),
            CodedIndex.MethodDefOrRef => (1, [TableIndex.MethodDef, TableIndex.MemberRef]),
            CodedIndex.MemberForwarded => (1, [TableIndex.Field, TableIndex.MethodDef]),
            CodedIndex.Implementation => (2, [TableIndex.File, TableIndex.AssemblyRef, TableIndex.ExportedType]),
            // CustomAttributeType uses tags 2 and 3 (not 0 and 1) per ECMA-335 II.24.2.6
            CodedIndex.CustomAttributeType => (3, [(TableIndex)0xFF, (TableIndex)0xFF, TableIndex.MethodDef, TableIndex.MemberRef]),
            CodedIndex.ResolutionScope => (2, [TableIndex.Module, TableIndex.ModuleRef, TableIndex.AssemblyRef, TableIndex.TypeRef]),
            CodedIndex.TypeOrMethodDef => (1, [TableIndex.TypeDef, TableIndex.MethodDef]),
            CodedIndex.HasCustomDebugInformation => (5, [
                TableIndex.MethodDef, TableIndex.Field, TableIndex.TypeRef, TableIndex.TypeDef, TableIndex.Param,
                TableIndex.InterfaceImpl, TableIndex.MemberRef, TableIndex.Module, TableIndex.DeclSecurity,
                TableIndex.Property, TableIndex.Event, TableIndex.StandAloneSig, TableIndex.ModuleRef,
                TableIndex.TypeSpec, TableIndex.Assembly, TableIndex.AssemblyRef, TableIndex.File,
                TableIndex.ExportedType, TableIndex.ManifestResource, TableIndex.GenericParam,
                TableIndex.GenericParamConstraint, TableIndex.MethodSpec, TableIndex.Document,
                TableIndex.LocalScope, TableIndex.LocalVariable, TableIndex.LocalConstant, TableIndex.ImportScope
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

        return new(table, rid);
    }

    /// <summary>
    /// Encodes a metadata token to a coded index value.
    /// </summary>
    public static uint EncodeToken(CodedIndex codedIndex, MetadataToken token)
    {
        if (token.RID == 0)
            return 0;

        var (bits, tables) = GetCodedIndexInfo(codedIndex);

        for (int i = 0; i < tables.Length; i++)
        {
            if (tables[i] == token.TableIndex)
                return (token.RID << bits) | (uint)i;
        }

        throw new ArgumentException($"Table {token.TableIndex} not valid for coded index {codedIndex}");
    }
}