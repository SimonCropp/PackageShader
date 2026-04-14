/// <summary>
/// Column kind in a metadata table row. Heap and table-index columns have variable widths
/// (2 or 4 bytes) determined at runtime from the heap/row counts; fixed-width columns are
/// always the same size.
/// </summary>
enum ColumnKind : byte
{
    UInt16,
    UInt32,
    StringIdx,
    BlobIdx,
    GuidIdx,
    TableIdx,
    CodedIdx
}

/// <summary>
/// One column in a metadata table row. <see cref="Param"/> stores the <see cref="TableIndex"/>
/// for <see cref="ColumnKind.TableIdx"/> or the <see cref="CodedIndex"/> for
/// <see cref="ColumnKind.CodedIdx"/>; it is unused for the other kinds.
/// </summary>
readonly struct ColumnSpec(ColumnKind kind, byte param)
{
    public ColumnKind Kind { get; } = kind;
    public byte Param { get; } = param;

    public static ColumnSpec U16 => new(ColumnKind.UInt16, 0);
    public static ColumnSpec U32 => new(ColumnKind.UInt32, 0);
    public static ColumnSpec Str => new(ColumnKind.StringIdx, 0);
    public static ColumnSpec Blob => new(ColumnKind.BlobIdx, 0);
    public static ColumnSpec Guid => new(ColumnKind.GuidIdx, 0);
    public static ColumnSpec Tbl(TableIndex t) => new(ColumnKind.TableIdx, (byte)t);
    public static ColumnSpec Coded(CodedIndex c) => new(ColumnKind.CodedIdx, (byte)c);
}

/// <summary>
/// Per-table column lists, mirroring the size formulas in
/// <see cref="StreamingMetadataReader.ComputeRowSize"/>. Used by the generic table rewriter
/// in <see cref="StreamingMetadataWriter"/> when string or blob heap indices promote from
/// 2 to 4 bytes and rows must be re-emitted with the wider widths.
/// </summary>
static class TableSchema
{
    static readonly ColumnSpec[] empty = [];

    static readonly ColumnSpec[] module = [ColumnSpec.U16, ColumnSpec.Str, ColumnSpec.Guid, ColumnSpec.Guid, ColumnSpec.Guid];
    static readonly ColumnSpec[] typeRef = [ColumnSpec.Coded(CodedIndex.ResolutionScope), ColumnSpec.Str, ColumnSpec.Str];
    static readonly ColumnSpec[] typeDef = [ColumnSpec.U32, ColumnSpec.Str, ColumnSpec.Str, ColumnSpec.Coded(CodedIndex.TypeDefOrRef), ColumnSpec.Tbl(TableIndex.Field), ColumnSpec.Tbl(TableIndex.MethodDef)];
    static readonly ColumnSpec[] fieldPtr = [ColumnSpec.Tbl(TableIndex.Field)];
    static readonly ColumnSpec[] field = [ColumnSpec.U16, ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] methodPtr = [ColumnSpec.Tbl(TableIndex.MethodDef)];
    static readonly ColumnSpec[] methodDef = [ColumnSpec.U32, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.Str, ColumnSpec.Blob, ColumnSpec.Tbl(TableIndex.Param)];
    static readonly ColumnSpec[] paramPtr = [ColumnSpec.Tbl(TableIndex.Param)];
    static readonly ColumnSpec[] paramTbl = [ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.Str];
    static readonly ColumnSpec[] interfaceImpl = [ColumnSpec.Tbl(TableIndex.TypeDef), ColumnSpec.Coded(CodedIndex.TypeDefOrRef)];
    static readonly ColumnSpec[] memberRef = [ColumnSpec.Coded(CodedIndex.MemberRefParent), ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] constant = [ColumnSpec.U16, ColumnSpec.Coded(CodedIndex.HasConstant), ColumnSpec.Blob];
    static readonly ColumnSpec[] customAttribute = [ColumnSpec.Coded(CodedIndex.HasCustomAttribute), ColumnSpec.Coded(CodedIndex.CustomAttributeType), ColumnSpec.Blob];
    static readonly ColumnSpec[] fieldMarshal = [ColumnSpec.Coded(CodedIndex.HasFieldMarshal), ColumnSpec.Blob];
    static readonly ColumnSpec[] declSecurity = [ColumnSpec.U16, ColumnSpec.Coded(CodedIndex.HasDeclSecurity), ColumnSpec.Blob];
    static readonly ColumnSpec[] classLayout = [ColumnSpec.U16, ColumnSpec.U32, ColumnSpec.Tbl(TableIndex.TypeDef)];
    static readonly ColumnSpec[] fieldLayout = [ColumnSpec.U32, ColumnSpec.Tbl(TableIndex.Field)];
    static readonly ColumnSpec[] standAloneSig = [ColumnSpec.Blob];
    static readonly ColumnSpec[] eventMap = [ColumnSpec.Tbl(TableIndex.TypeDef), ColumnSpec.Tbl(TableIndex.Event)];
    static readonly ColumnSpec[] eventPtr = [ColumnSpec.Tbl(TableIndex.Event)];
    static readonly ColumnSpec[] eventTbl = [ColumnSpec.U16, ColumnSpec.Str, ColumnSpec.Coded(CodedIndex.TypeDefOrRef)];
    static readonly ColumnSpec[] propertyMap = [ColumnSpec.Tbl(TableIndex.TypeDef), ColumnSpec.Tbl(TableIndex.Property)];
    static readonly ColumnSpec[] propertyPtr = [ColumnSpec.Tbl(TableIndex.Property)];
    static readonly ColumnSpec[] property = [ColumnSpec.U16, ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] methodSemantics = [ColumnSpec.U16, ColumnSpec.Tbl(TableIndex.MethodDef), ColumnSpec.Coded(CodedIndex.HasSemantics)];
    static readonly ColumnSpec[] methodImpl = [ColumnSpec.Tbl(TableIndex.TypeDef), ColumnSpec.Coded(CodedIndex.MethodDefOrRef), ColumnSpec.Coded(CodedIndex.MethodDefOrRef)];
    static readonly ColumnSpec[] moduleRef = [ColumnSpec.Str];
    static readonly ColumnSpec[] typeSpec = [ColumnSpec.Blob];
    static readonly ColumnSpec[] implMap = [ColumnSpec.U16, ColumnSpec.Coded(CodedIndex.MemberForwarded), ColumnSpec.Str, ColumnSpec.Tbl(TableIndex.ModuleRef)];
    static readonly ColumnSpec[] fieldRva = [ColumnSpec.U32, ColumnSpec.Tbl(TableIndex.Field)];
    static readonly ColumnSpec[] encLog = [ColumnSpec.U32, ColumnSpec.U32];
    static readonly ColumnSpec[] encMap = [ColumnSpec.U32];
    static readonly ColumnSpec[] assembly = [ColumnSpec.U32, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U32, ColumnSpec.Blob, ColumnSpec.Str, ColumnSpec.Str];
    static readonly ColumnSpec[] assemblyProcessor = [ColumnSpec.U32];
    static readonly ColumnSpec[] assemblyOS = [ColumnSpec.U32, ColumnSpec.U32, ColumnSpec.U32];
    static readonly ColumnSpec[] assemblyRef = [ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.U32, ColumnSpec.Blob, ColumnSpec.Str, ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] assemblyRefProcessor = [ColumnSpec.U32, ColumnSpec.Tbl(TableIndex.AssemblyRef)];
    static readonly ColumnSpec[] assemblyRefOS = [ColumnSpec.U32, ColumnSpec.U32, ColumnSpec.U32, ColumnSpec.Tbl(TableIndex.AssemblyRef)];
    static readonly ColumnSpec[] file = [ColumnSpec.U32, ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] exportedType = [ColumnSpec.U32, ColumnSpec.U32, ColumnSpec.Str, ColumnSpec.Str, ColumnSpec.Coded(CodedIndex.Implementation)];
    static readonly ColumnSpec[] manifestResource = [ColumnSpec.U32, ColumnSpec.U32, ColumnSpec.Str, ColumnSpec.Coded(CodedIndex.Implementation)];
    static readonly ColumnSpec[] nestedClass = [ColumnSpec.Tbl(TableIndex.TypeDef), ColumnSpec.Tbl(TableIndex.TypeDef)];
    static readonly ColumnSpec[] genericParam = [ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.Coded(CodedIndex.TypeOrMethodDef), ColumnSpec.Str];
    static readonly ColumnSpec[] methodSpec = [ColumnSpec.Coded(CodedIndex.MethodDefOrRef), ColumnSpec.Blob];
    static readonly ColumnSpec[] genericParamConstraint = [ColumnSpec.Tbl(TableIndex.GenericParam), ColumnSpec.Coded(CodedIndex.TypeDefOrRef)];
    static readonly ColumnSpec[] document = [ColumnSpec.Blob, ColumnSpec.Guid, ColumnSpec.Blob, ColumnSpec.Guid];
    static readonly ColumnSpec[] methodDebugInformation = [ColumnSpec.Tbl(TableIndex.Document), ColumnSpec.Blob];
    static readonly ColumnSpec[] localScope = [ColumnSpec.Tbl(TableIndex.MethodDef), ColumnSpec.Tbl(TableIndex.ImportScope), ColumnSpec.Tbl(TableIndex.LocalVariable), ColumnSpec.Tbl(TableIndex.LocalConstant), ColumnSpec.U32, ColumnSpec.U32];
    static readonly ColumnSpec[] localVariable = [ColumnSpec.U16, ColumnSpec.U16, ColumnSpec.Str];
    static readonly ColumnSpec[] localConstant = [ColumnSpec.Str, ColumnSpec.Blob];
    static readonly ColumnSpec[] importScope = [ColumnSpec.Tbl(TableIndex.ImportScope), ColumnSpec.Blob];
    static readonly ColumnSpec[] stateMachineMethod = [ColumnSpec.Tbl(TableIndex.MethodDef), ColumnSpec.Tbl(TableIndex.MethodDef)];
    static readonly ColumnSpec[] customDebugInformation = [ColumnSpec.Coded(CodedIndex.HasCustomDebugInformation), ColumnSpec.Guid, ColumnSpec.Blob];

    public static ColumnSpec[] GetColumns(TableIndex table) =>
        table switch
        {
            TableIndex.Module => module,
            TableIndex.TypeRef => typeRef,
            TableIndex.TypeDef => typeDef,
            TableIndex.FieldPtr => fieldPtr,
            TableIndex.Field => field,
            TableIndex.MethodPtr => methodPtr,
            TableIndex.MethodDef => methodDef,
            TableIndex.ParamPtr => paramPtr,
            TableIndex.Param => paramTbl,
            TableIndex.InterfaceImpl => interfaceImpl,
            TableIndex.MemberRef => memberRef,
            TableIndex.Constant => constant,
            TableIndex.CustomAttribute => customAttribute,
            TableIndex.FieldMarshal => fieldMarshal,
            TableIndex.DeclSecurity => declSecurity,
            TableIndex.ClassLayout => classLayout,
            TableIndex.FieldLayout => fieldLayout,
            TableIndex.StandAloneSig => standAloneSig,
            TableIndex.EventMap => eventMap,
            TableIndex.EventPtr => eventPtr,
            TableIndex.Event => eventTbl,
            TableIndex.PropertyMap => propertyMap,
            TableIndex.PropertyPtr => propertyPtr,
            TableIndex.Property => property,
            TableIndex.MethodSemantics => methodSemantics,
            TableIndex.MethodImpl => methodImpl,
            TableIndex.ModuleRef => moduleRef,
            TableIndex.TypeSpec => typeSpec,
            TableIndex.ImplMap => implMap,
            TableIndex.FieldRva => fieldRva,
            TableIndex.EncLog => encLog,
            TableIndex.EncMap => encMap,
            TableIndex.Assembly => assembly,
            TableIndex.AssemblyProcessor => assemblyProcessor,
            TableIndex.AssemblyOS => assemblyOS,
            TableIndex.AssemblyRef => assemblyRef,
            TableIndex.AssemblyRefProcessor => assemblyRefProcessor,
            TableIndex.AssemblyRefOS => assemblyRefOS,
            TableIndex.File => file,
            TableIndex.ExportedType => exportedType,
            TableIndex.ManifestResource => manifestResource,
            TableIndex.NestedClass => nestedClass,
            TableIndex.GenericParam => genericParam,
            TableIndex.MethodSpec => methodSpec,
            TableIndex.GenericParamConstraint => genericParamConstraint,
            TableIndex.Document => document,
            TableIndex.MethodDebugInformation => methodDebugInformation,
            TableIndex.LocalScope => localScope,
            TableIndex.LocalVariable => localVariable,
            TableIndex.LocalConstant => localConstant,
            TableIndex.ImportScope => importScope,
            TableIndex.StateMachineMethod => stateMachineMethod,
            TableIndex.CustomDebugInformation => customDebugInformation,
            _ => empty
        };
}
