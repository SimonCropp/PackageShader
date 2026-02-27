public class CodedIndexHelperTests
{
    // -------------------------------------------------------------------------
    // GetSize — threshold behaviour for every coded index type
    // -------------------------------------------------------------------------

    // TypeDefOrRef: 2 tag bits → threshold = 2^(16-2) = 16384
    [Fact]
    public void GetSize_TypeDefOrRef_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.TypeDefOrRef, _ => 100));

    [Fact]
    public void GetSize_TypeDefOrRef_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.TypeDefOrRef, _ => 16384));

    // HasConstant: 2 tag bits → threshold = 16384
    [Fact]
    public void GetSize_HasConstant_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasConstant, _ => 100));

    [Fact]
    public void GetSize_HasConstant_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasConstant, _ => 16384));

    // HasCustomAttribute: 5 tag bits → threshold = 2^(16-5) = 2048
    [Fact]
    public void GetSize_HasCustomAttribute_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasCustomAttribute, _ => 100));

    [Fact]
    public void GetSize_HasCustomAttribute_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasCustomAttribute, _ => 2048));

    // HasFieldMarshal: 1 tag bit → threshold = 2^(16-1) = 32768
    [Fact]
    public void GetSize_HasFieldMarshal_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasFieldMarshal, _ => 100));

    [Fact]
    public void GetSize_HasFieldMarshal_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasFieldMarshal, _ => 32768));

    // HasDeclSecurity: 2 tag bits → threshold = 16384
    [Fact]
    public void GetSize_HasDeclSecurity_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasDeclSecurity, _ => 100));

    [Fact]
    public void GetSize_HasDeclSecurity_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasDeclSecurity, _ => 16384));

    // MemberRefParent: 3 tag bits → threshold = 2^(16-3) = 8192
    [Fact]
    public void GetSize_MemberRefParent_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.MemberRefParent, _ => 100));

    [Fact]
    public void GetSize_MemberRefParent_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.MemberRefParent, _ => 8192));

    // HasSemantics: 1 tag bit → threshold = 32768
    [Fact]
    public void GetSize_HasSemantics_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasSemantics, _ => 100));

    [Fact]
    public void GetSize_HasSemantics_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasSemantics, _ => 32768));

    // MethodDefOrRef: 1 tag bit → threshold = 32768
    [Fact]
    public void GetSize_MethodDefOrRef_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.MethodDefOrRef, _ => 100));

    [Fact]
    public void GetSize_MethodDefOrRef_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.MethodDefOrRef, _ => 32768));

    // MemberForwarded: 1 tag bit → threshold = 32768
    [Fact]
    public void GetSize_MemberForwarded_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.MemberForwarded, _ => 100));

    [Fact]
    public void GetSize_MemberForwarded_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.MemberForwarded, _ => 32768));

    // Implementation: 2 tag bits → threshold = 16384
    [Fact]
    public void GetSize_Implementation_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.Implementation, _ => 100));

    [Fact]
    public void GetSize_Implementation_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.Implementation, _ => 16384));

    // CustomAttributeType: 3 tag bits, only tags 2 and 3 used → threshold = 8192
    [Fact]
    public void GetSize_CustomAttributeType_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.CustomAttributeType, _ => 100));

    [Fact]
    public void GetSize_CustomAttributeType_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.CustomAttributeType, _ => 8192));

    // ResolutionScope: 2 tag bits → threshold = 16384
    [Fact]
    public void GetSize_ResolutionScope_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.ResolutionScope, _ => 100));

    [Fact]
    public void GetSize_ResolutionScope_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.ResolutionScope, _ => 16384));

    // TypeOrMethodDef: 1 tag bit → threshold = 32768
    [Fact]
    public void GetSize_TypeOrMethodDef_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.TypeOrMethodDef, _ => 100));

    [Fact]
    public void GetSize_TypeOrMethodDef_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.TypeOrMethodDef, _ => 32768));

    // HasCustomDebugInformation: 5 tag bits → threshold = 2048
    [Fact]
    public void GetSize_HasCustomDebugInformation_SmallTables_Returns2() =>
        Assert.Equal(2, CodedIndexHelper.GetSize(CodedIndex.HasCustomDebugInformation, _ => 100));

    [Fact]
    public void GetSize_HasCustomDebugInformation_LargeTable_Returns4() =>
        Assert.Equal(4, CodedIndexHelper.GetSize(CodedIndex.HasCustomDebugInformation, _ => 2048));

    // Unknown coded index throws
    [Fact]
    public void GetSize_UnknownCodedIndex_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.GetSize((CodedIndex)999, _ => 1));

    // -------------------------------------------------------------------------
    // GetSize — placeholder tables (0xFF) are excluded from max calculation
    // -------------------------------------------------------------------------

    // CustomAttributeType has two 0xFF placeholder entries at tag 0 and 1.
    // Even if those table indices had huge row counts they should be ignored.
    [Fact]
    public void GetSize_CustomAttributeType_PlaceholderTablesIgnored()
    {
        // Return 0 for the real tables (MethodDef, MemberRef) and a huge value
        // for Module (tag 0 placeholder) – should still be 2-byte.
        var size = CodedIndexHelper.GetSize(CodedIndex.CustomAttributeType, table =>
            table == TableIndex.Module ? 100_000 : 10);
        Assert.Equal(2, size);
    }

    // -------------------------------------------------------------------------
    // EncodeToken + DecodeToken round-trips for every coded index type
    // -------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_HasConstant_Field()
    {
        var token = new MetadataToken(TableIndex.Field, 7);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasConstant, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasConstant_Param()
    {
        var token = new MetadataToken(TableIndex.Param, 3);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasConstant, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasConstant_Property()
    {
        var token = new MetadataToken(TableIndex.Property, 12);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasConstant, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasCustomAttribute_MethodDef()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 1);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomAttribute, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasCustomAttribute, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasCustomAttribute_Assembly()
    {
        var token = new MetadataToken(TableIndex.Assembly, 1);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomAttribute, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasCustomAttribute, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasFieldMarshal_Field()
    {
        var token = new MetadataToken(TableIndex.Field, 5);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasFieldMarshal, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasFieldMarshal, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasFieldMarshal_Param()
    {
        var token = new MetadataToken(TableIndex.Param, 2);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasFieldMarshal, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasFieldMarshal, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasDeclSecurity_TypeDef()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 4);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasDeclSecurity, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasDeclSecurity, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasDeclSecurity_Assembly()
    {
        var token = new MetadataToken(TableIndex.Assembly, 1);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasDeclSecurity, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasDeclSecurity, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MemberRefParent_TypeRef()
    {
        var token = new MetadataToken(TableIndex.TypeRef, 9);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MemberRefParent, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MemberRefParent, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MemberRefParent_TypeSpec()
    {
        var token = new MetadataToken(TableIndex.TypeSpec, 2);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MemberRefParent, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MemberRefParent, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasSemantics_Event()
    {
        var token = new MetadataToken(TableIndex.Event, 6);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasSemantics, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasSemantics, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasSemantics_Property()
    {
        var token = new MetadataToken(TableIndex.Property, 3);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasSemantics, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasSemantics, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MethodDefOrRef_MethodDef()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 11);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MethodDefOrRef, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MethodDefOrRef, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MethodDefOrRef_MemberRef()
    {
        var token = new MetadataToken(TableIndex.MemberRef, 4);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MethodDefOrRef, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MethodDefOrRef, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MemberForwarded_Field()
    {
        var token = new MetadataToken(TableIndex.Field, 8);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MemberForwarded, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MemberForwarded, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_MemberForwarded_MethodDef()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 5);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.MemberForwarded, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.MemberForwarded, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_Implementation_File()
    {
        var token = new MetadataToken(TableIndex.File, 2);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.Implementation, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.Implementation, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_Implementation_AssemblyRef()
    {
        var token = new MetadataToken(TableIndex.AssemblyRef, 3);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.Implementation, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.Implementation, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_Implementation_ExportedType()
    {
        var token = new MetadataToken(TableIndex.ExportedType, 1);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.Implementation, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.Implementation, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    // CustomAttributeType uses tags 2 and 3 (not 0 and 1)
    [Fact]
    public void RoundTrip_CustomAttributeType_MethodDef()
    {
        // MethodDef is at tag index 2
        var token = new MetadataToken(TableIndex.MethodDef, 10);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_CustomAttributeType_MemberRef()
    {
        // MemberRef is at tag index 3
        var token = new MetadataToken(TableIndex.MemberRef, 7);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_TypeOrMethodDef_TypeDef()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 6);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.TypeOrMethodDef, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.TypeOrMethodDef, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_TypeOrMethodDef_MethodDef()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 2);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.TypeOrMethodDef, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.TypeOrMethodDef, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    [Fact]
    public void RoundTrip_HasCustomDebugInformation_MethodDef()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 3);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.HasCustomDebugInformation, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.HasCustomDebugInformation, encoded);
        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);
    }

    // -------------------------------------------------------------------------
    // Zero RID → encoded value is 0 for all coded index types
    // (EncodeToken checks RID == 0 before table validity)
    // -------------------------------------------------------------------------

    [Fact] public void EncodeToken_ZeroRid_HasConstant() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasConstant));
    [Fact] public void EncodeToken_ZeroRid_HasCustomAttribute() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasCustomAttribute));
    [Fact] public void EncodeToken_ZeroRid_HasFieldMarshal() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasFieldMarshal));
    [Fact] public void EncodeToken_ZeroRid_HasDeclSecurity() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasDeclSecurity));
    [Fact] public void EncodeToken_ZeroRid_MemberRefParent() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.MemberRefParent));
    [Fact] public void EncodeToken_ZeroRid_HasSemantics() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasSemantics));
    [Fact] public void EncodeToken_ZeroRid_MethodDefOrRef() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.MethodDefOrRef));
    [Fact] public void EncodeToken_ZeroRid_MemberForwarded() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.MemberForwarded));
    [Fact] public void EncodeToken_ZeroRid_Implementation() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.Implementation));
    [Fact] public void EncodeToken_ZeroRid_CustomAttributeType() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.CustomAttributeType));
    [Fact] public void EncodeToken_ZeroRid_ResolutionScope() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.ResolutionScope));
    [Fact] public void EncodeToken_ZeroRid_TypeOrMethodDef() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.TypeOrMethodDef));
    [Fact] public void EncodeToken_ZeroRid_HasCustomDebugInformation() => Assert.Equal(0u, EncodeZeroRid(CodedIndex.HasCustomDebugInformation));

    static uint EncodeZeroRid(CodedIndex codedIndex) =>
        CodedIndexHelper.EncodeToken(codedIndex, new MetadataToken(TableIndex.TypeDef, 0));

    // -------------------------------------------------------------------------
    // DecodeToken — zero value returns zero-RID token for all coded index types
    // -------------------------------------------------------------------------

    [Fact] public void DecodeToken_ZeroValue_HasConstant() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasConstant, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_HasCustomAttribute() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasCustomAttribute, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_HasFieldMarshal() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasFieldMarshal, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_HasDeclSecurity() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasDeclSecurity, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_MemberRefParent() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.MemberRefParent, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_HasSemantics() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasSemantics, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_MethodDefOrRef() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.MethodDefOrRef, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_MemberForwarded() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.MemberForwarded, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_Implementation() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.Implementation, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_CustomAttributeType() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_ResolutionScope() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.ResolutionScope, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_TypeOrMethodDef() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.TypeOrMethodDef, 0).RID);
    [Fact] public void DecodeToken_ZeroValue_HasCustomDebugInformation() => Assert.Equal(0u, CodedIndexHelper.DecodeToken(CodedIndex.HasCustomDebugInformation, 0).RID);

    // -------------------------------------------------------------------------
    // DecodeToken — invalid tag throws
    // -------------------------------------------------------------------------

    // Tags 0 and 1 are placeholder 0xFF entries — should throw
    // Encoded value with tag 0 and RID 1: (1 << 3) | 0 = 8
    [Fact]
    public void DecodeToken_CustomAttributeType_InvalidTag0_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, 8));

    // Encoded value with tag 1 and RID 1: (1 << 3) | 1 = 9
    [Fact]
    public void DecodeToken_CustomAttributeType_InvalidTag1_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, 9));

    // HasConstant has 3 tables (tags 0, 1, 2); tag 3 is invalid
    // Encoded value with tag 3 and RID 1: (1 << 2) | 3 = 7
    [Fact]
    public void DecodeToken_HasConstant_InvalidTag_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.DecodeToken(CodedIndex.HasConstant, 7));

    // -------------------------------------------------------------------------
    // EncodeToken — invalid table throws
    // -------------------------------------------------------------------------

    [Fact]
    public void EncodeToken_HasConstant_InvalidTable_Throws()
    {
        var token = new MetadataToken(TableIndex.Assembly, 1);
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token));
    }

    [Fact]
    public void EncodeToken_HasSemantics_InvalidTable_Throws()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 1);
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.EncodeToken(CodedIndex.HasSemantics, token));
    }

    [Fact]
    public void EncodeToken_CustomAttributeType_InvalidTable_Throws()
    {
        // Module is tag 0 which is a placeholder — not a valid target
        var token = new MetadataToken(TableIndex.Module, 1);
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, token));
    }

    // -------------------------------------------------------------------------
    // Encoding correctness — verify the bit-level layout
    // -------------------------------------------------------------------------

    // HasConstant: Field=tag0, Param=tag1, Property=tag2 (2 bits)
    [Fact]
    public void Encode_HasConstant_Field_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.Field, 1);
        // (1 << 2) | 0 = 4
        Assert.Equal(4u, CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token));
    }

    [Fact]
    public void Encode_HasConstant_Property_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.Property, 1);
        // (1 << 2) | 2 = 6
        Assert.Equal(6u, CodedIndexHelper.EncodeToken(CodedIndex.HasConstant, token));
    }

    // CustomAttributeType: MethodDef=tag2, MemberRef=tag3 (3 bits)
    [Fact]
    public void Encode_CustomAttributeType_MethodDef_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 1);
        // (1 << 3) | 2 = 10
        Assert.Equal(10u, CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, token));
    }

    [Fact]
    public void Encode_CustomAttributeType_MemberRef_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.MemberRef, 1);
        // (1 << 3) | 3 = 11
        Assert.Equal(11u, CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, token));
    }

    // TypeOrMethodDef: TypeDef=tag0, MethodDef=tag1 (1 bit)
    [Fact]
    public void Encode_TypeOrMethodDef_TypeDef_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.TypeDef, 3);
        // (3 << 1) | 0 = 6
        Assert.Equal(6u, CodedIndexHelper.EncodeToken(CodedIndex.TypeOrMethodDef, token));
    }

    [Fact]
    public void Encode_TypeOrMethodDef_MethodDef_CorrectBits()
    {
        var token = new MetadataToken(TableIndex.MethodDef, 3);
        // (3 << 1) | 1 = 7
        Assert.Equal(7u, CodedIndexHelper.EncodeToken(CodedIndex.TypeOrMethodDef, token));
    }
}
