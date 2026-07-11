// A count or length read straight off the wire used to presize a buffer with no cross-check against the
// bytes actually present, so a few dozen input bytes could demand a multi-gigabyte allocation — a 32-byte
// .dbf declaring 0x40000000 records reserved 8 GiB before the first read went out of bounds. Every reader
// now bounds the declared value by what the input can actually hold, the way Snappy.Decompress already
// bounded its declared block size.
//
// Where the reader wraps *any* exception as GeoConvertException (WKB, FlatGeobuf), these tests assert on
// the message: the type alone passed even before the bound existed, because the catch-all swallowed the
// ArgumentOutOfRangeException (negative capacity) or OutOfMemoryException the presize used to raise. The
// GeoParquet bounds are pinned the same way in GeoParquetCraftTests, next to that codec's craft helper.
public class AllocationBoundsTests
{
    // Dbf.Read is not wrapped by a catch-all, so a bounded reader is the difference between
    // GeoConvertException and an IndexOutOfRangeException escaping to the caller.
    [Test]
    public async Task Dbf_record_count_is_bounded_by_the_bytes_present()
    {
        // The reported repro: a bare 32-byte header, no field terminator, recordCount = 0x40000000.
        var data = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x40000000);

        await Assert.That(TestSupport.ThrowsGeo(() => Dbf.Read(new MemoryStream(data)))).IsTrue();
    }

    [Test]
    public async Task Dbf_shorter_than_its_header_is_rejected() =>
        await Assert.That(TestSupport.ThrowsGeo(() => Dbf.Read(new MemoryStream(new byte[31])))).IsTrue();

    [Test]
    public async Task Dbf_truncated_field_descriptor_is_rejected()
    {
        // A 32-byte header then 10 bytes of what should be a 32-byte field descriptor. The leading byte
        // is not the 0x0D terminator, so the reader enters the descriptor branch and runs out of input.
        var data = new byte[42];
        data[32] = (byte)'A';

        await Assert.That(TestSupport.ThrowsGeo(() => Dbf.Read(new MemoryStream(data)))).IsTrue();
    }

    [Test]
    public async Task Dbf_reads_the_records_present_when_the_declared_count_overstates_them()
    {
        // uint.MaxValue used to reach `new List<object?[]>(-1)`. The count now clamps to the records the
        // file can hold, so a stale or hostile header degrades to reading what is actually there.
        var data = NameDbf(declaredRecords: uint.MaxValue, "ab");

        var (names, rows) = Dbf.Read(new MemoryStream(data));

        await Assert.That(names).IsEquivalentTo(["NAME"]);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo("ab");
    }

    [Test]
    public async Task Dbf_declared_count_below_the_records_present_still_wins()
    {
        // The clamp is a ceiling, not a floor: a header declaring fewer records than the file carries must
        // keep reading only what it declared, exactly as before.
        var data = NameDbf(declaredRecords: 1, "ab", "cd");

        var (_, rows) = Dbf.Read(new MemoryStream(data));

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0][0]).IsEqualTo("ab");
    }

    [Test]
    public async Task Dbf_records_exactly_filling_the_file_are_all_read()
    {
        // Both records sit right at the clamp: (length - headerEnd) / recordLength == 2. An off-by-one in
        // the bound would silently drop the last row of every shapefile.
        var data = NameDbf(declaredRecords: 2, "ab", "cd");

        var (_, rows) = Dbf.Read(new MemoryStream(data));

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[1][0]).IsEqualTo("cd");
    }

    [Test]
    public async Task Dbf_with_no_fields_reads_no_columns()
    {
        // The terminator sits immediately after the header, so recordLength is the lone deletion flag.
        var data = new byte[34];
        data[0] = 0x03;
        data[32] = 0x0D;
        data[33] = 0x1A;

        var (names, rows) = Dbf.Read(new MemoryStream(data));

        await Assert.That(names).IsEmpty();
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task Shapefile_surfaces_a_corrupt_dbf_as_GeoConvertException()
    {
        // Dbf.Read is the one read path with no catch-all wrapper, so its own guards are what keep a raw
        // IndexOutOfRangeException from escaping the public Shapefile API.
        using var shp = new MemoryStream(new byte[100]);
        using var dbf = new MemoryStream(new byte[4]);

        await Assert.That(TestSupport.ThrowsGeo(() => Shapefile.Read(shp, dbf))).IsTrue();
    }

    // header(32) + one 'NAME' C(2) field descriptor(32) + terminator(1) + one record(1 + 2) each + EOF(1).
    static byte[] NameDbf(uint declaredRecords, params string[] records)
    {
        var data = new byte[65 + records.Length * 3 + 1];
        data[0] = 0x03;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), declaredRecords);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 65);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 3);

        Encoding.Latin1.GetBytes("NAME").CopyTo(data, 32);
        data[43] = (byte)'C';
        data[48] = 2;
        data[64] = 0x0D;

        var position = 65;
        foreach (var record in records)
        {
            // not deleted, then the two-character cell
            data[position] = 0x20;
            Encoding.Latin1.GetBytes(record).CopyTo(data, position + 1);
            position += 3;
        }

        data[position] = 0x1A;
        return data;
    }

    [Test]
    [Arguments(2, "coordinates")]
    [Arguments(3, "rings")]
    [Arguments(4, "points")]
    [Arguments(5, "line strings")]
    [Arguments(6, "polygons")]
    [Arguments(7, "geometries")]
    public async Task Wkb_element_count_is_bounded_by_the_bytes_remaining(int type, string element)
    {
        // Nine bytes declaring 100 million elements. A MultiPoint's List<Position> presize alone is 4.8 GB.
        var message = TestSupport.GeoMessage(() => Wkb.ParseGeometry(WkbCount((uint)type, 100_000_000)));

        await Assert.That(message).Contains($"declares 100000000 {element} but only 0 bytes remain");
    }

    [Test]
    public async Task Wkb_element_count_that_casts_negative_is_bounded()
    {
        // 0xFFFFFFFF cast to int is -1, so the presize threw ArgumentOutOfRangeException from List's
        // constructor rather than reaching the bound at all.
        var message = TestSupport.GeoMessage(() => Wkb.ParseGeometry(WkbCount(2, uint.MaxValue)));

        await Assert.That(message).Contains("declares 4294967295 coordinates");
    }

    [Test]
    public async Task Wkb_big_endian_element_count_is_bounded()
    {
        // The count is read through the same endianness switch as everything else, so the bound has to
        // apply to the big-endian (XDR) branch too.
        var bytes = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), 0x40000000);

        await Assert.That(TestSupport.GeoMessage(() => Wkb.ParseGeometry(bytes)))
            .Contains("declares 1073741824 coordinates");
    }

    // The bound must not reject a geometry whose declared count is exactly what the bytes hold — a bound
    // one too tight would reject real files rather than hostile ones.
    [Test]
    public async Task Wkb_coordinate_count_exactly_filling_the_buffer_still_parses()
    {
        var line = (LineString)Wkb.ParseGeometry(WkbLine(2, 2, coordinateBytes: 16));

        await Assert.That(line.Positions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Wkb_coordinate_count_one_past_the_buffer_is_rejected()
    {
        // Three coordinates declared, two coordinates' worth of bytes supplied.
        var bytes = WkbLine(2, 2, coordinateBytes: 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 3);

        await Assert.That(TestSupport.GeoMessage(() => Wkb.ParseGeometry(bytes)))
            .Contains("declares 3 coordinates but only 32 bytes remain");
    }

    // A Z and/or M ordinate widens a coordinate to 24 or 32 bytes. If the bound assumed a flat 16 it would
    // let a hostile XYZM count through; if it assumed 32 it would reject every plain XY file.
    [Test]
    [Arguments(1002u, 24)]
    [Arguments(2002u, 24)]
    [Arguments(3002u, 32)]
    public async Task Wkb_zm_coordinates_are_measured_at_their_real_width(uint type, int coordinateBytes)
    {
        var line = (LineString)Wkb.ParseGeometry(WkbLine(type, 1, coordinateBytes));
        await Assert.That(line.Positions.Count).IsEqualTo(1);

        // One coordinate's worth of bytes, but two declared.
        var bytes = WkbLine(type, 1, coordinateBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 2);

        await Assert.That(TestSupport.GeoMessage(() => Wkb.ParseGeometry(bytes)))
            .Contains($"declares 2 coordinates but only {coordinateBytes} bytes remain");
    }

    [Test]
    public async Task Wkb_ring_count_exactly_filling_the_buffer_still_parses()
    {
        // Two rings, each encoded as nothing but its own 4-byte (zero) coordinate count.
        var polygon = (Polygon)Wkb.ParseGeometry(WkbEmptyRings(2));

        await Assert.That(polygon.Rings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Wkb_ring_count_one_past_the_buffer_is_rejected()
    {
        var bytes = WkbEmptyRings(2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 3);

        await Assert.That(TestSupport.GeoMessage(() => Wkb.ParseGeometry(bytes)))
            .Contains("declares 3 rings but only 8 bytes remain");
    }

    [Test]
    public async Task Wkb_empty_geometry_declares_zero_elements_and_parses()
    {
        // count == 0 with zero bytes remaining sits exactly on the bound; `LINESTRING EMPTY` must survive.
        var line = (LineString)Wkb.ParseGeometry(WkbCount(2, 0));

        await Assert.That(line.Positions).IsEmpty();
    }

    // Byte order + geometry type + an element count, with nothing following it.
    static byte[] WkbCount(uint type, uint count)
    {
        var bytes = new byte[9];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), count);
        return bytes;
    }

    // A LineString header followed by exactly `count` coordinates of `coordinateBytes` each.
    static byte[] WkbLine(uint type, uint count, int coordinateBytes)
    {
        var bytes = new byte[9 + count * coordinateBytes];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), count);
        return bytes;
    }

    // A Polygon header, a ring count, then that many 4-byte zero coordinate counts.
    static byte[] WkbEmptyRings(uint ringCount)
    {
        var bytes = new byte[9 + ringCount * 4];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), ringCount);
        return bytes;
    }

    [Test]
    public async Task FlatGeobuf_vector_length_is_bounded_by_the_buffer()
    {
        var bytes = CraftFlatGeobuf(
            builder =>
            {
                var xy = builder.CreateDoubleVector([1, 2]);
                builder.StartTable(8);
                builder.AddOffset(geometryXyField, xy);
                // Point
                builder.AddByte(geometryTypeField, 1, 0);
                return builder.EndTable();
            });

        // Two ordinates on the wire, but the vector claims 200 million — a 4.8 GB List<Position> presize.
        PatchXyVectorLength(bytes, 200_000_000);

        var message = TestSupport.GeoMessage(() => ReadFlatGeobuf(bytes));

        await Assert.That(message).Contains("declares 200000000 elements of 8 bytes");
    }

    [Test]
    public async Task FlatGeobuf_negative_vector_length_is_rejected()
    {
        var bytes = CraftFlatGeobuf(PointGeometry);

        // A negative length reached `new List<Position>(-1)` — ArgumentOutOfRangeException, not the
        // documented type, and the multiplication by the element size would overflow rather than bound.
        PatchXyVectorLength(bytes, -1);

        var message = TestSupport.GeoMessage(() => ReadFlatGeobuf(bytes));

        await Assert.That(message).Contains("declares -1 elements of 8 bytes");
    }

    [Test]
    public async Task FlatGeobuf_ring_end_is_bounded_by_the_coordinates_present()
    {
        var bytes = CraftFlatGeobuf(builder => RingGeometry(builder, [1_000_000_000u]));

        var message = TestSupport.GeoMessage(() => ReadFlatGeobuf(bytes));

        await Assert.That(message).Contains("ring end 1000000000 must fall between the previous ring's end (0) and the 4 coordinates");
    }

    [Test]
    public async Task FlatGeobuf_ring_ends_must_not_move_backwards()
    {
        // A second end below the first left `end - start` negative — a negative List capacity rather than
        // a bounded one. Ends index into one shared xy vector, so they can only climb.
        var bytes = CraftFlatGeobuf(builder => RingGeometry(builder, [4u, 2u]));

        var message = TestSupport.GeoMessage(() => ReadFlatGeobuf(bytes));

        await Assert.That(message).Contains("ring end 2 must fall between the previous ring's end (4)");
    }

    [Test]
    public async Task FlatGeobuf_ring_ends_reaching_the_last_coordinate_still_parse()
    {
        // Two rings that between them consume exactly the four coordinates present: the bound's upper edge.
        var bytes = CraftFlatGeobuf(builder => RingGeometry(builder, [2u, 4u]));

        var polygon = (Polygon)ReadFlatGeobuf(bytes).Features[0].Geometry!;

        await Assert.That(polygon.Rings.Count).IsEqualTo(2);
        await Assert.That(polygon.Rings[0].Count).IsEqualTo(2);
        await Assert.That(polygon.Rings[1].Count).IsEqualTo(2);
    }

    [Test]
    public async Task FlatGeobuf_property_string_length_is_bounded_by_the_blob()
    {
        // A property blob holding column index 0 and a string length of int.MaxValue, with no bytes after
        // it. BinaryReader.ReadBytes presizes from that count.
        var bytes = CraftFlatGeobuf(PointGeometry, column: "name", properties: PropertyBlob(int.MaxValue, ""));

        var message = TestSupport.GeoMessage(() => ReadFlatGeobuf(bytes));

        await Assert.That(message).Contains("declares 2147483647 bytes but only 0 remain");
    }

    [Test]
    public async Task FlatGeobuf_property_string_exactly_filling_the_blob_still_reads()
    {
        // The declared length equals the bytes left, which is what every real feature looks like.
        var bytes = CraftFlatGeobuf(PointGeometry, column: "name", properties: PropertyBlob(3, "abc"));

        var feature = ReadFlatGeobuf(bytes).Features[0];

        await Assert.That(feature.Properties["name"]).IsEqualTo("abc");
    }

    // Column index 0, a declared string length, then `text` — the FlatGeobuf property encoding.
    static byte[] PropertyBlob(int declaredLength, string text)
    {
        var blob = new byte[6 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(2), (uint)declaredLength);
        Encoding.UTF8.GetBytes(text).CopyTo(blob, 6);
        return blob;
    }

    static int PointGeometry(FlatBufferBuilder builder)
    {
        var xy = builder.CreateDoubleVector([1, 2]);
        builder.StartTable(8);
        builder.AddOffset(geometryXyField, xy);
        // Point
        builder.AddByte(geometryTypeField, 1, 0);
        return builder.EndTable();
    }

    // A Polygon over four coordinates, split into rings at the given `ends`.
    static int RingGeometry(FlatBufferBuilder builder, uint[] ends)
    {
        var xy = builder.CreateDoubleVector([0, 0, 1, 0, 1, 1, 0, 0]);
        var endsVector = builder.CreateUIntVector(ends);
        builder.StartTable(8);
        builder.AddOffset(geometryEndsField, endsVector);
        builder.AddOffset(geometryXyField, xy);
        // Polygon
        builder.AddByte(geometryTypeField, 3, 0);
        return builder.EndTable();
    }

    // FlatGeobuf schema field indexes, mirrored from the codec so the crafted tables line up with it.
    const int featureGeometryField = 0;
    const int featurePropertiesField = 1;
    const int geometryEndsField = 0;
    const int geometryXyField = 1;
    const int geometryTypeField = 6;
    const int headerColumnsField = 7;
    const int headerIndexNodeSizeField = 9;
    const int columnNameField = 0;
    const int columnTypeField = 1;
    const int columnStringType = 11;

    // magic + a size-prefixed header (no spatial index) + one size-prefixed feature whose geometry
    // sub-table `geometry` builds. Crafting through the builder rather than patching a written file keeps
    // the offsets honest — only the specific field under test is a lie.
    static byte[] CraftFlatGeobuf(
        Func<FlatBufferBuilder, int> geometry,
        string? column = null,
        byte[]? properties = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0x66, 0x67, 0x62, 0x03, 0x66, 0x67, 0x62, 0x00]);

        var builder = new FlatBufferBuilder();
        var columnsVector = 0;
        if (column != null)
        {
            var name = builder.CreateString(column);
            builder.StartTable(11);
            builder.AddOffset(columnNameField, name);
            builder.AddByte(columnTypeField, columnStringType, 0);
            columnsVector = builder.CreateOffsetVector([builder.EndTable()]);
        }

        builder.StartTable(14);
        builder.AddOffset(headerColumnsField, columnsVector);
        // 0 => no spatial index to skip
        builder.AddUShort(headerIndexNodeSizeField, 0, 16);
        builder.FinishSizePrefixed(builder.EndTable(), stream);
        builder.Reset();

        var geometryOffset = geometry(builder);
        var propertiesOffset = properties == null ? 0 : builder.CreateByteVector(properties);
        builder.StartTable(3);
        builder.AddOffset(featureGeometryField, geometryOffset);
        builder.AddOffset(featurePropertiesField, propertiesOffset);
        builder.FinishSizePrefixed(builder.EndTable(), stream);
        return stream.ToArray();
    }

    // Overwrites the element count of the geometry's xy vector, walking the tables the way
    // FlatBufferTable does: past the magic and header to the feature root, into the geometry sub-table,
    // then to the vector's 4-byte length prefix.
    static void PatchXyVectorLength(byte[] bytes, int value)
    {
        var feature = 8 + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var root = feature + 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(feature + 4));
        var geometrySlot = root + FieldOffset(bytes, root, featureGeometryField);
        var geometry = geometrySlot + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(geometrySlot));
        var xySlot = geometry + FieldOffset(bytes, geometry, geometryXyField);
        var xyVector = xySlot + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(xySlot));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(xyVector), value);
    }

    static int FieldOffset(byte[] bytes, int table, int field)
    {
        var vtable = table - BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(table));
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(vtable + 4 + field * 2));
    }

    static FeatureCollection ReadFlatGeobuf(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return FlatGeobuf.Read(stream);
    }
}
