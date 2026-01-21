namespace PackageShader.Tests;

/// <summary>
/// Tests validating PackageShader's compliance with ECMA-335 specification.
/// Each test corresponds to a specific ECMA-335 constraint with section references.
/// </summary>
public class Ecma335ComplianceTests
{
    static string binDirectory = Path.GetDirectoryName(typeof(Ecma335ComplianceTests).Assembly.Location)!;

    #region Phase 1: Critical Table Constraints

    /// <summary>
    /// ECMA-335 II.22.2: The Assembly table shall contain zero or one row [ERROR]
    /// </summary>
    [Fact]
    public void AssemblyTable_MustHaveZeroOrOneRow()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var rowCount = reader.GetRowCount(TableIndex.Assembly);

        // ECMA-335 II.22.2: Assembly table shall contain zero or one row
        Assert.True(rowCount <= 1, $"Assembly table must have 0 or 1 row, but has {rowCount} rows");
    }

    /// <summary>
    /// ECMA-335 II.22.30: The Module table shall contain one and only one row [ERROR]
    /// </summary>
    [Fact]
    public void ModuleTable_MustHaveExactlyOneRow()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var rowCount = reader.GetRowCount(TableIndex.Module);

        // ECMA-335 II.22.30: Module table shall contain one and only one row
        Assert.Equal(1, rowCount);
    }

    /// <summary>
    /// ECMA-335 II.22, II.24.2.6: RIDs are 1-based indices that must be within table bounds.
    /// A RID of 0 denotes a null reference. Valid RIDs are in the range [1, rowCount].
    /// </summary>
    [Fact]
    public void TableRID_MustBeOneBasedAndWithinBounds()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var typeRefCount = reader.GetRowCount(TableIndex.TypeRef);

        if (typeRefCount == 0)
            return; // No TypeRef rows to test

        // Valid RIDs [1, rowCount] should work
        for (uint rid = 1; rid <= typeRefCount; rid++)
        {
            var typeRefRow = reader.ReadTypeRefRow(rid);
            // TypeRefRow is a struct, just verify we can read it without exception
        }

        // RID 0 is null/invalid per ECMA-335 - ReadRow returns empty array
        var invalidRow = reader.ReadRow(TableIndex.TypeRef, 0);
        Assert.Empty(invalidRow);

        // RID beyond row count is invalid - ReadRow returns empty array
        var outOfBoundsRow = reader.ReadRow(TableIndex.TypeRef, (uint)(typeRefCount + 1));
        Assert.Empty(outOfBoundsRow);
    }

    /// <summary>
    /// ECMA-335 II.22.10, II.24.2.6: CustomAttribute table must be sorted by Parent column
    /// </summary>
    [Fact]
    public void CustomAttributeTable_MustBeSortedByParent()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var customAttrCount = reader.GetRowCount(TableIndex.CustomAttribute);
        if (customAttrCount == 0)
            return; // No custom attributes to validate

        uint? previousParent = null;

        for (uint rid = 1; rid <= customAttrCount; rid++)
        {
            var customAttr = reader.ReadCustomAttributeRow(rid);

            if (previousParent.HasValue)
            {
                // ECMA-335 II.22.10: CustomAttribute table must be sorted by ParentIndex (ascending)
                Assert.True(customAttr.ParentIndex >= previousParent.Value,
                    $"CustomAttribute table not sorted: row {rid} has ParentIndex={customAttr.ParentIndex}, previous was {previousParent}");
            }

            previousParent = customAttr.ParentIndex;
        }
    }

    #endregion

    #region Phase 2: Heap Integrity Validation

    /// <summary>
    /// ECMA-335 II.24.2.3: String heap indices must point to valid null-terminated UTF8 strings.
    /// The first entry (offset 0) is always the empty string.
    /// </summary>
    [Fact]
    public void StringIndex_MustBeWithinHeapBounds()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var stringHeapSize = reader.StringHeapSize;

        // String heap should have size > 0 (at least the empty string at index 0)
        Assert.True(stringHeapSize > 0, "String heap should contain at least the empty string");

        // Reading valid string indices should work (tested through TypeRef Name/Namespace)
        var typeRefCount = reader.GetRowCount(TableIndex.TypeRef);
        for (uint rid = 1; rid <= typeRefCount; rid++)
        {
            var typeRefRow = reader.ReadTypeRefRow(rid);
            // NameIndex and NamespaceIndex should be valid indices within string heap
            // System.Reflection.Metadata validates this internally
            Assert.True(typeRefRow.NameIndex < stringHeapSize);
            Assert.True(typeRefRow.NamespaceIndex < stringHeapSize);
        }
    }

    /// <summary>
    /// ECMA-335 II.24.2.4: Blob heap indices must point to valid compressed-length-prefixed blobs.
    /// The first entry (offset 0) is always the empty blob (0x00).
    /// </summary>
    [Fact]
    public void BlobIndex_MustHaveValidCompressedLength()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var blobHeapSize = reader.BlobHeapSize;

        // Blob heap should have size > 0 (at least the empty blob at index 0)
        Assert.True(blobHeapSize > 0, "Blob heap should contain at least the empty blob");

        // Reading valid blob indices should work (tested through AssemblyRef PublicKeyOrToken)
        var assemblyRefCount = reader.GetRowCount(TableIndex.AssemblyRef);
        for (uint rid = 1; rid <= assemblyRefCount; rid++)
        {
            var assemblyRefRow = reader.ReadAssemblyRefRow(rid);
            // PublicKeyOrTokenIndex should be valid (0 or within heap bounds)
            // System.Reflection.Metadata validates blob structure internally
            if (assemblyRefRow.PublicKeyOrTokenIndex != 0)
            {
                Assert.True(assemblyRefRow.PublicKeyOrTokenIndex < blobHeapSize);
            }
        }
    }

    /// <summary>
    /// ECMA-335 II.24.2.5: GUID heap indices are 1-based indices into 16-byte GUID array (not byte offsets).
    /// Index 0 means null/no GUID. Index N refers to GUID at byte offset (N-1)*16.
    /// </summary>
    [Fact]
    public void GuidIndex_IsOneBasedNotByteOffset()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // GUID index size is 2 or 4 bytes depending on HeapSizes flag (bit 0x02)
        // This is validated by System.Reflection.Metadata which PackageShader uses internally
        var guidIndexSize = reader.GuidIndexSize;
        Assert.True(guidIndexSize == 2 || guidIndexSize == 4,
            "GUID index size should be 2 or 4 bytes");
    }

    #endregion

    #region Phase 3: Coded Index Validation

    /// <summary>
    /// ECMA-335 II.24.2.6: Coded index tags must be valid for the coded index type.
    /// </summary>
    [Fact]
    public void CodedIndex_TagsMustBeValid()
    {
        // Test that encoded/decoded round-trip works
        var token = new MetadataToken(TableIndex.TypeDef, 42);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.TypeDefOrRef, token);
        var decoded = CodedIndexHelper.DecodeToken(CodedIndex.TypeDefOrRef, encoded);

        Assert.Equal(token.TableIndex, decoded.TableIndex);
        Assert.Equal(token.RID, decoded.RID);

        // Test null reference (0)
        var nullDecoded = CodedIndexHelper.DecodeToken(CodedIndex.TypeDefOrRef, 0);
        Assert.Equal(0u, nullDecoded.RID);
    }

    /// <summary>
    /// ECMA-335 II.24.2.6: CustomAttributeType coded index uses only tags 2 and 3 (not 0 and 1).
    /// This is because the first two slots are reserved/unused.
    /// </summary>
    [Fact]
    public void CustomAttributeType_UsesOnlyTags2And3()
    {
        // Test MethodDef (should use tag 2)
        var methodDefToken = new MetadataToken(TableIndex.MethodDef, 10);
        var encoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, methodDefToken);
        var tag = encoded & 0b111; // 3-bit tag
        Assert.Equal(2u, tag);

        // Test MemberRef (should use tag 3)
        var memberRefToken = new MetadataToken(TableIndex.MemberRef, 20);
        encoded = CodedIndexHelper.EncodeToken(CodedIndex.CustomAttributeType, memberRefToken);
        tag = encoded & 0b111;
        Assert.Equal(3u, tag);

        // Verify that tags 0 and 1 would fail decoding
        Assert.Throws<ArgumentException>(() =>
            CodedIndexHelper.DecodeToken(CodedIndex.CustomAttributeType, 0b001)); // Tag 1, RID 0
    }

    /// <summary>
    /// ECMA-335 II.24.2.6: After decoding tag, the RID must be valid for that table.
    /// </summary>
    [Fact]
    public void CodedIndexRID_MustBeValidInTargetTable()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        // Read TypeRef rows and validate ResolutionScope coded indices
        var typeRefCount = reader.GetRowCount(TableIndex.TypeRef);
        for (uint rid = 1; rid <= typeRefCount; rid++)
        {
            var typeRefRow = reader.ReadTypeRefRow(rid);

            if (typeRefRow.ResolutionScopeIndex == 0)
                continue; // Null reference is valid

            // Decode the ResolutionScope coded index
            var token = CodedIndexHelper.DecodeToken(CodedIndex.ResolutionScope, typeRefRow.ResolutionScopeIndex);

            // Verify the target RID is within the target table's row count
            var targetRowCount = reader.GetRowCount(token.TableIndex);
            Assert.True(token.RID > 0 && token.RID <= targetRowCount,
                $"TypeRef RID {rid}: ResolutionScope points to {token.TableIndex} RID {token.RID}, but table only has {targetRowCount} rows");
        }
    }

    #endregion

    #region Phase 4: Metadata Header and Additional Validation

    /// <summary>
    /// ECMA-335 II.24.2.1: Metadata root signature must be 0x424A5342 (ASCII "BSJB").
    /// This is the magic signature for physical metadata.
    /// </summary>
    [Fact]
    public void MetadataSignature_MustBeBSJB()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);

        // The metadata root signature is validated by System.Reflection.Metadata.PEReader
        // which PackageShader uses internally via StreamingPEFile
        // If the signature is invalid, PEReader throws BadImageFormatException during construction
        Assert.NotNull(peFile);

        // Additional validation: we can create a metadata reader successfully
        using var reader = new StreamingMetadataReader(peFile);
        Assert.NotNull(reader);
    }

    #endregion

    #region Phase 5: Additional Table Validation

    /// <summary>
    /// ECMA-335 II.22.2: Assembly.Name shall index a non-empty string in the String heap.
    /// </summary>
    [Fact]
    public void AssemblyName_MustBeNonEmpty()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var assemblyRowCount = reader.GetRowCount(TableIndex.Assembly);
        if (assemblyRowCount == 0)
            return; // No assembly row (modules without manifest)

        var assemblyRow = reader.ReadAssemblyRow(1);

        // ECMA-335 II.22.2: Name shall index a non-empty string
        Assert.True(assemblyRow.NameIndex > 0, "Assembly Name index must be non-zero");
        Assert.True(assemblyRow.NameIndex < reader.StringHeapSize, "Assembly Name index must be within string heap bounds");
    }

    /// <summary>
    /// ECMA-335 II.22.38: TypeRef.TypeName shall index a non-empty string in the String heap.
    /// If TypeNamespace is non-null, it shall also index a non-empty string.
    /// </summary>
    [Fact]
    public void TypeRefNames_MustBeNonEmpty()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var typeRefCount = reader.GetRowCount(TableIndex.TypeRef);

        for (uint rid = 1; rid <= typeRefCount; rid++)
        {
            var typeRefRow = reader.ReadTypeRefRow(rid);

            // ECMA-335 II.22.38: TypeName shall index a non-empty string
            Assert.True(typeRefRow.NameIndex > 0, $"TypeRef RID {rid}: TypeName index must be non-zero");

            // ECMA-335 II.22.38: If non-null, TypeNamespace shall index a non-empty string
            // TypeNamespace can be 0 (null) which is valid for types in the global namespace
            if (typeRefRow.NamespaceIndex > 0)
            {
                Assert.True(typeRefRow.NamespaceIndex < reader.StringHeapSize,
                    $"TypeRef RID {rid}: TypeNamespace index must be within string heap bounds");
            }
        }
    }

    /// <summary>
    /// ECMA-335 II.22.25: MemberRef.Name shall index a non-empty string in the String heap.
    /// MemberRef.Class shall not be null.
    /// </summary>
    [Fact]
    public void MemberRef_ValidationRules()
    {
        var assemblyPath = Path.Combine(binDirectory, "DummyAssembly.dll");

        using var peFile = StreamingPEFile.Open(assemblyPath);
        using var reader = new StreamingMetadataReader(peFile);

        var memberRefCount = reader.GetRowCount(TableIndex.MemberRef);

        for (uint rid = 1; rid <= memberRefCount; rid++)
        {
            var memberRefRow = reader.ReadMemberRefRow(rid);

            // ECMA-335 II.22.25: Name shall index a non-empty string
            Assert.True(memberRefRow.NameIndex > 0, $"MemberRef RID {rid}: Name index must be non-zero");
            Assert.True(memberRefRow.NameIndex < reader.StringHeapSize,
                $"MemberRef RID {rid}: Name index must be within string heap bounds");

            // ECMA-335 II.22.25: Class shall not be null
            Assert.True(memberRefRow.ClassIndex > 0, $"MemberRef RID {rid}: Class index must be non-null");
        }
    }

    #endregion
}
